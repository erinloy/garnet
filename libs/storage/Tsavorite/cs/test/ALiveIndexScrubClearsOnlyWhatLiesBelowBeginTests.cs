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
    /// A store's hash index keeps an entry for every chain it ever started until an insert happens to pass it; once the log's begin
    /// moves past a chain, its entry names nothing a read can reach. A live scrub clears those entries in bounded steps while the store
    /// serves, so the table's live entries follow the log - the first step of shrinking an index in place (ADR-0146 4a).
    /// </summary>
    [TestFixture]
    internal class ALiveIndexScrubClearsOnlyWhatLiesBelowBeginTests : TestBase
    {
        private const int Keys = 4000, Rewritten = 500, Step = 8, Pairs = 8;

        private TsavoriteKV<StructStoreFunctions, StructAllocator> store;
        private IDevice log;

        /// <summary>The shared Functions asserts a pending read's value against its key; these reads judge their own outputs.</summary>
        private sealed class Quiet : Functions
        {
            public override void ReadCompletionCallback(ref DiskLogRecord diskLogRecord, ref InputStruct input, ref OutputStruct output, Empty ctx, Status status, RecordMetadata recordMetadata) { }
        }

        [SetUp]
        public void Setup()
        {
            DeleteDirectory(MethodTestDir, wait: true);
            log = Devices.CreateLogDevice(Path.Join(MethodTestDir, "scrub.log"), deleteOnClose: true);
            store = new(new KVSettings { IndexSize = 1L << 12, LogDevice = log, PageSize = 1L << 12, LogMemorySize = 1L << 16, SegmentSize = 1L << 22 }
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

        [Test]
        [Category("TsavoriteKV")]
        [Category("Index")]
        public void A_scrub_in_steps_clears_every_entry_below_begin_and_changes_no_read([Values] bool writing)
        {
            using var session = store.NewSession<KeyStruct, InputStruct, OutputStruct, Empty, Quiet>(new Quiet());
            var context = session.BasicContext;
            for (var i = 0; i < Keys / 2; i++) Write(context, i, 0);
            var begin = store.Log.TailAddress;
            for (var i = Keys / 2; i < Keys; i++) Write(context, i, 0);
            store.Log.ShiftBeginAddress(begin);

            var below = store.IndexEntriesAddressedIn(LogAddress.kTempInvalidAddress + 1, begin);
            ClassicAssert.Greater(below, Keys / 4, "CONTROL: the first half's chains still hold entries below begin");
            ClassicAssert.Greater(store.IndexSize, 0);
            ClassicAssert.Greater(store.OverflowBucketCount, 0, "CONTROL: the table spills into overflow buckets, which the scrub walks too");
            for (var i = 0; i < Keys; i++)
                ClassicAssert.AreEqual(i >= Keys / 2, Read(context, i).Found, $"CONTROL: key {i} reads as its side of begin says");

            using var stop = new CancellationTokenSource();
            var writer = !writing ? Task.CompletedTask : Task.Run(() =>
            {
                using var ws = store.NewSession<KeyStruct, InputStruct, OutputStruct, Empty, Quiet>(new Quiet());
                var wc = ws.BasicContext;
                for (var i = 0; i < Rewritten; i++) { Write(wc, i, 1); Write(wc, Keys / 2 + i, 1); }   // a forgotten key comes back, a live one moves on
            });

            long cleared = 0, lost = 0, steps = 0, at = 0;
            while (true)
            {
                var step = store.ScrubEntriesBelowBegin(at, Step);
                ClassicAssert.IsFalse(step.Refused, "no resize is in flight");
                (cleared, lost, steps, at) = (cleared + step.Cleared, lost + step.Lost, steps + 1, step.Next);
                if (step.Done) break;
            }
            writer.Wait();

            ClassicAssert.AreEqual(store.IndexSize, steps * Step, "each step walked its own buckets once, and the steps cover the table");
            ClassicAssert.AreEqual(0, store.IndexEntriesAddressedIn(LogAddress.kTempInvalidAddress + 1, begin), "no entry below begin is left");
            if (!writing)
                ClassicAssert.AreEqual((below, 0L), (cleared, lost), "alone, the scrub clears exactly what lay below begin");
            else
                ClassicAssert.LessOrEqual(cleared, below, "beside a writer, an insert may reuse an entry first; the scrub never clears more");
            ClassicAssert.AreEqual(new IndexScrubStep(0, 0, store.IndexSize, true, false), store.ScrubEntriesBelowBegin(0, store.IndexSize),
                "a second pass, or a step re-run after a kill, finds nothing to clear");

            for (var i = 0; i < Keys; i++)
            {
                var rewritten = writing && (i < Rewritten || (i >= Keys / 2 && i < Keys / 2 + Rewritten));
                var expected = i >= Keys / 2 || rewritten ? (true, (long)(i * 10 + (rewritten ? 1 : 0))) : (false, -1L);
                ClassicAssert.AreEqual(expected, Read(context, i), $"key {i} reads after the scrub as it read before it, or as its last write");
            }
        }

        [Test]
        [Category("TsavoriteKV")]
        [Category("Index")]
        public void A_halving_names_each_same_tag_pair_and_a_drop_takes_only_the_chain_it_proved()
        {
            using var session = store.NewSession<KeyStruct, InputStruct, OutputStruct, Empty, Quiet>(new Quiet());
            var context = session.BasicContext;
            var size = store.IndexSize;
            var half = size / 2;
            (long Bucket, ushort Tag) Place(int i) { var hash = Utility.GetHashCode((long)i); return (hash & (size - 1), HashBucketEntry.GetTag(hash)); }

            // Background keys, plus keys found to put one tag on both sides of a pair: a halving's collisions, planted, not hoped for.
            var written = Enumerable.Range(0, Keys).ToList();
            var sides = new Dictionary<(long Low, ushort Tag), (int Low, int High)>();
            for (int i = Keys, pairs = 0; pairs < Pairs; i++)
            {
                var (bucket, tag) = Place(i);
                var cell = (bucket & (half - 1), tag);
                var (low, high) = sides.GetValueOrDefault(cell, (-1, -1));
                if (bucket < half && low < 0) low = i; else if (bucket >= half && high < 0) high = i; else continue;
                sides[cell] = (low, high);
                if (low >= 0 && high >= 0) { written.Add(low); written.Add(high); pairs++; }
            }
            foreach (var i in written) Write(context, i, 0);

            // The truth the index must agree with: which (bucket, tag) chains share a tag with their sibling across the halving line.
            var chains = written.GroupBy(Place).ToDictionary(g => g.Key, g => g.ToArray());
            var truth = chains.Keys.Where(c => c.Bucket < half && chains.ContainsKey((c.Bucket + half, c.Tag))).OrderBy(c => c.Bucket).ThenBy(c => c.Tag).ToArray();
            ClassicAssert.GreaterOrEqual(truth.Length, Pairs, "CONTROL: the planted keys put a tag on both sides of each of their pairs");

            var found = new List<IndexTagCollision>();
            long at = 0, steps = 0;
            while (true)
            {
                var step = store.FindHalvingCollisions(at, Step, found, int.MaxValue);
                ClassicAssert.IsFalse(step.Refused);
                (at, steps) = (step.Next, steps + 1);
                if (step.Done) break;
            }
            ClassicAssert.AreEqual(half / Step, steps, "the pre-pass walks the lower half in its steps");
            CollectionAssert.AreEqual(truth, found.Select(c => (c.Low, c.Tag)).OrderBy(c => c.Low).ThenBy(c => c.Tag).ToArray(),
                "every same-tag pair is named, once, and nothing else is");

            // A drop over a stale proof is refused: a write moved the chain's head after the word was read.
            var first = found[0];
            var moved = chains[(first.Low + half, first.Tag)][0];
            var newcomer = new KeyStruct { kfield1 = moved, kfield2 = -1 };   // the comparer hashes kfield1 alone: a new key at this chain's head
            var nv = new ValueStruct { vfield1 = moved, vfield2 = 7 };
            _ = context.Upsert(newcomer, SpanByte.FromPinnedVariable(ref nv), Empty.Default);
            ClassicAssert.IsFalse(store.DropEntryIfUnchanged(first.Low + half, first.Tag, first.HighWord), "the proof was of a chain that no longer is");
            InputStruct ni = default;
            OutputStruct no = default;
            ClassicAssert.IsTrue(context.Read(newcomer, ref ni, ref no).Found, "and the chain's newest write still reads");

            // A drop over a current word takes that chain, and only it: its keys miss here (their cold tier answers them in a cell).
            found.Clear();
            _ = store.FindHalvingCollisions(first.Low, 1, found, int.MaxValue);
            var now = found.Single(c => c.Tag == first.Tag);
            ClassicAssert.IsTrue(store.DropEntryIfUnchanged(now.Low + half, now.Tag, now.HighWord));
            var gone = chains[(now.Low + half, now.Tag)].ToHashSet();
            foreach (var i in written)
                ClassicAssert.AreEqual(!gone.Contains(i), Read(context, i).Found, $"key {i}: only the dropped chain's keys miss");
            found.Clear();
            _ = store.FindHalvingCollisions(now.Low, 1, found, int.MaxValue);
            ClassicAssert.IsFalse(found.Any(c => c.Tag == now.Tag), "the pair it settled is no longer named");
        }
    }
}
