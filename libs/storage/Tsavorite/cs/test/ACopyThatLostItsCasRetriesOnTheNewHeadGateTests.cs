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
    /// A copy to the tail (ConditionalCopyToTail: hot reads, F2's cold copies, compaction) loses its head CAS because another writer put a
    /// record of another key in the same tag chain at the head. Its search then finds nothing newer for its own key, so it retries the insert;
    /// the retry kept the hash entry it read first, so every CAS compared against the old head and lost, forever, under one epoch. No fold-over
    /// could flush, and every writer waited in WaitToAllocate (dev mesh 11-13: three wedges within minutes of a FULL checkpoint).
    /// The retry now takes the entry its search just read, so the second CAS lands on the new head.
    /// </summary>
    [TestFixture]
    internal unsafe class ACopyThatLostItsCasRetriesOnTheNewHeadGateTests : TestBase
    {
        private TsavoriteKV<StructStoreFunctions, StructAllocator> store;
        private ClientSession<KeyStruct, InputStruct, OutputStruct, Empty, Functions, StructStoreFunctions, StructAllocator> session;
        private IDevice log;

        [SetUp]
        public void Setup()
        {
            DeleteDirectory(MethodTestDir, wait: true);
            log = Devices.CreateLogDevice(Path.Join(MethodTestDir, "lostcopycas.log"), deleteOnClose: true);
            // one bucket, so two keys sharing a tag share a chain
            store = new(new KVSettings { IndexSize = 64, LogDevice = log, PageSize = 1L << 16, LogMemorySize = 1L << 20, SegmentSize = 1L << 22 }
                , StoreFunctions.Create(KeyStruct.Comparer.Instance, SpanByteRecordTriggers.Instance)
                , (allocatorSettings, storeFunctions) => new(allocatorSettings, storeFunctions));
            session = store.NewSession<KeyStruct, InputStruct, OutputStruct, Empty, Functions>(new Functions());
        }

        [TearDown]
        public void TearDown()
        {
            if (store != null)
                store.copyBeforeCasForTest = null;
            session?.Dispose(); session = null;
            store?.Dispose(); store = null;
            log?.Dispose(); log = null;
            OnTearDown();
        }

        [Test]
        [Category("TsavoriteKV")]
        public void A_copy_whose_head_moved_to_another_key_lands_on_the_new_head()
        {
            var keyA = new KeyStruct { kfield1 = 1, kfield2 = 1 };
            var hashA = store.storeFunctions.GetKeyHashCode64(keyA);
            var tag = HashBucketEntry.GetTag(hashA);
            KeyStruct keyC = default;
            for (var k = 2; ; k++)
            {
                keyC = new KeyStruct { kfield1 = k, kfield2 = 1 };
                if (HashBucketEntry.GetTag(store.storeFunctions.GetKeyHashCode64(keyC)) == tag)
                    break;
            }

            Upsert(keyA, 1);
            Upsert(keyC, 30);
            var c = Head(hashA);
            store.Log.ShiftReadOnlyAddress(store.Log.TailAddress, wait: true);   // so the next write appends instead of updating in place
            Upsert(keyC, 31);
            var d = Head(hashA);
            ClassicAssert.Greater(d, c, "CONTROL: the second write of the other key heads the shared chain");

            // The copy reads c as the head; another writer's d takes the head before the copy's CAS.
            SetHead(hashA, tag, c);
            var moved = 0;
            store.copyBeforeCasForTest = hei =>
            {
                if (moved++ == 0)
                    SetHead(hashA, tag, d);
            };

            var value = new ValueStruct { vfield1 = 2, vfield2 = 0 };
            var since = store.Log.TailAddress;
            var copy = System.Threading.Tasks.Task.Run(() =>
            {
                using var s = store.NewSession<KeyStruct, InputStruct, OutputStruct, Empty, Functions>(new Functions());
                return s.BasicContext.ConditionalInsert(keyA, SpanByte.FromPinnedVariable(ref value), since);
            });
            var finished = copy.Wait(10_000);
            store.copyBeforeCasForTest = null;

            ClassicAssert.IsTrue(finished, "the copy finished (MUTANT: the retry CASes against the old head, losing forever under one epoch)");
            ClassicAssert.GreaterOrEqual(moved, 2, "CONTROL: the first CAS lost and the copy tried again");
            ClassicAssert.Greater(Head(hashA), d, "the copy heads the chain, above the other writer's record");
            ClassicAssert.AreEqual(2L, ReadV(keyA), "a read finds the copy");
            ClassicAssert.AreEqual(31L, ReadV(keyC), "the other key's newest record stays in the chain");
        }

        private long Head(long hash)
        {
            HashEntryInfo hei = new(hash);
            ClassicAssert.IsTrue(store.FindTag(ref hei), "CONTROL: the key's tag is in the index");
            return hei.Address;
        }

        private void SetHead(long hash, ushort tag, long address)
        {
            HashEntryInfo hei = new(hash);
            ClassicAssert.IsTrue(store.FindTag(ref hei), "CONTROL: the key's tag is in the index");
            hei.firstBucket->bucket_entries[hei.slot] = new HashBucketEntry { Tag = tag, Address = address, Tentative = false }.word;
        }

        private long ReadV(KeyStruct key)
        {
            InputStruct input = default;
            OutputStruct output = default;
            var status = session.BasicContext.Read(key, ref input, ref output);
            ClassicAssert.IsTrue(status.Found, "CONTROL: the key is found");
            return output.value.vfield1;
        }

        private void Upsert(KeyStruct key, int v)
        {
            var value = new ValueStruct { vfield1 = v, vfield2 = 0 };
            _ = session.BasicContext.Upsert(key, SpanByte.FromPinnedVariable(ref value), Empty.Default);
        }
    }
}
