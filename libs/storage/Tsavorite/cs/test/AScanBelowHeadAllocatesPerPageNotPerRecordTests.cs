// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.IO;
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
    /// A pull scan below head calls BufferAndLoad for every record. Its loop made the page read's epoch-bump closure for every frame
    /// slot of every call, whether or not a load was issued: one router-store boot allocated 74.4 GB of those closures in 5 minutes.
    /// The closure is now made only when a page load is issued, so a scan's allocation follows its pages, not its records.
    /// </summary>
    [TestFixture]
    internal class AScanBelowHeadAllocatesPerPageNotPerRecordTests : TestBase
    {
        private const int Records = 100_000;   // enough that two page frames (2 x 64 KB) stay under the per-record bound

        private TsavoriteKV<StructStoreFunctions, StructAllocator> store;
        private ClientSession<KeyStruct, InputStruct, OutputStruct, Empty, Functions, StructStoreFunctions, StructAllocator> session;
        private IDevice log;

        [SetUp]
        public void Setup()
        {
            DeleteDirectory(MethodTestDir, wait: true);
            log = Devices.CreateLogDevice(Path.Join(MethodTestDir, "scan.log"), deleteOnClose: true);
            store = new(new KVSettings { IndexSize = 1L << 13, LogDevice = log, PageSize = 1L << 16, LogMemorySize = 1L << 20, SegmentSize = 1L << 22 }
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

        private long ScanAll(out long allocated)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            long seen = 0;
            using (var iter = store.Log.Scan(store.Log.BeginAddress, store.Log.TailAddress))
                while (iter.GetNext()) seen++;
            allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            return seen;
        }

        [Test]
        [Category("TsavoriteKV")]
        public void A_scan_below_head_allocates_with_its_pages_not_its_records()
        {
            var bContext = session.BasicContext;
            var value = new ValueStruct { vfield1 = 7, vfield2 = 8 };
            for (var i = 0; i < Records; i++)
            {
                var key = new KeyStruct { kfield1 = i, kfield2 = i };
                _ = bContext.Upsert(key, SpanByte.FromPinnedVariable(ref value), Empty.Default);
            }
            store.Log.FlushAndEvict(wait: true);
            ClassicAssert.AreEqual(store.Log.TailAddress, store.Log.HeadAddress, "CONTROL: every record is below head, so the scan reads each page from the device");

            _ = ScanAll(out _);   // JIT and first-touch
            var seen = ScanAll(out var allocated);
            var pages = (store.Log.TailAddress - store.Log.BeginAddress) / (1L << 16) + 1;
            TestContext.Progress.WriteLine($"{seen:N0} records over {pages:N0} pages allocated {allocated:N0} B ({allocated / (double)seen:0.##} B/record)");

            ClassicAssert.AreEqual(Records, seen, "CONTROL: the scan hands every record out");
            ClassicAssert.Less(allocated, Records * 8L, $"the scan allocated {allocated / (double)seen:0.##} B per record; a closure per record is 64 B or more");
        }
    }
}
