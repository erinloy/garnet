// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
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
    /// Ziltch mesh broker 11-01 (optimized IL, 30 s): the awaits under a session's CompletePendingWithOutputsAsync made 5 async boxes per
    /// pending read (the outputs' wait, the session's, the store's, the context's WaitPendingAsync and the queue's WaitForEntryAsyncSlow),
    /// ~200 MB of 3.4 GB. Each rents its box from a pool now. Mutant: the boxes are made per wait (a read that waits allocates past the bound).
    /// The queue's wait itself waits on a reused source, not a semaphore (with a token it made a task node, a box and a cancellation promise).
    /// </summary>
    [TestFixture]
    internal class APendingCompletionRentsItsBoxesTests : TestBase
    {
        private const int Keys = 4_000, Rounds = 9, PerRound = 400;

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
            log = Devices.CreateLogDevice(Path.Join(MethodTestDir, "pending.log"), deleteOnClose: true);
            store = new(new KVSettings { IndexSize = 1L << 16, LogDevice = log, PageSize = 1L << 12, LogMemorySize = 1L << 16, SegmentSize = 1L << 22 }
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

        [Test]
        [Category("TsavoriteKV")]
        public async Task A_read_that_waits_on_the_device_rents_its_completions_boxes([Values(false, true)] bool timed)
        {
            using var bound = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            var token = timed ? bound.Token : CancellationToken.None;
            using var session = store.NewSession<KeyStruct, InputStruct, OutputStruct, Empty, Quiet>(new Quiet());
            var context = session.BasicContext;
            for (var i = 0; i < Keys; i++)
            {
                var value = new ValueStruct { vfield1 = i, vfield2 = i * 10 };
                _ = context.Upsert(Key(i), SpanByte.FromPinnedVariable(ref value), Empty.Default);
            }
            store.Log.FlushAndEvict(wait: true);

            var pended = 0;
            async Task<long> Round(int round)
            {
                var before = GC.GetTotalAllocatedBytes(precise: true);
                for (var n = 0; n < PerRound; n++)
                {
                    var i = (round * PerRound + n * 7) % Keys;
                    InputStruct input = default;
                    OutputStruct output = default;
                    var status = context.Read(Key(i), ref input, ref output);
                    if (!status.IsPending) continue;
                    pended++;
                    using var outputs = await context.CompletePendingWithOutputsAsync(token: token).ConfigureAwait(false);
                    while (outputs.Next()) ClassicAssert.AreEqual(i * 10, outputs.Current.Output.value.vfield2);
                }
                return GC.GetTotalAllocatedBytes(precise: true) - before;
            }

            _ = await Round(0);
            pended = 0;
            var rounds = new long[Rounds];
            for (var r = 0; r < Rounds; r++) rounds[r] = await Round(r + 1);
            Array.Sort(rounds);
            var perRead = rounds[Rounds / 2] / (double)PerRound;
            TestContext.Progress.WriteLine($"timed {timed}: pending reads {pended}; bytes per read: median {perRead:F0}, min {rounds[0] / (double)PerRound:F0}, max {rounds[^1] / (double)PerRound:F0}");
            ClassicAssert.AreEqual(Rounds * PerRound, pended, "CONTROL: every read waits on the device");
            ClassicAssert.Less(perRead, timed ? TimedBound : Bound, $"MUTANT (a semaphore wait per read): a read that waits allocates {perRead:F0} B");
        }

        // measured: 464 B a read untimed, 466 B timed; with the semaphore's wait 554 B untimed and 841 B timed
        private const double Bound = 510, TimedBound = 650;
    }
}
