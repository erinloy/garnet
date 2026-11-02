// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.IO;
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
    /// The read-cache evictor finds the records of a closing page by the start bit each allocation set, not by striding from one record
    /// to the next by its stated size. A record whose header overstates its size made the stride walk step over its neighbour, which then
    /// stayed linked into a closed page: every later walk of that key's chain met a link below ClosedUntilAddress (the dangling links of
    /// 10-30 and 11-02, whose cause is still unproven). Here a header is overstated by hand; the neighbour is unlinked all the same, and
    /// the overshoot is counted.
    /// </summary>
    [TestFixture]
    internal class AReadCacheRecordHiddenByAnOverstatedNeighbourIsStillEvictedGateTests : TestBase
    {
        static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

        private TsavoriteKV<StructStoreFunctions, StructAllocator> store;
        private ClientSession<KeyStruct, InputStruct, OutputStruct, Empty, Functions, StructStoreFunctions, StructAllocator> session;
        private IDevice log;

        [SetUp]
        public void Setup()
        {
            DeleteDirectory(MethodTestDir, wait: true);
            log = Devices.CreateLogDevice(Path.Join(MethodTestDir, "hidden.log"), deleteOnClose: true);
            store = new(new KVSettings { IndexSize = 1L << 13, LogDevice = log, PageSize = 1L << 12, LogMemorySize = 1L << 16, SegmentSize = 1L << 22,
                                         CheckpointDir = MethodTestDir, ReadCacheEnabled = true, ReadCacheMemorySize = 1L << 15, ReadCachePageSize = 1L << 12 }
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

        [Test]
        [Category("TsavoriteKV")]
        [Category("ReadCache")]
        public unsafe void A_record_an_overstated_header_hides_is_unlinked_when_its_page_closes()
        {
            var bContext = session.BasicContext;
            var hider = new KeyStruct { kfield1 = 11, kfield2 = 1 };
            var hidden = new KeyStruct { kfield1 = 12, kfield2 = 1 };
            foreach (var key in new[] { hider, hidden })
            {
                var value = new ValueStruct { vfield1 = key.kfield1, vfield2 = key.kfield2 };
                _ = bContext.Upsert(key, SpanByte.FromPinnedVariable(ref value), Empty.Default);
            }
            store.Log.FlushAndEvict(wait: true);

            var hiderAt = CacheAndFind(hider);
            var hiddenAt = CacheAndFind(hidden);
            var hiderRecord = new LogRecord(store.readcacheBase.GetPhysicalAddress(hiderAt));
            var hiddenRecord = new LogRecord(store.readcacheBase.GetPhysicalAddress(hiddenAt));
            ClassicAssert.AreEqual(hiderAt + hiderRecord.AllocatedSize, hiddenAt, "CONTROL: the two copies are neighbours on one read-cache page");

            // The hider's header now says it runs to the end of its neighbour: a stride from it lands past the hidden record.
            var header = hiderRecord.DataHeader;
            header.FillerWords += hiddenRecord.AllocatedSize >> 3;
            hiderRecord.SetDataHeader(header);
            ClassicAssert.AreEqual(hiddenAt + hiddenRecord.AllocatedSize, hiderAt + hiderRecord.AllocatedSize, "CONTROL: the stride from the hider steps over the hidden record");

            var neighbour = 1000;
            while (store.readcacheBase.ClosedUntilAddress <= hiddenAt)
                Cache(neighbour++);

            HashEntryInfo hei = new(store.storeFunctions.GetKeyHashCode64(hidden));
            ClassicAssert.IsTrue(store.FindTag(ref hei), "CONTROL: the hidden key keeps its tag");
            ClassicAssert.IsFalse(hei.IsReadCache && LogAddress.AbsoluteAddress(hei.Address) < store.readcacheBase.ClosedUntilAddress,
                $"the hidden record's link survived its page's close: the slot names read-cache address {LogAddress.AbsoluteAddress(hei.Address)} "
                + $"below ClosedUntilAddress {store.readcacheBase.ClosedUntilAddress}, so every walk of the key meets a lost link");
            ClassicAssert.GreaterOrEqual(store.ReadCacheStrideOvershoots, 1, "the evictor counts the record the stated size hid");

            var read = Task.Run(() =>
            {
                using var reader = store.NewSession<KeyStruct, InputStruct, OutputStruct, Empty, Functions>(new Functions());
                InputStruct input = default;
                OutputStruct output = default;
                var status = reader.BasicContext.Read(hidden, ref input, ref output);
                if (status.IsPending)
                    _ = reader.BasicContext.CompletePending(wait: true);
            });
            ClassicAssert.IsTrue(read.Wait(Bound) && read.Status == TaskStatus.RanToCompletion,
                $"a read of the hidden key ends within {Bound} without a lost link: {read.Exception?.GetBaseException().Message}");
            ClassicAssert.AreEqual(0, store.ReadCacheMissedEvictions, "no walk met a lost link");
        }

        private long CacheAndFind(KeyStruct key)
        {
            var value = new ValueStruct { vfield1 = key.kfield1, vfield2 = key.kfield2 };
            ClassicAssert.IsTrue(session.BasicContext.ConditionalInsertReadCache(key, SpanByte.FromPinnedVariable(ref value), store.Log.TailAddress), "CONTROL: the key is cached");
            HashEntryInfo hei = new(store.storeFunctions.GetKeyHashCode64(key));
            ClassicAssert.IsTrue(store.FindTag(ref hei) && hei.IsReadCache, "CONTROL: a read-cache copy heads the key's chain");
            return LogAddress.AbsoluteAddress(hei.Address);
        }

        private void Cache(int kfield1)
        {
            var key = new KeyStruct { kfield1 = kfield1, kfield2 = 1 };
            var value = new ValueStruct { vfield1 = kfield1, vfield2 = 0 };
            _ = session.BasicContext.ConditionalInsertReadCache(key, SpanByte.FromPinnedVariable(ref value), store.Log.TailAddress);
        }
    }
}
