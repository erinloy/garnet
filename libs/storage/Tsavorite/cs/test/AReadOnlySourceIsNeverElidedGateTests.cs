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
    /// oracle 11-27 (kill sweep, chunk CD): a copy of a key's first version elided its read-only source, so SealAndInvalidate reached bytes a
    /// checkpoint had flushed; the next flush re-wrote that partial page and a recovery to that checkpoint served the older version.
    /// </summary>
    [TestFixture]
    internal class AReadOnlySourceIsNeverElidedGateTests : TestBase
    {
        private TsavoriteKV<StructStoreFunctions, StructAllocator> store;
        private ClientSession<KeyStruct, InputStruct, OutputStruct, Empty, Functions, StructStoreFunctions, StructAllocator> session;
        private IDevice log;

        [SetUp]
        public void Setup()
        {
            DeleteDirectory(MethodTestDir, wait: true);
            log = Devices.CreateLogDevice(Path.Join(MethodTestDir, "elide.log"), deleteOnClose: true);
            store = new(new KVSettings { IndexSize = 1L << 13, LogDevice = log, PageSize = MinKvLogPageSize, LogMemorySize = 1L << 15, SegmentSize = 1L << 22 }
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
        public void A_copy_of_a_flushed_first_version_leaves_its_source_valid()
        {
            var key = new KeyStruct { kfield1 = 311, kfield2 = 312 };
            var (first, second) = (new InputStruct { ifield1 = 1, ifield2 = 2 }, new InputStruct { ifield1 = 3, ifield2 = 4 });
            var at = store.Log.TailAddress;
            OutputStruct output = default;
            _ = session.BasicContext.RMW(key, ref first, ref output, Empty.Default);
            ClassicAssert.IsTrue(LogRecord.GetInfo(store.hlogBase.GetPhysicalAddress(at)).PreviousAddress < store.Log.BeginAddress, "CONTROL: a first version, the elidable shape");
            store.Log.ShiftReadOnlyAddress(store.Log.TailAddress, wait: true);   // flushed, as a FoldOver checkpoint leaves it, still in memory

            _ = session.BasicContext.RMW(key, ref second, ref output, Empty.Default);   // a copy-update: the RMW path is the one that takes a read-only record as its source

            ClassicAssert.Greater(store.Log.TailAddress, at + 1, "CONTROL: the second version was copied to the tail, not updated in place");
            var source = LogRecord.GetInfo(store.hlogBase.GetPhysicalAddress(at));
            ClassicAssert.IsTrue(source.Valid && !source.IsSealed, $"MUTANT (read-only source elided): the flushed version reads {source}, and a re-flush of its page writes that");
        }
    }
}
