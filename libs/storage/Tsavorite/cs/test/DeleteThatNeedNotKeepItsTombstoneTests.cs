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
    /// F2 with only some kinds of key in the cold store: a store that keeps tombstones keeps them for every delete, and a delete of a
    /// key that has no cold copy does not need its own. It says so, and then costs what a delete costs in a store that keeps none.
    /// </summary>
    [TestFixture]
    internal class DeleteThatNeedNotKeepItsTombstoneTests : TestBase
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

        private Status Delete(KeyStruct key, bool needNotBeKept)
        {
            DeleteOptions options = new() { TombstoneNeedNotBeKept = needNotBeKept };
            return bContext.Delete(key, ref options, Empty.Default);
        }

        [Test]
        [Category("TsavoriteKV")]
        public void It_writes_nothing_for_a_key_this_log_never_held([Values] bool needNotBeKept)
        {
            Open(keepDeleteTombstones: true);
            var key = new KeyStruct { kfield1 = 211, kfield2 = 212 };
            var tail = store.Log.TailAddress;

            _ = Delete(key, needNotBeKept);

            var status = Read(key);
            ClassicAssert.IsTrue(status.NotFound, status.ToString());
            if (needNotBeKept)
            {
                ClassicAssert.AreEqual(tail, store.Log.TailAddress, "a delete that need not keep its tombstone writes nothing for a key the log never held");
                ClassicAssert.IsFalse(status.IsTombstoned, $"and the key reads absent, not deleted ({status})");
            }
            else
            {
                ClassicAssert.Greater(store.Log.TailAddress, tail, "CONTROL: a delete that says nothing is a record in a store that keeps tombstones");
                ClassicAssert.IsTrue(status.IsTombstoned, $"CONTROL: and a read says the key was deleted ({status})");
            }
        }

        [Test]
        [Category("TsavoriteKV")]
        public void It_is_elided_when_it_is_alone_on_its_chain([Values] bool needNotBeKept)
        {
            Open(keepDeleteTombstones: true);
            var key = new KeyStruct { kfield1 = 221, kfield2 = 222 };
            var value = new ValueStruct { vfield1 = 9, vfield2 = 10 };
            // A fresh key in the mutable region with no older version: the case a store that keeps no tombstones elides.
            _ = bContext.Upsert(key, SpanByte.FromPinnedVariable(ref value), Empty.Default);

            _ = Delete(key, needNotBeKept);

            var status = Read(key);
            ClassicAssert.IsTrue(status.NotFound, status.ToString());
            if (needNotBeKept)
                ClassicAssert.IsFalse(status.IsTombstoned, $"the delete said its tombstone need not be kept, so none answers ({status})");
            else
                ClassicAssert.IsTrue(status.IsTombstoned, $"CONTROL: a delete that says nothing leaves its tombstone ({status})");
        }

        [Test]
        [Category("TsavoriteKV")]
        public void It_still_deletes_a_key_whose_record_has_left_memory([Values] bool needNotBeKept)
        {
            Open(keepDeleteTombstones: true);
            var key = new KeyStruct { kfield1 = 231, kfield2 = 232 };
            var value = new ValueStruct { vfield1 = 11, vfield2 = 12 };
            _ = bContext.Upsert(key, SpanByte.FromPinnedVariable(ref value), Empty.Default);
            store.Log.FlushAndEvict(wait: true);
            var tail = store.Log.TailAddress;

            _ = Delete(key, needNotBeKept);

            ClassicAssert.Greater(store.Log.TailAddress, tail, "the record is on the device, so the delete is a tombstone in the log whatever it said");
            var status = Read(key);
            ClassicAssert.IsTrue(status.NotFound, status.ToString());
            ClassicAssert.IsTrue(status.IsTombstoned, $"and the key reads deleted ({status})");
        }

        [Test]
        [Category("TsavoriteKV")]
        public void In_a_store_that_keeps_no_tombstones_it_changes_nothing([Values] bool needNotBeKept)
        {
            Open(keepDeleteTombstones: false);
            var (never, fresh) = (new KeyStruct { kfield1 = 241, kfield2 = 242 }, new KeyStruct { kfield1 = 251, kfield2 = 252 });
            var value = new ValueStruct { vfield1 = 13, vfield2 = 14 };
            _ = bContext.Upsert(fresh, SpanByte.FromPinnedVariable(ref value), Empty.Default);
            var tail = store.Log.TailAddress;

            _ = Delete(never, needNotBeKept);
            ClassicAssert.AreEqual(tail, store.Log.TailAddress, "a key the log never held: nothing is written");
            _ = Delete(fresh, needNotBeKept);

            ClassicAssert.IsFalse(Read(never).IsTombstoned);
            var status = Read(fresh);
            ClassicAssert.IsTrue(status.NotFound, status.ToString());
            ClassicAssert.IsFalse(status.IsTombstoned, $"and a fresh key's delete is elided ({status})");
        }
    }
}
