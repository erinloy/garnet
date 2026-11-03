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
    /// An operation finds its key's tag in a slot, then a scrub or lazy reclaim empties that slot (and the free slot before it) before the
    /// operation reads the slot's word, and another writer's FindOrCreateTag puts the same tag in the earlier free slot. The operation reads 0;
    /// a head CAS from 0 then succeeds and the tag sits in two slots, so FindTag (first slot wins) never sees the record placed in the second
    /// (Ziltch broker probe 11-02: read-cache copies from cold offers, and main-log deletes and retries, landed in the second slot).
    /// A CAS from 0 on a found tag is refused: the main log retries and finds the tag again, a read-cache copy is abandoned; each is counted.
    /// </summary>
    [TestFixture]
    internal unsafe class AWriteWhoseTagSlotWasEmptiedFindsItsTagAgainGateTests : TestBase
    {
        private TsavoriteKV<StructStoreFunctions, StructAllocator> store;
        private ClientSession<KeyStruct, InputStruct, OutputStruct, Empty, Functions, StructStoreFunctions, StructAllocator> session;
        private IDevice log;
        private KeyStruct keyA, keyB;
        private long hashB;
        private ushort tagA, tagB;

        [SetUp]
        public void Setup()
        {
            DeleteDirectory(MethodTestDir, wait: true);
            log = Devices.CreateLogDevice(Path.Join(MethodTestDir, "emptiedslot.log"), deleteOnClose: true);
            // one bucket: both keys' tags share it, so the earlier free slot is the one another writer's FindOrCreateTag takes
            store = new(new KVSettings { IndexSize = 64, LogDevice = log, PageSize = 1L << 16, LogMemorySize = 1L << 20, SegmentSize = 1L << 22,
                                         ReadCacheEnabled = true, ReadCacheMemorySize = 1L << 20, ReadCachePageSize = 1L << 16, KeepDeleteTombstones = true }
                , StoreFunctions.Create(KeyStruct.Comparer.Instance, SpanByteRecordTriggers.Instance)
                , (allocatorSettings, storeFunctions) => new(allocatorSettings, storeFunctions));
            session = store.NewSession<KeyStruct, InputStruct, OutputStruct, Empty, Functions>(new Functions());

            keyA = new KeyStruct { kfield1 = 1, kfield2 = 1 };
            tagA = HashBucketEntry.GetTag(store.storeFunctions.GetKeyHashCode64(keyA));
            for (var k = 2; ; k++)
            {
                keyB = new KeyStruct { kfield1 = k, kfield2 = 1 };
                hashB = store.storeFunctions.GetKeyHashCode64(keyB);
                tagB = HashBucketEntry.GetTag(hashB);
                if (tagB != tagA)
                    break;
            }
            Upsert(keyA, 1);
            Upsert(keyB, 2);
            ClassicAssert.AreEqual(1, SlotsHolding(tagB), "CONTROL: the second key's tag sits in one slot, after the first key's");
        }

        [TearDown]
        public void TearDown()
        {
            if (store != null)
                store.tagFoundBeforeReadForTest = null;
            session?.Dispose(); session = null;
            store?.Dispose(); store = null;
            log?.Dispose(); log = null;
            OnTearDown();
        }

        [Test]
        [Category("TsavoriteKV")]
        [Category("ReadCache")]
        public void A_read_cache_copy_onto_an_emptied_slot_is_abandoned_and_counted()
        {
            store.Log.FlushAndEvict(wait: true);
            var raced = EmptyTheSlotOnce();
            var value = new ValueStruct { vfield1 = 2, vfield2 = 0 };
            var cached = session.BasicContext.ConditionalInsertReadCache(keyB, SpanByte.FromPinnedVariable(ref value), store.Log.TailAddress);
            store.tagFoundBeforeReadForTest = null;
            ClassicAssert.AreEqual(1, raced[0], "CONTROL: the slot was emptied under the copy");

            ClassicAssert.AreEqual(1, SlotsHolding(tagB), "the key's tag sits in one slot (MUTANT: a CAS from 0 puts the copy in a second slot)");
            ClassicAssert.IsFalse(cached, "the copy was abandoned");
            ClassicAssert.AreEqual(1, store.ZeroCasRefusals.ReadCacheCopy, "the refused copy is counted");
        }

        [Test]
        [Category("TsavoriteKV")]
        public void An_upsert_onto_an_emptied_slot_retries_into_the_one_chain()
        {
            var raced = EmptyTheSlotOnce();
            Upsert(keyB, 22);
            store.tagFoundBeforeReadForTest = null;
            ClassicAssert.AreEqual(1, raced[0], "CONTROL: the slot was emptied under the upsert");

            ClassicAssert.AreEqual(1, SlotsHolding(tagB), "the key's tag sits in one slot (MUTANT: a CAS from 0 puts the record in a second slot)");
            ClassicAssert.AreEqual(1, store.ZeroCasRefusals.Upsert, "the refused upsert is counted");
            InputStruct input = default;
            OutputStruct output = default;
            var status = session.BasicContext.Read(keyB, ref input, ref output);
            ClassicAssert.IsTrue(status.Found, "a read finds the upsert");
            ClassicAssert.AreEqual(22, output.value.vfield1, "a read finds the upsert's value");
        }

        [Test]
        [Category("TsavoriteKV")]
        public void A_delete_onto_an_emptied_slot_retries_into_the_one_chain()
        {
            var raced = EmptyTheSlotOnce();
            _ = session.BasicContext.Delete(keyB, Empty.Default);
            store.tagFoundBeforeReadForTest = null;
            ClassicAssert.AreEqual(1, raced[0], "CONTROL: the slot was emptied under the delete");

            ClassicAssert.AreEqual(1, SlotsHolding(tagB), "the key's tag sits in one slot (MUTANT: a CAS from 0 puts the tombstone in a second slot)");
            ClassicAssert.AreEqual(1, store.ZeroCasRefusals.Delete, "the refused delete is counted");
            HashEntryInfo hei = new(hashB);
            ClassicAssert.IsTrue(store.FindTag(ref hei), "CONTROL: the key keeps its tag");
            ClassicAssert.Greater(hei.Address, LogAddress.kTempInvalidAddress, "the tombstone heads the chain FindTag finds");
            InputStruct input = default;
            OutputStruct output = default;
            ClassicAssert.IsFalse(session.BasicContext.Read(keyB, ref input, ref output).Found, "a read finds the key deleted");
        }

        // On the first lock helper that found the second key's tag: a scrub empties both keys' slots, then another writer's FindOrCreateTag
        // puts the second key's tag in the earlier free slot.
        private int[] EmptyTheSlotOnce()
        {
            var raced = new int[1];
            store.tagFoundBeforeReadForTest = hei =>
            {
                if (hei.tag != tagB || raced[0] != 0)
                    return;
                raced[0] = 1;
                var bucket = hei.firstBucket;
                for (var slot = 0; slot < Constants.kOverflowBucketIndex; slot++)
                {
                    var entry = new HashBucketEntry { word = bucket->bucket_entries[slot] };
                    if (entry.word != 0 && (entry.Tag == tagA || entry.Tag == tagB))
                        bucket->bucket_entries[slot] = 0;
                }
                HashEntryInfo other = new(hashB);
                store.FindOrCreateTag(ref other, store.Log.BeginAddress);
                ClassicAssert.Less(other.slot, hei.slot, "CONTROL: the other writer took the earlier free slot");
            };
            return raced;
        }

        private int SlotsHolding(ushort tag)
        {
            HashEntryInfo hei = new(hashB);
            ClassicAssert.IsTrue(store.FindTag(ref hei), "CONTROL: the key's tag is in the index");
            var count = 0;
            for (var slot = 0; slot < Constants.kOverflowBucketIndex; slot++)
            {
                var entry = new HashBucketEntry { word = hei.firstBucket->bucket_entries[slot] };
                if (entry.word != 0 && !entry.Tentative && entry.Tag == tag)
                    count++;
            }
            return count;
        }

        private void Upsert(KeyStruct key, int v)
        {
            var value = new ValueStruct { vfield1 = v, vfield2 = 0 };
            _ = session.BasicContext.Upsert(key, SpanByte.FromPinnedVariable(ref value), Empty.Default);
        }
    }
}
