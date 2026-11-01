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
    /// Ziltch mesh 11-01 10:5xZ: the same-tag pairs left before a halving rose 1,651 -> 2,449 in 4 min while ~620 joined a pass. A join chained one
    /// side's copies onto the other side's head but left the other side its own word, so the pair stood only while the barrier's in-memory walk
    /// could see the copies reach that head: a write to the other side, or the copies leaving memory, parted it again. A join now gives both sides
    /// the joined word. Mutant: only the copied side is swapped (both facts red).
    /// </summary>
    [TestFixture]
    internal class AJoinedPairHoldsOneWordOnBothSidesTests : TestBase
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
            log = Devices.CreateLogDevice(Path.Join(MethodTestDir, "onejoin.log"), deleteOnClose: true);
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

        // A pair whose low side and high side (bucket + half) each hold two keys under one tag: equal counts, so the low side is the one copied.
        private static (int[] Low, int[] High) Pair(long size)
        {
            var half = size / 2;
            var cells = new Dictionary<(long, ushort), List<int>>();
            for (var i = 0; i < 2_000_000; i++)
            {
                var (bucket, tag) = Place(i, size);
                if (!cells.TryGetValue((bucket, tag), out var keys)) cells[(bucket, tag)] = keys = [];
                keys.Add(i);
            }
            foreach (var ((bucket, tag), keys) in cells)
                if (bucket < half && keys.Count >= 2 && cells.TryGetValue((bucket + half, tag), out var high) && high.Count >= 2)
                    return ([keys[0], keys[1]], [high[0], high[1]]);
            throw new AssertionException("CONTROL: the search finds a pair");
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
        public async Task A_write_to_the_side_a_join_did_not_copy_leaves_the_pair_joined()
        {
            using var session = store.NewSession<KeyStruct, InputStruct, OutputStruct, Empty, Quiet>(new Quiet());
            var context = session.BasicContext;
            var (low, high) = Pair(store.IndexSize);
            foreach (var i in low.Concat(high)) Write(context, i, 0);
            store.Log.ShiftReadOnlyAddress(store.Log.TailAddress, wait: true);   // a join copies only what no writer updates in place
            var join = store.JoinHalvingPair(Collisions().Single());
            ClassicAssert.AreEqual(HalvingJoinOutcome.Joined, join.Outcome, "CONTROL: the pair joins in memory");
            ClassicAssert.AreEqual(2, join.Copied, "CONTROL: the low side's two keys are copied");

            Write(context, high[0], 1);   // the side the join chained onto
            ClassicAssert.AreEqual(0, Collisions().Count, "a write to the side the join did not copy keeps the pair one chain");
            var h = await store.HalveIndexAsync();
            ClassicAssert.IsTrue(h.Halved, h.Refusal);
            ClassicAssert.AreEqual((true, high[0] * 10L + 1), Read(context, high[0]), "the write after the join reads");
            foreach (var i in low.Concat(high.Skip(1)))
                ClassicAssert.AreEqual((true, i * 10L), Read(context, i), $"key {i} reads back after the halving");
        }

        [Test]
        [Category("TsavoriteKV")]
        [Category("Index")]
        public async Task A_pair_joined_from_the_device_stays_joined_after_its_copies_leave_memory()
        {
            using var session = store.NewSession<KeyStruct, InputStruct, OutputStruct, Empty, Quiet>(new Quiet());
            var context = session.BasicContext;
            var (low, high) = Pair(store.IndexSize);
            foreach (var i in low.Concat(high)) Write(context, i, 0);
            store.Log.FlushAndEvict(wait: true);
            var join = store.JoinHalvingPairFromDevice(Collisions().Single());
            ClassicAssert.AreEqual(HalvingJoinOutcome.Joined, join.Outcome, "CONTROL: the pair joins from the device");

            store.Log.FlushAndEvict(wait: true);   // the copies go below head, where the barrier's walk cannot follow them
            ClassicAssert.AreEqual(0, Collisions().Count, "a joined pair is one word, wherever its copies lie");
            var h = await store.HalveIndexAsync();
            ClassicAssert.IsTrue(h.Halved, h.Refusal);
            ClassicAssert.AreEqual(store.IndexSize, IndexBytes / 64 / 2, "CONTROL: the table halved");
            foreach (var i in low.Concat(high))
                ClassicAssert.AreEqual((true, i * 10L), Read(context, i), $"key {i} reads back after the halving");
            Write(context, high[1], 3);
            ClassicAssert.AreEqual((true, high[1] * 10L + 3), Read(context, high[1]), "the halved table takes writes");
        }
    }
}
