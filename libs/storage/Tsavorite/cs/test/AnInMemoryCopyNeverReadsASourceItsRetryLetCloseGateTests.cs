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
    /// A read of a record in the immutable in-memory region with CopyFrom AllImmutable, CopyTo MainLog copies its in-memory source to the tail
    /// (ReadFromImmutable, wantIO false). When the insert loses its CAS, the retry may refresh the epoch and HeadAddress may pass the source;
    /// the re-search then read the source's key from a page that could close (keystone 11-24: NullReferenceException at LogRecord.KeyBytes
    /// under ConditionalCopyToTail, a chunk store under chunk swaps). The copy is best effort, so a source that left memory is dropped first.
    /// </summary>
    [TestFixture]
    internal class AnInMemoryCopyNeverReadsASourceItsRetryLetCloseGateTests : TestBase
    {
        private TsavoriteKV<StructStoreFunctions, StructAllocator> store;
        private ClientSession<KeyStruct, InputStruct, OutputStruct, Empty, Functions, StructStoreFunctions, StructAllocator> session;
        private IDevice log;

        [SetUp]
        public void Setup()
        {
            DeleteDirectory(MethodTestDir, wait: true);
            log = Devices.CreateLogDevice(Path.Join(MethodTestDir, "evictedsource.log"), deleteOnClose: true);
            store = new(new KVSettings { IndexSize = 1L << 13, LogDevice = log, PageSize = 1L << 12, LogMemorySize = 1L << 16, SegmentSize = 1L << 22 }
                , StoreFunctions.Create(KeyStruct.Comparer.Instance, SpanByteRecordTriggers.Instance)
                , (allocatorSettings, storeFunctions) => new(allocatorSettings, storeFunctions));
            session = store.NewSession<KeyStruct, InputStruct, OutputStruct, Empty, Functions>(new Functions());
        }

        [TearDown]
        public void TearDown()
        {
            if (store != null)
                (store.conditionalCopyLosesOnceForTest, store.conditionalCopyEvictedCheckOffForTest, store.conditionalCopyRetryForTest) = (false, false, null);
            session?.Dispose(); session = null;
            store?.Dispose(); store = null;
            log?.Dispose(); log = null;
            OnTearDown();
        }

        [Test]
        [Category("TsavoriteKV")]
        public void A_copy_whose_retry_let_its_in_memory_source_close_is_dropped_before_its_key_is_read()
        {
            var (found, searched, dropped) = ReadWhileTheRetryClosesTheSource(checkOff: false);
            ClassicAssert.IsTrue(found, "the read answers the record it found in memory");
            ClassicAssert.AreEqual(1L, dropped, "the copy whose source left memory is dropped, and counted");
            ClassicAssert.AreEqual(0, searched, "no search read the key of a source below HeadAddress");
        }

        [Test]
        [Category("TsavoriteKV")]
        public void CONTROL_without_the_check_the_retry_searches_with_the_closed_source()
        {
            // The defect kept visible: the session holds its epoch here, so the closed page's bytes are still intact and the read survives;
            // in production another thread's refresh frees it, and the same read of the key throws.
            var (found, searched, dropped) = ReadWhileTheRetryClosesTheSource(checkOff: true);
            ClassicAssert.IsTrue(found, "CONTROL: the read answers");
            ClassicAssert.AreEqual(0L, dropped, "CONTROL: nothing is dropped without the check");
            ClassicAssert.AreEqual(1, searched, "CONTROL: without the check the retry searches with a source already below HeadAddress");
        }

        private (bool Found, int Searched, long Dropped) ReadWhileTheRetryClosesTheSource(bool checkOff)
        {
            var bContext = session.BasicContext;
            var key = new KeyStruct { kfield1 = 13, kfield2 = 14 };
            var value = new ValueStruct { vfield1 = 23, vfield2 = 24 };
            ClassicAssert.IsTrue(bContext.Upsert(key, SpanByte.FromPinnedVariable(ref value), Empty.Default).IsCompletedSuccessfully);
            var source = store.Log.TailAddress;   // the next filler starts here; the key's record is just below it
            for (var i = 0; i < 200; i++)
            {
                var k = new KeyStruct { kfield1 = 1000 + i, kfield2 = 1 };
                var v = new ValueStruct { vfield1 = i, vfield2 = i };
                ClassicAssert.IsTrue(bContext.Upsert(k, SpanByte.FromPinnedVariable(ref v), Empty.Default).IsCompletedSuccessfully);
            }
            store.Log.ShiftReadOnlyAddress(store.Log.TailAddress, wait: true);   // everything immutable and flushed, still in memory
            ClassicAssert.Less(store.Log.HeadAddress, source, "CONTROL: the key's record is in memory");
            ClassicAssert.GreaterOrEqual(store.Log.ReadOnlyAddress, source, "CONTROL: and in the immutable region");

            var closeTo = store.hlogBase.GetFirstValidLogicalAddressOnPage(store.hlogBase.GetPage(store.Log.ReadOnlyAddress));
            ClassicAssert.Greater(closeTo, source, "CONTROL: the head can be moved past the key's page");
            var droppedBefore = store.CopiesFromEvictedSourcesDropped;
            store.conditionalCopySearchedEvictedForTest = 0;
            store.conditionalCopyEvictedCheckOffForTest = checkOff;
            store.conditionalCopyLosesOnceForTest = true;
            store.conditionalCopyRetryForTest = () => { store.conditionalCopyRetryForTest = null; _ = store.hlogBase.ShiftHeadAddress(closeTo); };

            InputStruct input = default;
            OutputStruct output = default;
            ReadOptions readOptions = new() { CopyOptions = new(ReadCopyFrom.AllImmutable, ReadCopyTo.MainLog) };
            var status = bContext.Read(key, ref input, ref output, ref readOptions, out _);
            ClassicAssert.IsFalse(status.IsPending, "CONTROL: the record was read from memory");
            ClassicAssert.IsNull(store.conditionalCopyRetryForTest, "CONTROL: the insert lost once and the retry ran");
            ClassicAssert.GreaterOrEqual(store.Log.HeadAddress, closeTo, "CONTROL: the retry moved the head past the source");
            return (status.Found && output.value.vfield1 == value.vfield1, store.conditionalCopySearchedEvictedForTest, store.CopiesFromEvictedSourcesDropped - droppedBefore);
        }
    }
}
