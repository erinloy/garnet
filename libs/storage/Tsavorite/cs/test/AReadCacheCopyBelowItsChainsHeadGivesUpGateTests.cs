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
    /// A read-cache copy whose every allocation lands at or below the read-cache record heading its key's chain gives the copy up after a
    /// bounded number of tries. It never spins under the epoch. Ziltch matrix-api 11-04: 124 threads spun in TryAllocateRecordReadCache under
    /// ConditionalInsertReadCache, the epoch table filled, and ~400 operations of the store waited for a slot that never freed.
    /// </summary>
    [TestFixture]
    internal class AReadCacheCopyBelowItsChainsHeadGivesUpGateTests : TestBase
    {
        private TsavoriteKV<StructStoreFunctions, StructAllocator> store;
        private ClientSession<KeyStruct, InputStruct, OutputStruct, Empty, Functions, StructStoreFunctions, StructAllocator> session;
        private IDevice log;

        [SetUp]
        public void Setup()
        {
            DeleteDirectory(MethodTestDir, wait: true);
            log = Devices.CreateLogDevice(Path.Join(MethodTestDir, "copybelowhead.log"), deleteOnClose: true);
            store = new(new KVSettings { IndexSize = 1L << 13, LogDevice = log, PageSize = 1L << 16, LogMemorySize = 1L << 20, SegmentSize = 1L << 22,
                                         ReadCacheEnabled = true, ReadCacheMemorySize = 1L << 20, ReadCachePageSize = 1L << 16 }
                , StoreFunctions.Create(KeyStruct.Comparer.Instance, SpanByteRecordTriggers.Instance)
                , (allocatorSettings, storeFunctions) => new(allocatorSettings, storeFunctions));
            session = store.NewSession<KeyStruct, InputStruct, OutputStruct, Empty, Functions>(new Functions());
        }

        [TearDown]
        public void TearDown()
        {
            if (store != null)
            {
                store.readCacheAllocateBelowHeadForTest = null;
                store.readCacheAllocateNoPageForTest = false;
            }
            session?.Dispose(); session = null;
            store?.Dispose(); store = null;
            log?.Dispose(); log = null;
            OnTearDown();
        }

        private bool Offer(int kfield1)
        {
            var key = new KeyStruct { kfield1 = kfield1, kfield2 = 1 };
            var value = new ValueStruct { vfield1 = kfield1, vfield2 = 2 };
            return session.BasicContext.ConditionalInsertReadCache(key, SpanByte.FromPinnedVariable(ref value), store.Log.TailAddress);
        }

        [Test]
        [Category("TsavoriteKV")]
        [Category("ReadCache")]
        public void A_copy_whose_allocations_all_land_below_the_chains_head_is_given_up_and_counted()
        {
            store.readCacheAllocateBelowHeadForTest = static () => true;
            var offered = Task.Run(() => Offer(7));
            ClassicAssert.IsTrue(offered.Wait(TimeSpan.FromSeconds(10)), "the offer returns (MUTANT: the unbounded loop spins under the epoch for good)");
            ClassicAssert.IsFalse(offered.Result, "a copy that found no room above the head is not cached");
            ClassicAssert.AreEqual(1, store.ReadCacheAllocateSpunOut, "the given-up copy is counted");

            store.readCacheAllocateBelowHeadForTest = null;
            ClassicAssert.IsTrue(Task.Run(() =>
            {
                using var reader = store.NewSession<KeyStruct, InputStruct, OutputStruct, Empty, Functions>(new Functions());
                InputStruct input = default;
                OutputStruct output = default;
                var status = reader.BasicContext.Read(new KeyStruct { kfield1 = 8, kfield2 = 1 }, ref input, ref output);
                if (status.IsPending)
                    _ = reader.BasicContext.CompletePendingWithOutputs(out var outputs, wait: true);
            }).Wait(TimeSpan.FromSeconds(10)), "the store's epoch is free: another operation runs");
        }

        [Test]
        [Category("TsavoriteKV")]
        [Category("ReadCache")]
        public void A_copy_that_finds_no_read_cache_page_ready_is_given_up_never_waited()
        {
            store.readCacheAllocateNoPageForTest = true;
            var offered = Task.Run(() => Offer(11));
            ClassicAssert.IsTrue(offered.Wait(TimeSpan.FromSeconds(10)), "the offer returns");
            ClassicAssert.IsFalse(offered.Result, "a copy with no page ready is not cached");
            ClassicAssert.AreEqual(1, store.ReadCacheAllocateNoPage, "the given-up copy is counted (MUTANT: the flush-event wait path counts nothing)");
        }

        [Test]
        [Category("TsavoriteKV")]
        [Category("ReadCache")]
        public void CONTROL_a_copy_with_room_above_the_head_lands_and_nothing_is_given_up()
        {
            ClassicAssert.IsTrue(Offer(9), "an offer for a key with no record lands in the read cache");
            ClassicAssert.AreEqual(0, store.ReadCacheAllocateSpunOut);
            ClassicAssert.AreEqual(0, store.ReadCacheAllocateNoPage);
        }
    }
}
