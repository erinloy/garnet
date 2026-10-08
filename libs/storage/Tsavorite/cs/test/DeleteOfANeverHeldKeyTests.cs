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
    /// F2's hot delete: the key's only copy may be in a cold store, so a store that keeps tombstones writes one for a key it never
    /// held, even when the key's tag has no entry in its index. Without it the cold copy answers again.
    /// </summary>
    [TestFixture]
    internal class DeleteOfANeverHeldKeyTests : TestBase
    {
        private TsavoriteKV<StructStoreFunctions, StructAllocator> store;
        private ClientSession<KeyStruct, InputStruct, OutputStruct, Empty, QuietFunctions, StructStoreFunctions, StructAllocator> session;
        private BasicContext<KeyStruct, InputStruct, OutputStruct, Empty, QuietFunctions, StructStoreFunctions, StructAllocator> bContext;
        private IDevice log;

        private void Open(bool keepDeleteTombstones)
        {
            DeleteDirectory(MethodTestDir, wait: true);
            log = Devices.CreateLogDevice(Path.Join(MethodTestDir, "delete.log"), deleteOnClose: true);
            store = new(new KVSettings { IndexSize = 1L << 13, LogDevice = log, PageSize = MinKvLogPageSize, LogMemorySize = 1L << 15, SegmentSize = 1L << 22,
                                         KeepDeleteTombstones = keepDeleteTombstones }
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

        private Status Read(KeyStruct key)
        {
            InputStruct input = default;
            OutputStruct output = default;
            ReadOptions options = new() { ReportTombstone = true };
            var status = bContext.Read(key, ref input, ref output, ref options, Empty.Default);
            if (status.IsPending)
            {
                _ = bContext.CompletePendingWithOutputs(out var outputs, wait: true);
                (status, _) = GetSinglePendingResult(outputs);
            }
            return status;
        }

        [Test]
        [Category("TsavoriteKV")]
        public void A_store_that_keeps_tombstones_writes_one_for_a_key_whose_tag_it_never_held([Values] bool keep)
        {
            Open(keepDeleteTombstones: keep);
            var key = new KeyStruct { kfield1 = 111, kfield2 = 112 };
            var tail = store.Log.TailAddress;

            _ = bContext.Delete(key, Empty.Default);

            var status = Read(key);
            ClassicAssert.IsTrue(status.NotFound, status.ToString());
            if (keep)
            {
                ClassicAssert.Greater(store.Log.TailAddress, tail, "the delete is a record in this log");
                ClassicAssert.IsTrue(status.IsTombstoned, $"and a read says the key was deleted, so a lookup ends here and does not go on to the cold store ({status})");
            }
            else
            {
                ClassicAssert.AreEqual(tail, store.Log.TailAddress, "CONTROL: the default store writes nothing for a key it never held");
                ClassicAssert.IsFalse(status.IsTombstoned, $"CONTROL: and the key reads absent, not deleted ({status})");
            }
        }
    }
}
