// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Garnet.test;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using Tsavorite.core;
using static Tsavorite.test.TestUtils;

namespace Tsavorite.test
{
    using StructAllocator = SpanByteAllocator<StoreFunctions<KeyStruct.Comparer, SpanByteRecordTriggers>>;
    using StructStoreFunctions = StoreFunctions<KeyStruct.Comparer, SpanByteRecordTriggers>;

    /// <summary>
    /// Ziltch router 11-01 16:3xZ: 2,097,152 buckets for 46k hot keys, and every halving refused by the same 3 same-tag pairs: a busy read cache
    /// heads them again by every ask, and a join refused any side whose head is a read-cache copy. A join now drops a side's read-cache prefix
    /// first, the way a writer's detach does (one CAS of the entry onto its chain's first main-log address), and joins the main-log chains. A read
    /// that caches a key on one side between the join and the halving is resolved at the barrier the same way. Every key reads back.
    /// Mutants: no splice (the join refuses as OnDisk); the barrier compares read-cache words as they are (the halving is refused).
    /// </summary>
    [TestFixture]
    internal class APairHeadedByReadCacheCopiesIsJoinedAndHalvesTests : TestBase
    {
        private const long IndexBytes = 1L << 12;   // 64 buckets: a same-tag pair across i / i + 32 is a short search

        private TsavoriteKV<StructStoreFunctions, StructAllocator> store;
        private IDevice log;

        private sealed class Quiet : Functions
        {
            public override void ReadCompletionCallback(ref DiskLogRecord diskLogRecord, ref InputStruct input, ref OutputStruct output, Empty ctx, Status status, RecordMetadata recordMetadata) { }
        }

        [SetUp]
        public void Setup()
        {
            DeleteDirectory(MethodTestDir, wait: true);
            log = Devices.CreateLogDevice(Path.Join(MethodTestDir, "rcjoin.log"), deleteOnClose: true);
            store = new(new KVSettings { IndexSize = IndexBytes, LogDevice = log, PageSize = 1L << 12, LogMemorySize = 1L << 22, SegmentSize = 1L << 22,
                                         ReadCacheEnabled = true, ReadCacheMemorySize = 1L << 20, ReadCachePageSize = 1L << 12 }
                , StoreFunctions.Create(KeyStruct.Comparer.Instance, SpanByteRecordTriggers.Instance)
                , (allocatorSettings, storeFunctions) => new(allocatorSettings, storeFunctions));
        }

        [TearDown]
        public void TearDown()
        {
            store?.Dispose(); store = null;
            log?.Dispose(); log = null;
            OnTearDown();
        }

        private static KeyStruct Key(int i) => new() { kfield1 = i, kfield2 = i };

        private static (long Bucket, ushort Tag) Place(int i, long size) { var hash = Utility.GetHashCode((long)i); return (hash & (size - 1), HashBucketEntry.GetTag(hash)); }

        // Pairs whose low side and high side (bucket + half) each hold three keys under one tag, each cell its own.
        private static List<(int[] Low, int[] High)> Pairs(long size, int want)
        {
            var half = size / 2;
            var cells = new Dictionary<(long, ushort), List<int>>();
            for (var i = 0; i < 4_000_000; i++)
            {
                var (bucket, tag) = Place(i, size);
                if (!cells.TryGetValue((bucket, tag), out var keys)) cells[(bucket, tag)] = keys = [];
                keys.Add(i);
            }
            var pairs = new List<(int[] Low, int[] High)>();
            var used = new HashSet<long>();
            foreach (var ((bucket, tag), keys) in cells)
            {
                if (bucket >= half || keys.Count < 3 || !cells.TryGetValue((bucket + half, tag), out var high) || high.Count < 3 || !used.Add(bucket)) continue;
                pairs.Add(([keys[0], keys[1], keys[2]], [high[0], high[1], high[2]]));
                if (pairs.Count == want) break;
            }
            return pairs;
        }

        private static (bool Found, long Value) Read(BasicContext<KeyStruct, InputStruct, OutputStruct, Empty, Quiet, StructStoreFunctions, StructAllocator> context, int i)
        {
            InputStruct input = default;
            OutputStruct output = default;
            var status = context.Read(Key(i), ref input, ref output);
            if (status.IsPending)
            {
                _ = context.CompletePendingWithOutputs(out var outputs, wait: true);
                (status, output) = GetSinglePendingResult(outputs);
            }
            return (status.Found, status.Found ? output.value.vfield2 : -1);
        }

