// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
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
    /// Each multi-page scan pinned up to 32 page-sized frames, and the log's pool kept only 32 MB of the slots handed back, so N
    /// scans at once made N x 32 slots and dropped all but 32 MB of them. The pinned heap never compacts, so the dropped slots
    /// stayed committed as holes: 0.5-1.7 GB on the broker after each boot. A log's scans now hold at most its budget of frames
    /// at once, and the pool keeps every slot, so the slots a log ever makes follow its budget, not how many scans ran together.
    /// </summary>
    [TestFixture]
    internal class ConcurrentScansHoldTheirLogsFrameBudgetTests : TestBase
    {
        private const int Records = 3_000_000;
        private const int PageBits = 20;
        private const int Scans = 16;

        private TsavoriteKV<StructStoreFunctions, StructAllocator> store;
        private ClientSession<KeyStruct, InputStruct, OutputStruct, Empty, Functions, StructStoreFunctions, StructAllocator> session;
        private IDevice log;

        [SetUp]
        public void Setup()
        {
            DeleteDirectory(MethodTestDir, wait: true);
            log = Devices.CreateLogDevice(Path.Join(MethodTestDir, "budget.log"), deleteOnClose: true);
            store = new(new KVSettings { IndexSize = 1L << 20, LogDevice = log, PageSize = 1L << PageBits, LogMemorySize = 1L << 23, SegmentSize = 1L << 26 }
                , StoreFunctions.Create(KeyStruct.Comparer.Instance, SpanByteRecordTriggers.Instance)
                , (allocatorSettings, storeFunctions) => new(allocatorSettings, storeFunctions));
            session = store.NewSession<KeyStruct, InputStruct, OutputStruct, Empty, Functions>(new Functions());
        }

        [TearDown]
        public void TearDown()
        {
            session?.Dispose(); session = null;
            store?.Dispose(); store = null;
            log?.Dispose(); log = null;
            OnTearDown();
        }

        private sealed class InFlight
        {
            public int Now, Most;

            public void Enter()
            {
                var now = Interlocked.Increment(ref Now);
                for (int most; now > (most = Volatile.Read(ref Most)) && Interlocked.CompareExchange(ref Most, now, most) != most;) { }
            }

            public void Leave() => Interlocked.Decrement(ref Now);
        }

        private struct Counter : IScanIteratorFunctions
        {
            public long Seen, Wrong;
            public bool OnStart(long beginAddress, long endAddress) => true;
            public bool Reader<TSourceLogRecord>(in TSourceLogRecord logRecord, RecordMetadata recordMetadata, long numberOfRecords, out CursorRecordResult cursorRecordResult)
                where TSourceLogRecord : ISourceLogRecord
            {
                cursorRecordResult = CursorRecordResult.Accept;
                if (++Seen % 100_000 == 0)
                    Thread.Sleep(2);   // a scan that does work per record, so the scans overlap
                var value = MemoryMarshal.Read<ValueStruct>(logRecord.ValueSpan);
                if (value.vfield1 != 7 || value.vfield2 != 8) Wrong++;
                return true;
            }
            public void OnException(Exception exception, long numberOfRecords) => throw exception;
            public void OnStop(bool completed, long numberOfRecords) { }
        }

        [Test]
        [Category("TsavoriteKV")]
        public async Task Concurrent_scans_make_no_more_frame_slots_than_their_logs_budget()
        {
            var bContext = session.BasicContext;
            var value = new ValueStruct { vfield1 = 7, vfield2 = 8 };
            for (var i = 0; i < Records; i++)
            {
                var key = new KeyStruct { kfield1 = i, kfield2 = i };
                _ = bContext.Upsert(key, SpanByte.FromPinnedVariable(ref value), Empty.Default);
            }
            store.Log.FlushAndEvict(wait: true);
            var pages = (store.Log.TailAddress - store.Log.BeginAddress) >> PageBits;
            ClassicAssert.Greater(pages, 2 * ScanReadAhead.Frames(PageBits), "CONTROL: each scan reads more pages than its frames hold");

            var inFlight = new InFlight();
            using var go = new ManualResetEventSlim();
            var scans = Enumerable.Range(0, Scans).Select(scan => Task.Run(() =>
            {
                var counter = new Counter();
                go.Wait();
                inFlight.Enter();
                try { _ = store.Log.Scan(ref counter, store.Log.BeginAddress, store.Log.TailAddress, DiskScanBufferingMode.MultiPageBuffering); }
                finally { inFlight.Leave(); }
                return (counter.Seen, counter.Wrong);
            })).ToArray();
            go.Set();
            var seen = await Task.WhenAll(scans);
            var frames = store.ScanFrames;
            TestContext.Progress.WriteLine($"{Scans} scans of {pages} pages, {inFlight.Most} at once: {frames}");

            ClassicAssert.IsTrue(seen.All(s => s.Seen == Records && s.Wrong == 0), "every scan hands out every record once, each read as written");
            ClassicAssert.GreaterOrEqual(inFlight.Most, 4, "CONTROL: the scans asked for their frames together");
            ClassicAssert.LessOrEqual(frames.Made, frames.Budget, $"{Scans} scans at once made {frames.Made} frame slots against a budget of {frames.Budget}: each pinned its own frames");
            ClassicAssert.AreEqual(0, frames.PastBudget, "no scan took frames past the budget: each waited or was trimmed to what was left");
            ClassicAssert.AreEqual(0, frames.Held, "every scan handed its frames back");
            ClassicAssert.AreEqual(frames.Made, frames.Kept, "the pool keeps every slot it made, so none is left as a hole");
        }
    }
}
