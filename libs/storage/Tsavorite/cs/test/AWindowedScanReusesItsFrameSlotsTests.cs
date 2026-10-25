// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.IO;
using System.Runtime.InteropServices;
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
    /// A walk of the log in bounded windows opens one scan iterator per window, and each iterator built its own frame, pinning a
    /// fresh page-sized array for every page it loaded. The allocator now keeps the slots a disposed scan hands back, so the next
    /// window borrows them: a windowed walk's frame allocation follows the pages one window holds, not the pages of the whole log.
    /// </summary>
    [TestFixture]
    internal class AWindowedScanReusesItsFrameSlotsTests : TestBase
    {
        private const int Records = 100_000;
        private const int PageBits = 16;
        private const int WindowPages = 4;

        private TsavoriteKV<StructStoreFunctions, StructAllocator> store;
        private ClientSession<KeyStruct, InputStruct, OutputStruct, Empty, Functions, StructStoreFunctions, StructAllocator> session;
        private IDevice log;

        [SetUp]
        public void Setup()
        {
            DeleteDirectory(MethodTestDir, wait: true);
            log = Devices.CreateLogDevice(Path.Join(MethodTestDir, "windows.log"), deleteOnClose: true);
            store = new(new KVSettings { IndexSize = 1L << 13, LogDevice = log, PageSize = 1L << PageBits, LogMemorySize = 1L << 20, SegmentSize = 1L << 22 }
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

        private struct Counter : IScanIteratorFunctions
        {
            public long Seen, Wrong;
            public bool OnStart(long beginAddress, long endAddress) => true;
            public bool Reader<TSourceLogRecord>(in TSourceLogRecord logRecord, RecordMetadata recordMetadata, long numberOfRecords, out CursorRecordResult cursorRecordResult)
                where TSourceLogRecord : ISourceLogRecord
            {
                cursorRecordResult = CursorRecordResult.Accept;
                Seen++;
                var value = MemoryMarshal.Read<ValueStruct>(logRecord.ValueSpan);
                if (value.vfield1 != 7 || value.vfield2 != 8) Wrong++;
                return true;
            }
            public void OnException(Exception exception, long numberOfRecords) => throw exception;
            public void OnStop(bool completed, long numberOfRecords) { }
        }

        private (long seen, long wrong, int windows) WalkInWindows()
        {
            var counter = new Counter();
            var windows = 0;
            var pageSize = 1L << PageBits;
            for (var from = store.Log.BeginAddress; from < store.Log.TailAddress; windows++)
            {
                var to = Math.Min((from / pageSize + WindowPages) * pageSize, store.Log.TailAddress);
                _ = store.Log.Scan(ref counter, from, to, DiskScanBufferingMode.MultiPageBuffering);
                from = to;
            }
            return (counter.Seen, counter.Wrong, windows);
        }

        [Test]
        [Category("TsavoriteKV")]
        public void A_walk_in_windows_borrows_the_frame_slots_the_last_window_handed_back()
        {
            var bContext = session.BasicContext;
            var value = new ValueStruct { vfield1 = 7, vfield2 = 8 };
            for (var i = 0; i < Records; i++)
            {
                var key = new KeyStruct { kfield1 = i, kfield2 = i };
                _ = bContext.Upsert(key, SpanByte.FromPinnedVariable(ref value), Empty.Default);
            }
            store.Log.FlushAndEvict(wait: true);
            ClassicAssert.AreEqual(store.Log.TailAddress, store.Log.HeadAddress, "CONTROL: every page is read from the device");

            _ = WalkInWindows();   // JIT, first-touch, and the slots one window needs
            var before = GC.GetAllocatedBytesForCurrentThread();
            var (seen, wrong, windows) = WalkInWindows();
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            var pageBytes = 1L << PageBits;
            TestContext.Progress.WriteLine($"{windows} windows of {WindowPages} pages: {seen:N0} records, allocated {allocated:N0} B ({allocated / (double)windows:N0} B/window; a page is {pageBytes:N0} B)");

            ClassicAssert.AreEqual(Records, seen, "CONTROL: the walk hands every record out once");
            ClassicAssert.AreEqual(0, wrong, "a borrowed slot is read as the page it was loaded with, never what the last scan left in it");
            ClassicAssert.Greater(windows, 10, "CONTROL: the walk is many windows");
            ClassicAssert.Less(allocated, windows * pageBytes, $"the walk allocated {allocated / (double)windows:N0} B a window, a page or more: each window pinned its own frame slots");
        }
    }
}