        private static void Write(BasicContext<KeyStruct, InputStruct, OutputStruct, Empty, Quiet, StructStoreFunctions, StructAllocator> context, int i, int generation)
        {
            var value = new ValueStruct { vfield1 = i, vfield2 = i * 10 + generation };
            _ = context.Upsert(Key(i), SpanByte.FromPinnedVariable(ref value), Empty.Default);
        }

        private List<IndexTagCollision> Collisions()
        {
            var found = new List<IndexTagCollision>();
            var step = store.FindHalvingCollisions(0, 1L << 40, found, int.MaxValue);
            ClassicAssert.IsFalse(step.Refused, "CONTROL: no resize is in flight");
            return found;
        }

        private static bool IsReadCache(long word) { HashBucketEntry entry = default; entry.word = word; return entry.IsReadCache; }

        [Test]
        [Category("TsavoriteKV")]
        [Category("Index")]
        [Category("ReadCache")]
        public async Task A_pair_headed_by_read_cache_copies_is_joined_and_halves_and_every_key_reads_back()
        {
            using var session = store.NewSession<KeyStruct, InputStruct, OutputStruct, Empty, Quiet>(new Quiet());
            var context = session.BasicContext;
            var size = store.IndexSize;
            var pairs = Pairs(size, 4);
            ClassicAssert.AreEqual(4, pairs.Count, "CONTROL: the search finds four pairs");
            var keys = pairs.SelectMany(p => p.Low.Concat(p.High)).ToList();
            foreach (var i in keys) Write(context, i, 0);
            foreach (var i in keys) Write(context, i, 1);
            var deleted = pairs[0].Low[1];
            _ = context.Delete(Key(deleted));
            store.Log.FlushAndEvict(wait: true);
            foreach (var i in keys) _ = Read(context, i);   // each read from the device leaves a read-cache copy at its side's head
            var collisions = Collisions();
            ClassicAssert.AreEqual(4, collisions.Count, "CONTROL: each pair is a same-tag pair of two chains");
            ClassicAssert.AreEqual(4, collisions.Count(c => IsReadCache(c.LowWord) && IsReadCache(c.HighWord)), "CONTROL: both sides of every pair are headed by read-cache copies");

            var joins = collisions.Select(store.JoinHalvingPairFromDevice).ToList();
            TestContext.Progress.WriteLine(string.Join("; ", joins) + $"; splices {store.HalvingReadCacheSplices}");
            ClassicAssert.IsTrue(joins.All(j => j.Outcome == HalvingJoinOutcome.Joined),
                $"every pair joins once its read-cache prefix is dropped. MUTANT (no splice): OnDisk. {string.Join("; ", joins)}");
            ClassicAssert.AreEqual(8, store.HalvingReadCacheSplices, "each side's prefix is dropped once");
            foreach (var i in keys)
                ClassicAssert.AreEqual(i == deleted ? (false, -1L) : (true, i * 10L + 1), Read(context, i), $"key {i} reads its newest after the join");
            ClassicAssert.IsTrue(Collisions().Any(c => IsReadCache(c.LowWord) != IsReadCache(c.HighWord)),
                "CONTROL: a read after the join cached a key on one side of a joined pair: one side is a read-cache word, the other the joined word");

            var h = await store.HalveIndexAsync();
            ClassicAssert.IsTrue(h.Halved, $"a read-cache head on one side of a joined pair is its main-log word at the barrier. MUTANT2 (compared as it is): {h.Refusal}");
            ClassicAssert.AreEqual(size / 2, store.IndexSize);
            foreach (var i in keys)
                ClassicAssert.AreEqual(i == deleted ? (false, -1L) : (true, i * 10L + 1), Read(context, i), $"key {i} reads its newest after the halving");
            store.ReadCache.FlushAndEvict(wait: true);   // the dropped copies' pages close: the eviction walks a chain that no longer holds them
            foreach (var i in keys)
                ClassicAssert.AreEqual(i == deleted ? (false, -1L) : (true, i * 10L + 1), Read(context, i), $"key {i} reads its newest after the read cache is evicted");
            Write(context, keys[0], 7);
            ClassicAssert.AreEqual((true, keys[0] * 10L + 7), Read(context, keys[0]), "the halved table takes writes");
        }
    }
}
