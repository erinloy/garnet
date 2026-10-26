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
    /// A read-cache hit in the cache's oldest fraction is copied to its tail and the old copy marked Invalid. A copy cached for a key with
    /// no log record ends its chain at kInvalidAddress (0), so clearing its Valid bit left a zero RecordInfo, which ReadCacheEvict takes for
    /// a page's padding: the copy was never unlinked, and once its page closed every reader of the bucket spun in
    /// ReadCacheNeedToWaitForEviction forever (Ziltch broker 10-26, 40 threads, minutes after each start).
    /// </summary>
    [TestFixture]
    internal class APromotedReadCacheCopyOfAKeyWithNoLogRecordLeavesItsChainTests : TestBase
    {
        private TsavoriteKV<StructStoreFunctions, StructAllocator> store;
        private ClientSession<KeyStruct, InputStruct, OutputStruct, Empty, Functions, StructStoreFunctions, StructAllocator> session;
        private IDevice log;

        [SetUp]
        public void Setup()
        {
            DeleteDirectory(MethodTestDir, wait: true);
            log = Devices.CreateLogDevice(Path.Join(MethodTestDir, "promoted.log"), deleteOnClose: true);
            store = new(new KVSettings { IndexSize = 1L << 13, LogDevice = log, PageSize = 1L << 16, LogMemorySize = 1L << 20, SegmentSize = 1L << 22,
                                         ReadCacheEnabled = true, ReadCacheMemorySize = 1L << 20, ReadCachePageSize = 1L << 16 }
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
        public void A_promoted_copy_leaves_its_chain_when_its_page_closes([Values] bool noLogRecord)
        {
            var bContext = session.BasicContext;
            var key = new KeyStruct { kfield1 = 7, kfield2 = 1 };
            var value = new ValueStruct { vfield1 = 7, vfield2 = 8 };
            if (!noLogRecord)
            {
                // CONTROL arm: the key's chain ends at its log record, so the old copy's RecordInfo keeps an address when it is invalidated.
                _ = bContext.Upsert(key, SpanByte.FromPinnedVariable(ref value), Empty.Default);
                store.Log.FlushAndEvict(wait: true);
            }
            ClassicAssert.IsTrue(bContext.ConditionalInsertReadCache(key, SpanByte.FromPinnedVariable(ref value), store.Log.TailAddress), "CONTROL: the key is cached");
            var old = HeadOf(key);

            var neighbour = 1000;
            while (store.readcacheBase.SafeReadOnlyAddress <= old)
                Cache(neighbour++);
            ClassicAssert.GreaterOrEqual(old, store.readcacheBase.HeadAddress, "CONTROL: the old copy is in the cache's oldest fraction, still in memory");

            InputStruct input = default;
            OutputStruct output = default;
            ClassicAssert.IsTrue(bContext.Read(key, ref input, ref output).Found, "CONTROL: the hit is served");
            ClassicAssert.AreEqual(1, store.ReadCachePromotions.Promoted, "CONTROL: the hit was copied to the cache's tail");
            ClassicAssert.Greater(HeadOf(key), old, "CONTROL: the copy heads the chain");

            while (store.readcacheBase.ClosedUntilAddress <= old)
                Cache(neighbour++);

            HashEntryInfo hei = new(store.storeFunctions.GetKeyHashCode64(key));
            ClassicAssert.IsTrue(store.FindTag(ref hei), "CONTROL: the key keeps its tag");
            for (var at = hei.Address; LogAddress.IsReadCache(at); at = LogRecord.GetInfo(store.readcacheBase.GetPhysicalAddress(at)).PreviousAddress)
                ClassicAssert.GreaterOrEqual(LogAddress.AbsoluteAddress(at), store.readcacheBase.HeadAddress,
                    $"no chain names a closed read-cache page: {LogAddress.AbsoluteAddress(at)} is below the cache's head, and every reader of the bucket would spin on it");

            var newValue = new ValueStruct { vfield1 = 9, vfield2 = 10 };
            ClassicAssert.IsTrue(Task.Run(() =>
            {
                using var writer = store.NewSession<KeyStruct, InputStruct, OutputStruct, Empty, Functions>(new Functions());
                _ = writer.BasicContext.Upsert(key, SpanByte.FromPinnedVariable(ref newValue), Empty.Default);
            }).Wait(TimeSpan.FromSeconds(10)), "an update walks the whole chain and returns");
            ClassicAssert.IsTrue(bContext.Read(key, ref input, ref output).Found);
            ClassicAssert.AreEqual(9, output.value.vfield1, "and the key reads its new value");
        }

        private long HeadOf(KeyStruct key)
        {
            HashEntryInfo hei = new(store.storeFunctions.GetKeyHashCode64(key));
            ClassicAssert.IsTrue(store.FindTag(ref hei) && hei.IsReadCache, "CONTROL: a read-cache record heads the key's chain");
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
