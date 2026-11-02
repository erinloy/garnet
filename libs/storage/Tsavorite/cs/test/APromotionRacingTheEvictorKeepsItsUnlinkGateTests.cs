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
    /// A hot key is promoted again and again, so its chain holds its older copies: the new copy, then the old copy, then the copy before that.
    /// The second chance marks the old copy Invalid while the evictor closing the page below it unlinks the copy before by CAS on the old
    /// copy's RecordInfo (TryUpdateAddress). With a plain read-modify-write the promotion wrote the old PreviousAddress back after the evictor
    /// had marked the evicted copy out of every chain, so its page closed while still linked (Ziltch broker 11-02 07:18Z: a checkpoint walk met
    /// read-cache address 42365112 below ClosedUntilAddress 184549376, 17 minutes into a life, with no overstated record size).
    /// The new copy's validation after its head CAS is the second site: the evictor unlinks the old copy by CAS on the new copy's word.
    /// Each seam runs the evictor's unlink exactly where it races.
    /// </summary>
    [TestFixture]
    internal class APromotionRacingTheEvictorKeepsItsUnlinkGateTests : TestBase
    {
        private TsavoriteKV<StructStoreFunctions, StructAllocator> store;
        private ClientSession<KeyStruct, InputStruct, OutputStruct, Empty, Functions, StructStoreFunctions, StructAllocator> session;
        private IDevice log;

        [SetUp]
        public void Setup()
        {
            DeleteDirectory(MethodTestDir, wait: true);
            log = Devices.CreateLogDevice(Path.Join(MethodTestDir, "promotionrace.log"), deleteOnClose: true);
            // a wide second chance: both older copies sit in the cache's oldest fraction while the newest is made
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
            {
                store.promotionBeforeInvalidateForTest = null;
                store.copyBeforeValidateForTest = null;
            }
            session?.Dispose(); session = null;
            store?.Dispose(); store = null;
            log?.Dispose(); log = null;
            OnTearDown();
        }

        [Test]
        [Category("TsavoriteKV")]
        [Category("ReadCache")]
        public void An_unlink_the_evictor_makes_during_a_promotion_is_kept()
        {
            var bContext = session.BasicContext;
            var key = new KeyStruct { kfield1 = 7, kfield2 = 1 };
            var value = new ValueStruct { vfield1 = 7, vfield2 = 8 };
            _ = bContext.Upsert(key, SpanByte.FromPinnedVariable(ref value), Empty.Default);
            store.Log.FlushAndEvict(wait: true);
            ClassicAssert.IsTrue(bContext.ConditionalInsertReadCache(key, SpanByte.FromPinnedVariable(ref value), store.Log.TailAddress), "CONTROL: the key is cached");
            var oldest = HeadOf(key);

            var neighbour = 1000;
            while (store.readcacheBase.SafeReadOnlyAddress <= oldest)
                Cache(neighbour++);
            Read(key);
            ClassicAssert.AreEqual(1, store.ReadCachePromotions.Promoted, "CONTROL: the first hit in the oldest fraction was promoted");
            var old = HeadOf(key);
            ClassicAssert.AreEqual(oldest, PreviousOf(old), "CONTROL: the old copy links the copy before it");

            while (store.readcacheBase.SafeReadOnlyAddress <= old)
                Cache(neighbour++);
            ClassicAssert.GreaterOrEqual(oldest, store.readcacheBase.HeadAddress, "CONTROL: the copy before is still in memory");

            // The evictor closing the oldest copy's page unlinks it from the old copy, between the promotion's read of that word and its write.
            var raced = 0;
            store.promotionBeforeInvalidateForTest = () =>
            {
                if (raced++ == 0)
                    store.ReadCacheEvict(oldest, oldest + 8);
            };
            Read(key);
            store.promotionBeforeInvalidateForTest = null;
            ClassicAssert.AreEqual(2, store.ReadCachePromotions.Promoted, "CONTROL: the second hit was promoted");
            ClassicAssert.GreaterOrEqual(raced, 1, "CONTROL: the evictor ran inside the promotion");
            ClassicAssert.IsFalse(LogAddress.IsReadCache(PreviousOfRaw(old)),
                $"the old copy keeps the evictor's unlink: it must not link the evicted copy at {oldest} again (MUTANT: a plain write of the word it read)");
            ClassicAssert.AreEqual(1, store.ReadCachePromotionInvalidateRetries, "the promotion met the changed word and read it again");

            ClosesCleanly(key, oldest, ref neighbour);
        }

        [Test]
        [Category("TsavoriteKV")]
        [Category("ReadCache")]
        public void An_unlink_the_evictor_makes_while_a_new_copy_is_validated_is_kept()
        {
            var bContext = session.BasicContext;
            var key = new KeyStruct { kfield1 = 7, kfield2 = 1 };
            var value = new ValueStruct { vfield1 = 7, vfield2 = 8 };
            _ = bContext.Upsert(key, SpanByte.FromPinnedVariable(ref value), Empty.Default);
            store.Log.FlushAndEvict(wait: true);
            ClassicAssert.IsTrue(bContext.ConditionalInsertReadCache(key, SpanByte.FromPinnedVariable(ref value), store.Log.TailAddress), "CONTROL: the key is cached");
            var oldest = HeadOf(key);

            var neighbour = 1000;
            while (store.readcacheBase.SafeReadOnlyAddress <= oldest)
                Cache(neighbour++);

            // The new copy is published at the head, linking the old one; the evictor closing the old copy's page unlinks it from the new copy,
            // between the copy's read of its own word and its validation.
            var raced = 0;
            store.copyBeforeValidateForTest = () =>
            {
                if (raced++ == 0)
                    store.ReadCacheEvict(oldest, oldest + 8);
            };
            Read(key);
            store.copyBeforeValidateForTest = null;
            ClassicAssert.AreEqual(1, store.ReadCachePromotions.Promoted, "CONTROL: the hit was promoted");
            ClassicAssert.GreaterOrEqual(raced, 1, "CONTROL: the evictor ran inside the copy");
            var copy = HeadOf(key);
            ClassicAssert.Greater(copy, oldest, "CONTROL: the new copy heads the chain");
            ClassicAssert.IsFalse(LogAddress.IsReadCache(PreviousOfRaw(copy)),
                $"the new copy keeps the evictor's unlink: it must not link the evicted copy at {oldest} again (MUTANT: a plain write of the word it read)");
            ClassicAssert.AreEqual(1, store.ReadCacheCopyValidateRetries, "the copy met the changed word and read it again");

            ClosesCleanly(key, oldest, ref neighbour);
        }

        // Closes the evicted copy's page, then no chain of the key names a closed page and a read of it returns.
        private void ClosesCleanly(KeyStruct key, long evicted, ref int neighbour)
        {
            while (store.readcacheBase.ClosedUntilAddress <= evicted)
                Cache(neighbour++);
            HashEntryInfo hei = new(store.storeFunctions.GetKeyHashCode64(key));
            ClassicAssert.IsTrue(store.FindTag(ref hei), "CONTROL: the key keeps its tag");
            for (var at = hei.Address; LogAddress.IsReadCache(at); at = LogRecord.GetInfo(store.readcacheBase.GetPhysicalAddress(at)).PreviousAddress)
                ClassicAssert.GreaterOrEqual(LogAddress.AbsoluteAddress(at), store.readcacheBase.HeadAddress,
                    $"no chain names a closed read-cache page: {LogAddress.AbsoluteAddress(at)} is below the cache's head");

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

        private long PreviousOfRaw(long readCacheAddress) => LogRecord.GetInfo(store.readcacheBase.GetPhysicalAddress(readCacheAddress)).PreviousAddress;

        private long PreviousOf(long readCacheAddress) => LogAddress.AbsoluteAddress(PreviousOfRaw(readCacheAddress));

        private void Cache(int kfield1)
        {
            var key = new KeyStruct { kfield1 = kfield1, kfield2 = 1 };
            var value = new ValueStruct { vfield1 = kfield1, vfield2 = 0 };
            _ = session.BasicContext.ConditionalInsertReadCache(key, SpanByte.FromPinnedVariable(ref value), store.Log.TailAddress);
        }
    }
}
