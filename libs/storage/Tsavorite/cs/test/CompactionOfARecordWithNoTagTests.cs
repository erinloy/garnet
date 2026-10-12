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
    /// A valid record above BeginAddress whose key has no hash-index entry is unreachable: every read starts at the tag. A lookup compaction
    /// that meets one drops it and counts it; it used to link the copy into a bucket it never found (NullReferenceException in
    /// CASRecordIntoChain), read on a copy of the mesh broker's store on 2026-10-12, where every pass over that window threw.
    /// </summary>
    [TestFixture]
    internal class CompactionOfARecordWithNoTagTests : TestBase
    {
        private const int Keys = 200;
        private TsavoriteKV<StructStoreFunctions, StructAllocator> store;
        private ClientSession<KeyStruct, InputStruct, OutputStruct, Empty, QuietFunctions, StructStoreFunctions, StructAllocator> session;
        private BasicContext<KeyStruct, InputStruct, OutputStruct, Empty, QuietFunctions, StructStoreFunctions, StructAllocator> bContext;
        private IDevice log;

        [SetUp]
        public void Setup()
        {
            DeleteDirectory(MethodTestDir, wait: true);
            log = Devices.CreateLogDevice(Path.Join(MethodTestDir, "notag.log"), deleteOnClose: true);
            store = new(new KVSettings { IndexSize = 1L << 13, LogDevice = log, PageSize = MinKvLogPageSize, LogMemorySize = 1L << 20, SegmentSize = 1L << 22 }
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

        private static KeyStruct Key(int i) => new() { kfield1 = 1_000 + i, kfield2 = 2_000 + i };

        private Status Read(KeyStruct key)
        {
            InputStruct input = default;
            OutputStruct output = default;
            var status = bContext.Read(key, ref input, ref output, Empty.Default);
            if (status.IsPending)
            {
                _ = bContext.CompletePendingWithOutputs(out var outputs, wait: true);
                (status, _) = GetSinglePendingResult(outputs);
            }
            return status;
        }

        private void FillAndMakeReadOnly()
        {
            for (var i = 0; i < Keys; i++)
            {
                var key = Key(i);
                var value = new ValueStruct { vfield1 = i, vfield2 = -i };
                _ = bContext.Upsert(key, SpanByte.FromPinnedVariable(ref value), Empty.Default);
            }
            store.Log.ShiftReadOnlyAddress(store.Log.TailAddress, wait: true);
        }

        // The state the broker's store held: the record stays in the log, valid, and no index entry names its key.
        private void RemoveTagOf(KeyStruct key)
        {
            HashEntryInfo hei = new(store.storeFunctions.GetKeyHashCode64(key));
            ClassicAssert.IsTrue(store.FindTag(ref hei), "CONTROL: the key had a tag");
            ClassicAssert.IsTrue(hei.TryElide(), "the tag is removed");
            ClassicAssert.IsTrue(Read(key).NotFound, "CONTROL: with no tag, no read reaches the record");
        }

        [Test]
        [Category("TsavoriteKV"), Category("Compaction")]
        public void A_lookup_compaction_drops_a_record_whose_key_has_no_tag_and_counts_it([Values] bool removeATag)
        {
            FillAndMakeReadOnly();
            var orphan = Key(Keys / 2);
            if (removeATag) RemoveTagOf(orphan);
            var before = store.CompactionSourcesWithoutTag;

            var until = store.Log.SafeReadOnlyAddress;
            var reached = session.Compact(until, CompactionType.Lookup);

            ClassicAssert.AreEqual(reached, store.Log.BeginAddress, "the pass completed and moved BeginAddress");
            ClassicAssert.AreEqual(removeATag ? 1 : 0, store.CompactionSourcesWithoutTag - before, "each record with no tag is counted, and only those");
            for (var i = 0; i < Keys; i++)
            {
                var status = Read(Key(i));
                if (removeATag && i == Keys / 2) ClassicAssert.IsTrue(status.NotFound, $"the orphan stays unreachable ({status})");
                else ClassicAssert.IsTrue(status.Found, $"key {i} was carried ({status})");
            }
        }
    }
}
