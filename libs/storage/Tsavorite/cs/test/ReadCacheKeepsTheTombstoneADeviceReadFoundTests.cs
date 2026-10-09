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
    /// A read that went to the device and found a tombstone leaves that tombstone in the read cache, as it leaves a live record there:
    /// the next read of the deleted key is answered in memory. Before this, every read of a deleted key whose tombstone had left memory paid the device again.
    /// </summary>
    [TestFixture]
    internal class ReadCacheKeepsTheTombstoneADeviceReadFoundTests : TestBase
    {
        private TsavoriteKV<StructStoreFunctions, StructAllocator> store;
        private ClientSession<KeyStruct, InputStruct, OutputStruct, Empty, QuietFunctions, StructStoreFunctions, StructAllocator> session;
        private BasicContext<KeyStruct, InputStruct, OutputStruct, Empty, QuietFunctions, StructStoreFunctions, StructAllocator> bContext;
        private IDevice log;

        private void Open(bool readCache, bool keepDeleteTombstones = false)
        {
            DeleteDirectory(MethodTestDir, wait: true);
            log = Devices.CreateLogDevice(Path.Join(MethodTestDir, "cached.log"), deleteOnClose: true);
            store = new(new KVSettings { IndexSize = 1L << 13, LogDevice = log, PageSize = MinKvLogPageSize, LogMemorySize = 1L << 15, SegmentSize = 1L << 22,
                                         KeepDeleteTombstones = keepDeleteTombstones,
                                         ReadCacheEnabled = readCache, ReadCacheMemorySize = 1L << 15, ReadCachePageSize = MinKvLogPageSize }
                , StoreFunctions.Create(KeyStruct.Comparer.Instance, SpanByteRecordTriggers.Instance)
                , (allocatorSettings, storeFunctions) => new(allocatorSettings, storeFunctions));
            session = store.NewSession<KeyStruct, InputStruct, OutputStruct, Empty, QuietFunctions>(new QuietFunctions());
            bContext = session.BasicContext;
        }

        [TearDown]
        public void TearDown()
        {
            session?.Dispose(); session = null;
            store?.Dispose(); store = null;
            log?.Dispose(); log = null;
            OnTearDown();
        }

        /// <summary>One read: its answer, whether it had to wait for the device, and what it read.</summary>
        private (Status Status, bool WentToDevice, OutputStruct Output) Read(KeyStruct key, bool reportTombstone = true)
        {
            InputStruct input = default;
            OutputStruct output = default;
            ReadOptions options = new() { ReportTombstone = reportTombstone };
            var status = bContext.Read(key, ref input, ref output, ref options, Empty.Default);
            if (!status.IsPending) return (status, false, output);
            _ = bContext.CompletePendingWithOutputs(out var outputs, wait: true);
            (status, output) = GetSinglePendingResult(outputs);
            return (status, true, output);
        }

        /// <summary>A key written, its record pushed to the device, deleted, and the tombstone pushed to the device too.</summary>
        private KeyStruct ADeletedKeyWhoseTombstoneLeftMemory(int id)
        {
            var key = new KeyStruct { kfield1 = id, kfield2 = id + 1 };
            var value = new ValueStruct { vfield1 = 11, vfield2 = 12 };
            _ = bContext.Upsert(key, SpanByte.FromPinnedVariable(ref value), Empty.Default);
            store.Log.FlushAndEvict(wait: true);
            _ = bContext.Delete(key, Empty.Default);
            store.Log.FlushAndEvict(wait: true);
            return key;
        }

        [Test]
        [Category("TsavoriteKV")]
        [Category("ReadCache")]
        public void The_second_read_of_a_deleted_key_whose_tombstone_left_memory_costs_no_device_read([Values] bool readCache, [Values] bool keepDeleteTombstones)
        {
            Open(readCache, keepDeleteTombstones);
            var key = ADeletedKeyWhoseTombstoneLeftMemory(311);

            var first = Read(key);
            ClassicAssert.IsTrue(first.WentToDevice, "CONTROL: the tombstone is on the device, so the first read waits for it");
            ClassicAssert.IsTrue(first.Status.NotFound && first.Status.IsTombstoned, first.Status.ToString());

            var hits = store.ReadCacheHits;
            var second = Read(key);
            ClassicAssert.IsTrue(second.Status.NotFound && second.Status.IsTombstoned, $"the key still reads deleted ({second.Status})");
            if (readCache)
            {
                ClassicAssert.IsFalse(second.WentToDevice, "the read cache kept the tombstone the first read found: the second read costs no device read");
                ClassicAssert.AreEqual(hits + 1, store.ReadCacheHits, "and it is counted as the cache's answer");
            }
            else
                ClassicAssert.IsTrue(second.WentToDevice, "CONTROL: with no read cache every read of the deleted key waits for the device");
        }

        [Test]
        [Category("TsavoriteKV")]
        [Category("ReadCache")]
        public void A_read_that_did_not_ask_for_the_tombstone_is_not_found_from_the_cache_and_never_a_record()
        {
            Open(readCache: true);
            var key = ADeletedKeyWhoseTombstoneLeftMemory(321);

            var first = Read(key, reportTombstone: false);
            ClassicAssert.IsTrue(first.WentToDevice);
            ClassicAssert.IsTrue(first.Status.NotFound && !first.Status.IsTombstoned, first.Status.ToString());

            var second = Read(key, reportTombstone: false);
            ClassicAssert.IsFalse(second.WentToDevice, "answered from the cache");
            ClassicAssert.IsFalse(second.Status.Found, $"a cached tombstone is never served as a record ({second.Status})");
            ClassicAssert.IsTrue(second.Status.NotFound && !second.Status.IsTombstoned, $"and a read that did not ask is told nothing more ({second.Status})");
        }

        [Test]
        [Category("TsavoriteKV")]
        [Category("ReadCache")]
        public void A_live_record_is_cached_and_read_as_before()
        {
            Open(readCache: true);
            var key = new KeyStruct { kfield1 = 331, kfield2 = 332 };
            var value = new ValueStruct { vfield1 = 21, vfield2 = 22 };
            _ = bContext.Upsert(key, SpanByte.FromPinnedVariable(ref value), Empty.Default);
            store.Log.FlushAndEvict(wait: true);

            var first = Read(key);
            ClassicAssert.IsTrue(first.WentToDevice && first.Status.Found, first.Status.ToString());
            var second = Read(key);
            ClassicAssert.IsFalse(second.WentToDevice, "CONTROL: a live record the device read found is in the cache");
            ClassicAssert.IsTrue(second.Status.Found && !second.Status.IsTombstoned, second.Status.ToString());
            ClassicAssert.AreEqual(21, second.Output.value.vfield1);
        }

        [Test]
        [Category("TsavoriteKV")]
        [Category("ReadCache")]
        public void A_write_over_a_cached_tombstone_is_what_the_next_read_finds()
        {
            Open(readCache: true);
            var (upserted, updated, deletedAgain) = (ADeletedKeyWhoseTombstoneLeftMemory(341), ADeletedKeyWhoseTombstoneLeftMemory(351), ADeletedKeyWhoseTombstoneLeftMemory(361));
            foreach (var key in new[] { upserted, updated, deletedAgain })
            {
                _ = Read(key);
                ClassicAssert.IsFalse(Read(key).WentToDevice, "the tombstone is cached");
            }

            var written = new ValueStruct { vfield1 = 31, vfield2 = 32 };
            _ = bContext.Upsert(upserted, SpanByte.FromPinnedVariable(ref written), Empty.Default);
            var afterUpsert = Read(upserted);
            ClassicAssert.IsTrue(afterUpsert.Status.Found, $"an upsert over the cached tombstone answers ({afterUpsert.Status})");
            ClassicAssert.AreEqual(31, afterUpsert.Output.value.vfield1);

            // The test functions' update of an absent key writes its input; of a present one it ADDS the input. A deleted key is absent.
            var input = new InputStruct { ifield1 = 5, ifield2 = 6 };
            var rmw = bContext.RMW(updated, ref input, Empty.Default);
            if (rmw.IsPending) _ = bContext.CompletePending(wait: true);
            var afterUpdate = Read(updated);
            ClassicAssert.IsTrue(afterUpdate.Status.Found, afterUpdate.Status.ToString());
            ClassicAssert.AreEqual(5, afterUpdate.Output.value.vfield1, "an update over the cached tombstone starts from nothing, not from the deleted value (11) or an empty one");

            _ = bContext.Delete(deletedAgain, Empty.Default);
            var afterDelete = Read(deletedAgain);
            ClassicAssert.IsTrue(afterDelete.Status.NotFound && afterDelete.Status.IsTombstoned, $"a second delete leaves the key deleted ({afterDelete.Status})");
        }

        [Test]
        [Category("TsavoriteKV")]
        [Category("ReadCache")]
        public void A_cached_tombstone_is_evicted_like_any_entry_and_the_device_answers_again()
        {
            Open(readCache: true);
            var key = ADeletedKeyWhoseTombstoneLeftMemory(371);
            _ = Read(key);
            ClassicAssert.IsFalse(Read(key).WentToDevice, "the tombstone is cached");

            store.ReadCache.FlushAndEvict(wait: true);

            var after = Read(key);
            ClassicAssert.IsTrue(after.WentToDevice, "the cache let it go, so the device is asked again");
            ClassicAssert.IsTrue(after.Status.NotFound && after.Status.IsTombstoned, $"and the answer is the same ({after.Status})");
        }
    }
}
