// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

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
    /// A main-log allocation that lands at or below its chain's latest address is abandoned. It was marked invalid through
    /// LogRecord.GetInfo, which returns a copy, so the allocation kept no header and a scan met whatever bytes the page held there.
    /// SPARK 11-20: two mid-page spans with no record header (6,248 B and 1,760 B in hlog.log.175) stopped its hot pass by name.
    /// It is now framed at its allocated size and invalid, so a scan steps over it and hands out every live record.
    /// </summary>
    [TestFixture]
    internal class AnAbandonedMainLogAllocationIsAFramedInvalidRecordGateTests : TestBase
    {
        private TsavoriteKV<StructStoreFunctions, StructAllocator> store;
        private ClientSession<KeyStruct, InputStruct, OutputStruct, Empty, Functions, StructStoreFunctions, StructAllocator> session;
        private IDevice log;

        [SetUp]
        public void Setup()
        {
            DeleteDirectory(MethodTestDir, wait: true);
            log = Devices.CreateLogDevice(Path.Join(MethodTestDir, "abandoned.log"), deleteOnClose: true);
            store = new(new KVSettings { IndexSize = 1L << 13, LogDevice = log, PageSize = 1L << 16, LogMemorySize = 1L << 20, SegmentSize = 1L << 22 }
                , StoreFunctions.Create(KeyStruct.Comparer.Instance, SpanByteRecordTriggers.Instance)
                , (allocatorSettings, storeFunctions) => new(allocatorSettings, storeFunctions));
            session = store.NewSession<KeyStruct, InputStruct, OutputStruct, Empty, Functions>(new Functions());
        }

        [TearDown]
        public void TearDown()
        {
            if (store != null) store.mainLogAllocateBelowLatestForTest = null;
            session?.Dispose(); session = null;
            store?.Dispose(); store = null;
            log?.Dispose(); log = null;
            OnTearDown();
        }

        private void Upsert(int k)
        {
            var value = new ValueStruct { vfield1 = k, vfield2 = k };
            _ = session.BasicContext.Upsert(new KeyStruct { kfield1 = k, kfield2 = k }, SpanByte.FromPinnedVariable(ref value), Empty.Default);
        }

        private long ScanCount()
        {
            long seen = 0;
            using var iter = store.Log.Scan(store.Log.BeginAddress, store.Log.TailAddress);
            while (iter.GetNext()) seen++;
            return seen;
        }

        [Test]
        [Category("TsavoriteKV")]
        public void An_abandoned_allocation_carries_an_invalid_header_at_its_size_and_a_scan_steps_over_it()
        {
            var firstAt = store.Log.TailAddress;
            Upsert(1);
            var fired = 0;
            store.mainLogAllocateBelowLatestForTest = () => System.Threading.Interlocked.Exchange(ref fired, 1) == 0;
            var abandonedAt = store.Log.TailAddress;
            Upsert(2);
            store.mainLogAllocateBelowLatestForTest = null;
            Upsert(3);

            ClassicAssert.AreEqual(1, store.MainLogAllocationsAbandoned, "CONTROL: the seam abandoned exactly one allocation");
            var physical = store.hlogBase.GetPhysicalAddress(abandonedAt);
            var info = LogRecord.GetInfo(physical);
            ClassicAssert.IsFalse(info.IsNull, "🔑 the abandoned allocation has a header (MUTANT: GetInfo(..).SetInvalid() wrote to a copy, so the word stays 0)");
            ClassicAssert.IsTrue(info.Invalid, "the abandoned allocation is invalid, so no scan hands it out");
            ClassicAssert.AreEqual(abandonedAt - firstAt, new LogRecord(physical).AllocatedSize, "its header frames exactly the bytes it was allocated (a record of the same shape as key 1's), so a scan steps to the next record");

            ClassicAssert.AreEqual(3, ScanCount(), "an in-memory scan hands out the three live records");
            store.Log.FlushAndEvict(wait: true);
            ClassicAssert.AreEqual(3, ScanCount(), "a scan from the device hands out the three live records");
        }

        [Test]
        [Category("TsavoriteKV")]
        public void CONTROL_with_no_seam_nothing_is_abandoned()
        {
            Upsert(1); Upsert(2);
            ClassicAssert.AreEqual(0, store.MainLogAllocationsAbandoned);
            ClassicAssert.AreEqual(2, ScanCount());
        }
    }
}
