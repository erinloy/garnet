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
    /// Ziltch mesh 11-01 08:5xZ: 33,554,432 buckets (4.3 GB) for 386k hot keys, and every halving refused by 46,023 same-tag pairs whose chains lie on
    /// the device. A pair on the device is joined by reading it: each side's chain is walked from the observed word to begin, and one side's newest
    /// record of each key (a tombstone too) is copied onto the other's head by CAS on that word. Every key reads back after the halving; a write to the
    /// copied side while the walk reads is refused at the CAS (Raced) and kept, and the next ask joins again.
    /// Mutants: the walk stops at head (a side's keys on the device are lost); the swap does not compare the word (the racing write is lost).
    /// </summary>
    [TestFixture]
    internal class ASameTagPairOnTheDeviceIsJoinedByReadingItTests : TestBase
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
            log = Devices.CreateLogDevice(Path.Join(MethodTestDir, "diskjoin.log"), deleteOnClose: true);
            store = new(new KVSettings { IndexSize = IndexBytes, LogDevice = log, PageSize = 1L << 12, LogMemorySize = 1L << 22, SegmentSize = 1L << 22 }
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

        [Test]
        [Category("TsavoriteKV")]
        [Category("Index")]
        public async Task A_pair_on_the_device_is_joined_by_reading_it_and_every_key_reads_back_a_tombstone_too()
        {
            using var session = store.NewSession<KeyStruct, InputStruct, OutputStruct, Empty, Quiet>(new Quiet());
            var context = session.BasicContext;
            var size = store.IndexSize;
            var pairs = Pairs(size, 4);
            ClassicAssert.AreEqual(4, pairs.Count, "CONTROL: the search finds four pairs");
            var keys = pairs.SelectMany(p => p.Low.Concat(p.High)).ToList();
            foreach (var i in keys) Write(context, i, 0);
            foreach (var i in keys) Write(context, i, 1);   // each key twice: the walk keeps the newest and passes the older
            var deleted = pairs[0].Low[1];
            _ = context.Delete(Key(deleted));   // a tombstone on one side: the join carries it, and the key stays deleted
            store.Log.FlushAndEvict(wait: true);
            var collisions = Collisions();
            ClassicAssert.AreEqual(4, collisions.Count, "CONTROL: each pair is a same-tag pair of two chains");
            ClassicAssert.IsTrue(collisions.All(c => store.JoinHalvingPair(c).Outcome == HalvingJoinOutcome.OnDisk), "CONTROL: the in-memory join refuses chains on the device");

            var joins = collisions.Select(store.JoinHalvingPairFromDevice).ToList();
            TestContext.Progress.WriteLine(string.Join("; ", joins));
            ClassicAssert.IsTrue(joins.All(j => j.Outcome == HalvingJoinOutcome.Joined), $"every pair joins from the device: {string.Join("; ", joins)}");
            ClassicAssert.AreEqual(4 * 3, joins.Sum(j => j.Copied), "each join copies the three keys of one side, once each");
            var h = await store.HalveIndexAsync();
            ClassicAssert.IsTrue(h.Halved, $"the joined pairs merge: {h.Refusal}");
            ClassicAssert.AreEqual(size / 2, store.IndexSize);
            foreach (var i in keys)
                ClassicAssert.AreEqual(i == deleted ? (false, -1L) : (true, i * 10L + 1), Read(context, i), $"key {i} reads its newest after the halving");
            Write(context, keys[0], 7);
            ClassicAssert.AreEqual((true, keys[0] * 10L + 7), Read(context, keys[0]), "the halved table takes writes");
        }

        [Test]
        [Category("TsavoriteKV")]
        [Category("Index")]
        public async Task A_write_to_the_copied_side_while_the_walk_reads_is_kept_and_the_next_ask_joins_again()
        {
            using var session = store.NewSession<KeyStruct, InputStruct, OutputStruct, Empty, Quiet>(new Quiet());
            var context = session.BasicContext;
            var (low, high) = Pairs(store.IndexSize, 1).Single();
            foreach (var i in low.Concat(high)) Write(context, i, 0);
            store.Log.FlushAndEvict(wait: true);
            var pair = Collisions().Single();
            store.HalvingJoinBeforeCopyForTest = () => Write(context, low[0], 5);   // the low side (fewer or equal keys) is the one copied
            HalvingJoin raced;
            try { raced = store.JoinHalvingPairFromDevice(pair); }
            finally { store.HalvingJoinBeforeCopyForTest = null; }
            ClassicAssert.AreEqual(HalvingJoinOutcome.Raced, raced.Outcome, "the side moved under the walk: the swap is refused");
            ClassicAssert.AreEqual((true, low[0] * 10L + 5), Read(context, low[0]), "the racing write is kept");

            store.Log.FlushAndEvict(wait: true);
            var again = store.JoinHalvingPairFromDevice(Collisions().Single());
            ClassicAssert.AreEqual(HalvingJoinOutcome.Joined, again.Outcome, "the next ask joins the pair as it now is");
            var h = await store.HalveIndexAsync();
            ClassicAssert.IsTrue(h.Halved, h.Refusal);
            ClassicAssert.AreEqual((true, low[0] * 10L + 5), Read(context, low[0]));
            foreach (var i in low.Skip(1).Concat(high))
                ClassicAssert.AreEqual((true, i * 10L), Read(context, i), $"key {i} reads back after the halving");
        }

        [Test]
        [Category("TsavoriteKV")]
        [Category("Index")]
        public void A_pair_whose_chain_a_writer_may_still_update_waits()
        {
            using var session = store.NewSession<KeyStruct, InputStruct, OutputStruct, Empty, Quiet>(new Quiet());
            var context = session.BasicContext;
            var (low, high) = Pairs(store.IndexSize, 1).Single();
            foreach (var i in low.Concat(high)) Write(context, i, 0);
            var pair = Collisions().Single();
            ClassicAssert.AreEqual(HalvingJoinOutcome.Mutable, store.JoinHalvingPairFromDevice(pair).Outcome, "a chain in the mutable region is not copied");
            ClassicAssert.AreEqual(pair, Collisions().Single(), "the pair stands as it was");
        }
    }
}
