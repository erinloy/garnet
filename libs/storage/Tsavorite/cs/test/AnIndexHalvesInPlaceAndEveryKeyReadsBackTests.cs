// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
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
    /// A hash index halves in place (ADR-0146 law 7, rg ruling 10-29: in place replaces rebuild-beside): bucket i and i + N/2 merge into
    /// bucket i of a table half the size, with an overflow of its own, and every key reads back. A same-tag pair of two chains is never
    /// merged blind: the halving is refused and the table is as it was. A grow's copies (one entry twice, or one chain holding the
    /// other) merge back into one.
    /// </summary>
    [TestFixture]
    internal class AnIndexHalvesInPlaceAndEveryKeyReadsBackTests : TestBase
    {
        private const int Keys = 4000, Pairs = 6;

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
            log = Devices.CreateLogDevice(Path.Join(MethodTestDir, "halve.log"), deleteOnClose: true);
            store = new(new KVSettings { IndexSize = 1L << 16, LogDevice = log, PageSize = 1L << 12, LogMemorySize = 1L << 20, SegmentSize = 1L << 22 }
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

        private (long Bucket, ushort Tag) Place(int i, long size) { var hash = Utility.GetHashCode((long)i); return (hash & (size - 1), HashBucketEntry.GetTag(hash)); }

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

        // Keys whose tag no other bucket brings to their cell in a table <paramref name="shrunk"/> times smaller: halvings with no collision.
        private List<int> KeysWithoutCollisions(long size, int shrunk)
        {
            var final = size >> shrunk;
            var cells = new Dictionary<(long, ushort), long>();
            var keys = new List<int>();
            for (var i = 0; keys.Count < Keys; i++)
            {
                var (bucket, tag) = Place(i, size);
                if (cells.TryGetValue((bucket & (final - 1), tag), out var held) && held != bucket) continue;
                cells[(bucket & (final - 1), tag)] = bucket;
                keys.Add(i);
            }
            return keys;
        }

        [Test]
        [Category("TsavoriteKV")]
        [Category("Index")]
        public async Task A_table_halves_twice_while_it_serves_and_every_key_reads_back([Values] bool writing)
        {
            using var session = store.NewSession<KeyStruct, InputStruct, OutputStruct, Empty, Quiet>(new Quiet());
            var context = session.BasicContext;
            var size = store.IndexSize;
            var keys = KeysWithoutCollisions(size, 2);
            foreach (var i in keys) Write(context, i, 0);
            var entries = store.IndexEntriesAddressedIn(LogAddress.kTempInvalidAddress + 1, store.Log.TailAddress);
            ClassicAssert.Greater(entries, Keys / 2, "CONTROL: the keys' chains hold entries");

            var generation = new int[keys.Max() + 1];
            using var stop = new CancellationTokenSource();
            var writer = !writing ? Task.CompletedTask : Task.Run(() =>
            {
                using var ws = store.NewSession<KeyStruct, InputStruct, OutputStruct, Empty, Quiet>(new Quiet());
                for (var n = 0; !stop.IsCancellationRequested; n++)
                {
                    var i = keys[n % keys.Count];
                    Write(ws.BasicContext, i, Volatile.Read(ref generation[i]) + 1);
                    Volatile.Write(ref generation[i], generation[i] + 1);
                }
            });

            for (var halving = 1; halving <= 2; halving++)
            {
                var before = store.IndexSize;
                var h = await store.HalveIndexAsync();
                ClassicAssert.IsTrue(h.Halved, $"halving {halving}: {h.Refusal}");
                ClassicAssert.AreEqual(before / 2, store.IndexSize, $"halving {halving}: the table is half its size");
                ClassicAssert.AreEqual(0, h.Collisions);
                ClassicAssert.Greater(store.OverflowBucketCount, 0, $"CONTROL: halving {halving}'s buckets spill into an overflow of their own: {h}");
                TestContext.Progress.WriteLine($"halving {halving}: {h}");
            }
            stop.Cancel();
            await writer;

            foreach (var i in keys)
                ClassicAssert.AreEqual((true, (long)(i * 10 + Volatile.Read(ref generation[i]))), Read(context, i), $"key {i} reads its last write after two halvings");
            Write(context, keys[0], 99);
            ClassicAssert.AreEqual((true, keys[0] * 10L + 99), Read(context, keys[0]), "the halved table takes writes");
        }

        [Test]
        [Category("TsavoriteKV")]
        [Category("Index")]
        public async Task A_same_tag_pair_of_two_chains_refuses_the_halving_and_leaves_the_table_as_it_was()
        {
            using var session = store.NewSession<KeyStruct, InputStruct, OutputStruct, Empty, Quiet>(new Quiet());
            var context = session.BasicContext;
            var size = store.IndexSize;
            var half = size / 2;
            var written = KeysWithoutCollisions(size, 1);
            var sides = new Dictionary<(long, ushort), (int Low, int High)>();
            for (int i = written.Max() + 1, pairs = 0; pairs < Pairs; i++)
            {
                var (bucket, tag) = Place(i, size);
                var cell = (bucket & (half - 1), tag);
                var (low, high) = sides.GetValueOrDefault(cell, (-1, -1));
                if (bucket < half && low < 0) low = i; else if (bucket >= half && high < 0) high = i; else continue;
                sides[cell] = (low, high);
                if (low >= 0 && high >= 0) { written.Add(low); written.Add(high); pairs++; }
            }
            foreach (var i in written) Write(context, i, 0);

            var h = await store.HalveIndexAsync();
            ClassicAssert.IsFalse(h.Halved, "MUTANT (one chain of a colliding pair kept): the halving loses the other chain's keys");
            ClassicAssert.GreaterOrEqual(h.Collisions, Pairs, $"each planted pair is a collision: {h}");
            StringAssert.Contains("two chains", h.Refusal);
            ClassicAssert.AreEqual(size, store.IndexSize, "a refused halving leaves the table as it was");
            foreach (var i in written)
                ClassicAssert.AreEqual((true, (long)(i * 10)), Read(context, i), $"key {i} reads after the refusal");

            // settled the way a cell settles them: the pre-pass names each pair and a proven chain is dropped (its keys are its cold tier's)
            var found = new List<IndexTagCollision>();
            for (long at = 0; ;)
            {
                var step = store.FindHalvingCollisions(at, 64, found, int.MaxValue);
                if (step.Done) break;
                at = step.Next;
            }
            var dropped = new HashSet<int>();
            foreach (var c in found)
            {
                ClassicAssert.IsTrue(store.DropEntryIfUnchanged(c.Low + half, c.Tag, c.HighWord));
                dropped.UnionWith(written.Where(i => Place(i, size) == (c.Low + half, c.Tag)));
            }
            h = await store.HalveIndexAsync();
            ClassicAssert.IsTrue(h.Halved, $"with its pairs settled the table halves: {h.Refusal}");
            foreach (var i in written)
                ClassicAssert.AreEqual(dropped.Contains(i) ? (false, -1L) : (true, (long)(i * 10)), Read(context, i), $"key {i}: only the dropped chains' keys miss");
        }

        [Test]
        [Category("TsavoriteKV")]
        [Category("Index")]
        public async Task A_grown_table_halves_back_and_keeps_each_copy_once()
        {
            using var session = store.NewSession<KeyStruct, InputStruct, OutputStruct, Empty, Quiet>(new Quiet());
            var context = session.BasicContext;
            var size = store.IndexSize;
            var keys = KeysWithoutCollisions(size, 1);
            var onDisk = keys.Take(Keys / 2).ToArray();
            foreach (var i in onDisk) Write(context, i, 0);
            store.Log.FlushAndEvict(wait: true);   // below head at the grow: each entry is copied into both halves
            foreach (var i in keys.Skip(Keys / 2)) Write(context, i, 0);   // in memory at the grow: each chain is split by the key's bit
            ClassicAssert.IsTrue(await store.GrowIndexAsync());
            ClassicAssert.AreEqual(size * 2, store.IndexSize);
            var big = store.IndexSize;
            foreach (var i in keys.Where(i => Place(i, big).Bucket < size)) Write(context, i, 1);   // the lower half's chains move on past their copies

            var h = await store.HalveIndexAsync();
            ClassicAssert.IsTrue(h.Halved, $"the grown table halves back: {h.Refusal}");
            ClassicAssert.AreEqual(size, store.IndexSize);
            ClassicAssert.Greater(h.Duplicates, 0, $"a below-head entry the grow copied twice is kept once: {h}");
            ClassicAssert.Greater(h.Suffixes, 0, $"a chain that holds its copy is kept, its copy not: {h}");
            foreach (var i in keys)
                ClassicAssert.AreEqual((true, (long)(i * 10 + (Place(i, big).Bucket < size ? 1 : 0))), Read(context, i), $"key {i} reads its last write after grow and halve");
        }
    }
}
