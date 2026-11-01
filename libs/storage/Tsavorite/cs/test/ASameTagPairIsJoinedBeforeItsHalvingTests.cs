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
    /// Ziltch router 11-01 00:3xZ: its F2 pass ended with 2.1 GB hot, and one halving (2,097,152 -> 1,048,576 buckets) would bring its index under
    /// its bar, but 52 same-tag pairs of two chains refused every halving, and no pass moves them. A pair is joined before the barrier: one side's
    /// newest record of each key is copied to the tail onto the other side's head, so the merged entry walks both. Every key reads back after the
    /// halving, a tombstone included; a write to the other side between the join and the barrier is refused there, and joined again.
    /// Mutants: copies chained to nothing (the halving is refused, so nothing can join); the head's record copied alone (a key of the side is lost).
    /// </summary>
    [TestFixture]
    internal class ASameTagPairIsJoinedBeforeItsHalvingTests : TestBase
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
            log = Devices.CreateLogDevice(Path.Join(MethodTestDir, "join.log"), deleteOnClose: true);
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

        // Pairs whose low side and high side (bucket + half) each hold two keys under one tag, each cell its own: a join copies two.
        private static List<(int[] Low, int[] High)> Pairs(long size, int want)
        {
            var half = size / 2;
            var cells = new Dictionary<(long, ushort), List<int>>();
            for (var i = 0; i < 2_000_000; i++)
            {
                var (bucket, tag) = Place(i, size);
                if (!cells.TryGetValue((bucket, tag), out var keys)) cells[(bucket, tag)] = keys = [];
                keys.Add(i);
            }
            var pairs = new List<(int[] Low, int[] High)>();
            var used = new HashSet<long>();
            foreach (var ((bucket, tag), keys) in cells)
            {
                if (bucket >= half || keys.Count < 2 || !cells.TryGetValue((bucket + half, tag), out var high) || high.Count < 2 || !used.Add(bucket)) continue;
                pairs.Add(([keys[0], keys[1]], [high[0], high[1]]));
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

        private List<HalvingJoin> JoinAll()
        {
            store.Log.ShiftReadOnlyAddress(store.Log.TailAddress, wait: true);   // a join copies only what no writer updates in place
            return [.. Collisions().Select(store.JoinHalvingPair)];
        }

        [Test]
        [Category("TsavoriteKV")]
        [Category("Index")]
        public async Task A_joined_pair_halves_and_every_key_reads_back_a_tombstone_too()
        {
            using var session = store.NewSession<KeyStruct, InputStruct, OutputStruct, Empty, Quiet>(new Quiet());
            var context = session.BasicContext;
            var size = store.IndexSize;
            var pairs = Pairs(size, 4);
            ClassicAssert.AreEqual(4, pairs.Count, "CONTROL: the search finds four pairs");
            var keys = pairs.SelectMany(p => p.Low.Concat(p.High)).ToList();
            foreach (var i in keys) Write(context, i, 0);
            var deleted = pairs[0].Low[1];
            _ = context.Delete(Key(deleted));   // a tombstone heads one side: the join carries it, and the key stays deleted
            ClassicAssert.AreEqual(4, Collisions().Count, "CONTROL: each pair is a same-tag pair of two chains");
            var refused = await store.HalveIndexAsync();
            ClassicAssert.IsFalse(refused.Halved, "CONTROL: a blind pair refuses the halving");

            var joins = JoinAll();
            TestContext.Progress.WriteLine(string.Join("; ", joins));
            ClassicAssert.IsTrue(joins.All(j => j.Outcome == HalvingJoinOutcome.Joined), $"every pair joins: {string.Join("; ", joins)}");
            ClassicAssert.Greater(joins.Sum(j => j.Bytes), 0, "the copies are counted bytes");
            var h = await store.HalveIndexAsync();
            ClassicAssert.IsTrue(h.Halved, $"the joined pairs merge: {h.Refusal}");
            ClassicAssert.AreEqual(size / 2, store.IndexSize);
            ClassicAssert.AreEqual(0, h.Collisions);
            foreach (var i in keys)
                ClassicAssert.AreEqual(i == deleted ? (false, -1L) : (true, (long)(i * 10)), Read(context, i), $"key {i} reads as it was written after the halving");
            Write(context, keys[0], 7);
            ClassicAssert.AreEqual((true, keys[0] * 10L + 7), Read(context, keys[0]), "the halved table takes writes");
        }

        [Test]
        [Category("TsavoriteKV")]
        [Category("Index")]
        public async Task A_write_to_the_other_side_after_a_join_is_refused_at_the_barrier_and_joined_again()
        {
            using var session = store.NewSession<KeyStruct, InputStruct, OutputStruct, Empty, Quiet>(new Quiet());
            var context = session.BasicContext;
            var size = store.IndexSize;
            var (low, high) = Pairs(size, 1).Single();
            foreach (var i in low.Concat(high)) Write(context, i, 0);
            ClassicAssert.AreEqual(HalvingJoinOutcome.Joined, JoinAll().Single().Outcome);
            store.Log.ShiftReadOnlyAddress(store.Log.TailAddress, wait: true);   // each write makes a new record: a copy updated in place parts nothing
            Write(context, high[0], 1);   // both sides' heads move past the joined word, each onto its own record
            Write(context, low[0], 1);
            var refused = await store.HalveIndexAsync();
            ClassicAssert.IsFalse(refused.Halved, "a side written after its join is no longer a suffix: the barrier refuses it");
            ClassicAssert.AreEqual(1, refused.Collisions);

            ClassicAssert.AreEqual(HalvingJoinOutcome.Joined, JoinAll().Single().Outcome, "the next ask joins the pair again");
            var h = await store.HalveIndexAsync();
            ClassicAssert.IsTrue(h.Halved, h.Refusal);
            ClassicAssert.AreEqual((true, high[0] * 10L + 1), Read(context, high[0]));
            ClassicAssert.AreEqual((true, high[1] * 10L), Read(context, high[1]));
            ClassicAssert.AreEqual((true, low[0] * 10L + 1), Read(context, low[0]));
            ClassicAssert.AreEqual((true, low[1] * 10L), Read(context, low[1]));
        }

        [Test]
        [Category("TsavoriteKV")]
        [Category("Index")]
        public void A_pair_whose_chains_lie_below_head_is_refused_on_disk_and_a_mutable_one_waits()
        {
            using var session = store.NewSession<KeyStruct, InputStruct, OutputStruct, Empty, Quiet>(new Quiet());
            var context = session.BasicContext;
            var (low, high) = Pairs(store.IndexSize, 1).Single();
            foreach (var i in low.Concat(high)) Write(context, i, 0);
            var pair = Collisions().Single();
            ClassicAssert.AreEqual(HalvingJoinOutcome.Mutable, store.JoinHalvingPair(pair).Outcome, "a chain a writer may still update in place is not copied");
            store.Log.FlushAndEvict(wait: true);
            var join = store.JoinHalvingPair(pair);
            ClassicAssert.AreEqual(HalvingJoinOutcome.OnDisk, join.Outcome, "chains below head are refused by name, with no read");
            ClassicAssert.AreEqual(0, join.Copied);
            ClassicAssert.AreEqual(pair, Collisions().Single(), "the pair stands as it was");
        }
    }
}
