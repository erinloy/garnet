// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System.IO;
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
    /// A chain never gives an overflow bucket back: once begin passes most of a small table's keys, their emptied slots stay linked, and
    /// the allocator's high water is the index's size (dev webfrontend 10-30: 16,384 buckets over 7.95M overflow buckets, 497 MB, for
    /// 465 hot keys). A compaction copies each chain's live entries, packed, onto a fresh overflow allocator at the resize barrier: the
    /// overflow falls to what the live entries need, and every key reads as it read before.
    /// </summary>
    [TestFixture]
    internal class AnIndexsOverflowCompactsToWhatItsLiveEntriesNeedTests : TestBase
    {
        private const int Keys = 20000, Kept = 1000, Rewritten = 300;

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
            log = Devices.CreateLogDevice(Path.Join(MethodTestDir, "compact.log"), deleteOnClose: true);
            store = new(new KVSettings { IndexSize = 1L << 12, LogDevice = log, PageSize = 1L << 12, LogMemorySize = 1L << 20, SegmentSize = 1L << 22 }
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

        private IndexOverflowStep Count()
        {
            long reached = 0, needed = 0, entries = 0, below = 0;
            for (long at = 0; ;)
            {
                var step = store.CountOverflowReached(at, 64);
                ClassicAssert.IsFalse(step.Refused, "no resize is in flight");
                (reached, needed, entries, below, at) = (reached + step.Reached, needed + step.Needed, entries + step.Entries, below + step.BelowBegin, step.Next);
                if (step.Done) return new(reached, needed, entries, below, at, true, false);
            }
        }

        [Test]
        [Category("TsavoriteKV")]
        [Category("Index")]
        public async Task A_table_whose_keys_went_below_begin_compacts_its_overflow_and_every_key_reads_back([Values] bool writing)
        {
            using var session = store.NewSession<KeyStruct, InputStruct, OutputStruct, Empty, Quiet>(new Quiet());
            var context = session.BasicContext;
            for (var i = Kept; i < Keys; i++) Write(context, i, 0);
            store.Log.ShiftBeginAddress(store.Log.TailAddress);
            for (var i = 0; i < Kept; i++) Write(context, i, 0);

            var size = store.IndexSize;
            var before = Count();
            TestContext.Progress.WriteLine($"before: {before}, allocator {store.OverflowBucketCount}");
            ClassicAssert.Greater(before.Reached, 4 * (before.Needed + 1), $"CONTROL: the chains still link the buckets their dead entries filled: {before}");
            ClassicAssert.AreEqual(Kept, before.Entries, "CONTROL: one live entry per kept key");

            var generation = new int[Kept];
            using var stop = new CancellationTokenSource();
            var writer = !writing ? Task.CompletedTask : Task.Run(() =>
            {
                using var ws = store.NewSession<KeyStruct, InputStruct, OutputStruct, Empty, Quiet>(new Quiet());
                for (var n = 0; n < Rewritten * 20 && !stop.IsCancellationRequested; n++)
                {
                    var i = n % Rewritten;
                    Write(ws.BasicContext, i, Volatile.Read(ref generation[i]) + 1);
                    Volatile.Write(ref generation[i], generation[i] + 1);
                }
            });

            var c = await store.CompactOverflowAsync();
            stop.Cancel();
            await writer;
            TestContext.Progress.WriteLine($"compaction: {c}");
            ClassicAssert.IsTrue(c.Compacted, c.Refusal);
            ClassicAssert.AreEqual(size, store.IndexSize, "the table keeps its size");
            ClassicAssert.Less(store.OverflowBucketCount, before.Reached / 2,
                $"MUTANT (the allocator not swapped): the overflow falls to what the live entries need, not the high water it was: {c}");

            var after = Count();
            ClassicAssert.AreEqual(after.Needed, after.Reached, $"every overflow bucket a chain reaches is packed: {after}");
            ClassicAssert.AreEqual(0, after.BelowBegin, "no entry below begin is carried");
            for (var i = 0; i < Keys; i++)
            {
                var expected = i < Kept ? (true, (long)(i * 10 + (i < Rewritten ? Volatile.Read(ref generation[i]) : 0))) : (false, -1L);
                ClassicAssert.AreEqual(expected, Read(context, i), $"key {i} reads after the compaction as it read before, or as its last write");
            }
            Write(context, Keys + 1, 0);
            ClassicAssert.AreEqual((true, (Keys + 1) * 10L), Read(context, Keys + 1), "the compacted table takes a new key");
        }
    }
}
