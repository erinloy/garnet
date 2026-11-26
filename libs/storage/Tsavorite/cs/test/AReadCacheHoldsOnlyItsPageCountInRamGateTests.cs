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
    /// A read cache whose page count is held under its ring (Ziltch's band: SetMaxAllocatedPageCount) holds that many pages in RAM, not the
    /// ring's. With no size tracker a closed page stayed in its slot, so once the tail had turned through every slot the whole ring was
    /// resident (keystone 11-26: 2,048 + 1,024 MB per cell against a 256 + 128 MB band). A freed page now leaves its slot.
    /// </summary>
    [TestFixture]
    internal class AReadCacheHoldsOnlyItsPageCountInRamGateTests : TestBase
    {
        private const int RingPages = 32, BandPages = 4;
        private TsavoriteKV<StructStoreFunctions, StructAllocator> store;
        private ClientSession<KeyStruct, InputStruct, OutputStruct, Empty, Functions, StructStoreFunctions, StructAllocator> session;
        private IDevice log;

        [SetUp]
        public void Setup()
        {
            DeleteDirectory(MethodTestDir, wait: true);
            log = Devices.CreateLogDevice(Path.Join(MethodTestDir, "band.log"), deleteOnClose: true);
            store = new(new KVSettings { IndexSize = 1L << 13, LogDevice = log, PageSize = 1L << 12, LogMemorySize = 1L << 16, SegmentSize = 1L << 22,
                                         ReadCacheEnabled = true, ReadCacheMemorySize = RingPages * (1L << 12), ReadCachePageSize = 1L << 12 }
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
        public void A_banded_read_cache_turned_through_its_ring_holds_only_its_band()
        {
            ClassicAssert.LessOrEqual(Turn(keepFreed: false), BandPages + 2, "the read cache holds more pages than its band once its tail turned the ring");
        }

        [Test]
        [Category("TsavoriteKV")]
        [Category("ReadCache")]
        public void CONTROL_with_freed_pages_kept_in_their_slots_the_whole_ring_is_resident()
        {
            ClassicAssert.Greater(Turn(keepFreed: true), RingPages / 2, "CONTROL: a freed page kept in its slot fills the ring");
        }

        // Caches distinct keys until the read cache's tail has passed through every slot of its ring twice; returns its allocated pages.
        private int Turn(bool keepFreed)
        {
            var cache = store.readcacheBase;
            ClassicAssert.AreEqual(RingPages, cache.BufferSize, "CONTROL: the ring is the configured size");
            cache.keepsFreedPagesForTest = keepFreed;
            cache.SetMaxAllocatedPageCount(BandPages, wait: true);
            var bContext = session.BasicContext;
            var until = cache.GetTailAddress() + 2L * RingPages * cache.PageSize;
            for (var k = 1; cache.GetTailAddress() < until; k++)
            {
                var key = new KeyStruct { kfield1 = k, kfield2 = 1 };
                var value = new ValueStruct { vfield1 = k, vfield2 = 0 };
                _ = bContext.ConditionalInsertReadCache(key, SpanByte.FromPinnedVariable(ref value), store.Log.TailAddress);
            }
            return cache.AllocatedPageCount;
        }
    }
}
