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
    /// A key with no main-log record (the answer ConditionalInsertReadCache caches for a key held only in a cold tier) is cached, then promoted
    /// twice: newest -> old -> oldest -> 0. The second chance clears the older copies' Valid bit, and their Sealed bit was cleared at
    /// validation, so a copy whose PreviousAddress is 0 had a RecordInfo word of 0. The oldest copy's word is 0 from its promotion on. The old
    /// copy's word becomes 0 when the evictor unlinks the oldest from under it and copies its 0 in. The evictor took a zero word for an
    /// allocation never written and skipped it, so the copy above kept a link into the closed page (Ziltch broker-mix probe 11-02, cause A:
    /// every index checkpoint after it threw ReadCacheChainLostException). A read-cache record now carries a marker bit from its write on.
    /// </summary>
    [TestFixture]
    internal class APromotedCopyWithNoOlderRecordIsStillEvictedGateTests : TestBase
    {
        private TsavoriteKV<StructStoreFunctions, StructAllocator> store;
        private ClientSession<KeyStruct, InputStruct, OutputStruct, Empty, Functions, StructStoreFunctions, StructAllocator> session;
        private IDevice log;

        [SetUp]
        public void Setup()
        {
            DeleteDirectory(MethodTestDir, wait: true);
            log = Devices.CreateLogDevice(Path.Join(MethodTestDir, "promotedcopy.log"), deleteOnClose: true);
            store = new(new KVSettings { IndexSize = 1L << 13, LogDevice = log, PageSize = 1L << 16, LogMemorySize = 1L << 20, SegmentSize = 1L << 22,
                                         ReadCacheEnabled = true, ReadCacheMemorySize = 1L << 20, ReadCachePageSize = 1L << 16, ReadCacheSecondChanceFraction = 0.9 }
                , StoreFunctions.Create(KeyStruct.Comparer.Instance, SpanByteRecordTriggers.Instance)
                , (allocatorSettings, storeFunctions) => new(allocatorSettings, storeFunctions));
            session = store.NewSession<KeyStruct, InputStruct, OutputStruct, Empty, Functions>(new Functions());
        }

        [TearDown]
        public void TearDown()
        {
            if (store != null)
                store.readCacheRecordMarkOffForTest = false;
            session?.Dispose(); session = null;
            store?.Dispose(); store = null;
            log?.Dispose(); log = null;
            OnTearDown();
        }

        [Test]
        [Category("TsavoriteKV")]
        [Category("ReadCache")]
        public void A_copy_promoted_away_with_no_older_record_is_evicted_before_its_page_closes()
        {
            var (key, unwritten, closedLink) = PromoteTwiceAndClose(markOff: false);
            ClassicAssert.AreEqual(0, unwritten, "no written copy was taken for an unwritten start (MUTANT: no marker, the old copy's word is 0)");
            ClassicAssert.AreEqual(-1L, closedLink, $"no chain names a closed read-cache page: {closedLink} is below the cache's head");

            ClassicAssert.IsTrue(Task.Run(() =>
            {
                using var reader = store.NewSession<KeyStruct, InputStruct, OutputStruct, Empty, Functions>(new Functions());
                InputStruct input = default;
                OutputStruct output = default;
                var status = reader.BasicContext.Read(key, ref input, ref output);
                if (status.IsPending)
                    _ = reader.BasicContext.CompletePendingWithOutputs(out var outputs, wait: true);
            }).Wait(TimeSpan.FromSeconds(10)), "a read of the key walks its chain and returns");
        }

        [Test]
        [Category("TsavoriteKV")]
        [Category("ReadCache")]
        public void CONTROL_without_the_marker_the_old_copy_is_skipped_and_its_link_outlives_its_page()
        {
            // The probe's cause kept visible: with the marker off, the same steps leave the newest copy linked into a closed page.
            var (_, unwritten, closedLink) = PromoteTwiceAndClose(markOff: true);
            ClassicAssert.Greater(unwritten, 0, "CONTROL: without the marker the old copy reads as an unwritten start");
            ClassicAssert.AreNotEqual(-1L, closedLink, "CONTROL: without the marker a chain keeps a link into a closed page");
        }

        // Returns the evictor's unwritten-start count and the first chain address below the cache's head (-1 when none).
        private (KeyStruct Key, long Unwritten, long ClosedLink) PromoteTwiceAndClose(bool markOff)
        {
            store.readCacheRecordMarkOffForTest = markOff;
            var bContext = session.BasicContext;
            var key = new KeyStruct { kfield1 = 7, kfield2 = 1 };
            var value = new ValueStruct { vfield1 = 7, vfield2 = 8 };
            ClassicAssert.IsTrue(bContext.ConditionalInsertReadCache(key, SpanByte.FromPinnedVariable(ref value), store.Log.TailAddress),
                "CONTROL: the key with no main-log record is cached");
            var oldest = HeadOf(key);
            ClassicAssert.AreEqual(0L, PreviousOfRaw(oldest), "CONTROL: the oldest copy links nothing below it");

            var neighbour = 1000;
            while (store.readcacheBase.SafeReadOnlyAddress <= oldest)
                Cache(neighbour++);
            Read(key);
            var old = HeadOf(key);
            ClassicAssert.Greater(old, oldest, "CONTROL: the first hit was promoted");
            // Without the marker the promotion leaves Valid on a copy whose word would read 0; with it the copy is invalidated and stays nonzero.
            if (!markOff)
            {
                ClassicAssert.IsFalse(InfoOf(oldest).Valid, "the second chance invalidated the oldest copy");
                ClassicAssert.IsFalse(InfoOf(oldest).IsNull, "a copy promoted away with no older record keeps a nonzero word");
            }

            while (store.readcacheBase.SafeReadOnlyAddress <= old)
                Cache(neighbour++);
            Read(key);
            var newest = HeadOf(key);
            ClassicAssert.Greater(newest, old, "CONTROL: the second hit was promoted");
            ClassicAssert.AreEqual(oldest, LogAddress.AbsoluteAddress(PreviousOfRaw(old)), "CONTROL: the old copy still links the oldest");

            // Closing the oldest copy's page unlinks it from under the old copy (an interior unlink copying its 0 in); closing the old copy's
            // page must then unlink the old copy from under the newest.
            while (store.readcacheBase.ClosedUntilAddress <= old)
                Cache(neighbour++);
            HashEntryInfo hei = new(store.storeFunctions.GetKeyHashCode64(key));
            ClassicAssert.IsTrue(store.FindTag(ref hei), "CONTROL: the key keeps its tag");
            var closedLink = -1L;
            for (var at = hei.Address; LogAddress.IsReadCache(at); at = LogRecord.GetInfo(store.readcacheBase.GetPhysicalAddress(at)).PreviousAddress)
            {
                if (LogAddress.AbsoluteAddress(at) < store.readcacheBase.HeadAddress)
                {
                    closedLink = LogAddress.AbsoluteAddress(at);
                    break;
                }
            }
            return (key, store.ReadCacheUnwrittenStarts, closedLink);
        }

        private void Read(KeyStruct key)
        {
            InputStruct input = default;
            OutputStruct output = default;
            ClassicAssert.IsTrue(session.BasicContext.Read(key, ref input, ref output).Found, "CONTROL: the hit is served from the read cache");
            ClassicAssert.AreEqual(7, output.value.vfield1);
        }

        private long HeadOf(KeyStruct key)
        {
            HashEntryInfo hei = new(store.storeFunctions.GetKeyHashCode64(key));
            ClassicAssert.IsTrue(store.FindTag(ref hei) && hei.IsReadCache, "CONTROL: a read-cache record heads the key's chain");
            return LogAddress.AbsoluteAddress(hei.Address);
        }

        private RecordInfo InfoOf(long readCacheAddress) => LogRecord.GetInfo(store.readcacheBase.GetPhysicalAddress(readCacheAddress));

        private long PreviousOfRaw(long readCacheAddress) => InfoOf(readCacheAddress).PreviousAddress;

        private void Cache(int kfield1)
        {
            var key = new KeyStruct { kfield1 = kfield1, kfield2 = 1 };
            var value = new ValueStruct { vfield1 = kfield1, vfield2 = 0 };
            _ = session.BasicContext.ConditionalInsertReadCache(key, SpanByte.FromPinnedVariable(ref value), store.Log.TailAddress);
        }
    }
}
