// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.IO;
using System.Runtime.InteropServices;
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
    /// F2's copy from a cold store whose records are bytes, not log records: the hot log takes (key, value) or a tombstone only when it
    /// holds nothing for the key at or above the tail its caller read before looking in the cold store.
    /// </summary>
    [TestFixture]
    internal class ConditionalInsertFromBytesTests : TestBase
    {
        private TsavoriteKV<StructStoreFunctions, StructAllocator> store;
        private ClientSession<KeyStruct, InputStruct, OutputStruct, Empty, QuietFunctions, StructStoreFunctions, StructAllocator> session;
        private BasicContext<KeyStruct, InputStruct, OutputStruct, Empty, QuietFunctions, StructStoreFunctions, StructAllocator> bContext;
        private IDevice log;

        [SetUp]
        public void Setup()
        {
            DeleteDirectory(MethodTestDir, wait: true);
            log = Devices.CreateLogDevice(Path.Join(MethodTestDir, "hot.log"), deleteOnClose: true);
            store = new(new KVSettings { IndexSize = 1L << 13, LogDevice = log, PageSize = MinKvLogPageSize, LogMemorySize = 1L << 15, SegmentSize = 1L << 22,
                                         KeepDeleteTombstones = true }
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

        private static ReadOnlySpan<byte> Bytes(ref ValueStruct value) => MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref value, 1));

        private (Status Status, long Field) Read(KeyStruct key)
        {
            InputStruct input = default;
            OutputStruct output = default;
            ReadOptions options = new() { ReportTombstone = true };
            var status = bContext.Read(key, ref input, ref output, ref options, Empty.Default);
            if (status.IsPending)
            {
                _ = bContext.CompletePendingWithOutputs(out var outputs, wait: true);
                (status, output) = GetSinglePendingResult(outputs);
            }
            return (status, status.Found ? output.value.vfield1 : -1);
        }

        private Status Insert(KeyStruct key, ref ValueStruct value, long since)
        {
            var status = bContext.ConditionalInsert(key, Bytes(ref value), since);
            if (status.IsPending) _ = bContext.CompletePending(wait: true);
            return status;
        }

        [Test]
        [Category("TsavoriteKV")]
        public void A_key_this_log_never_held_is_appended_from_bytes([Values] bool anotherKeyOfItsChainIsHere)
        {
            var key = new KeyStruct { kfield1 = 61, kfield2 = 62 };
            var value = new ValueStruct { vfield1 = 7, vfield2 = 8 };
            if (anotherKeyOfItsChainIsHere)
            {
                var other = new ValueStruct { vfield1 = 1, vfield2 = 1 };
                _ = bContext.Upsert(new KeyStruct { kfield1 = 61, kfield2 = 99 }, SpanByte.FromPinnedVariable(ref other), Empty.Default);
            }

            var tail = store.Log.TailAddress;
            var status = Insert(key, ref value, tail);

            ClassicAssert.IsTrue(status.Record.Copied, $"nothing for the key is in this log, so it is appended: {status}");
            ClassicAssert.Greater(store.Log.TailAddress, tail, "the log grew by the record");
            ClassicAssert.AreEqual(7, Read(key).Field, "and the key reads its value from this log");
            if (anotherKeyOfItsChainIsHere)
                ClassicAssert.AreEqual(1, Read(new KeyStruct { kfield1 = 61, kfield2 = 99 }).Field, "CONTROL: the other key of the chain still reads");
        }

        [Test]
        [Category("TsavoriteKV")]
        public void A_record_written_exactly_at_the_tail_the_caller_read_stops_the_insert()
        {
            var key = new KeyStruct { kfield1 = 71, kfield2 = 72 };
            var stale = new ValueStruct { vfield1 = 3, vfield2 = 3 };
            var newer = new ValueStruct { vfield1 = 9, vfield2 = 9 };

            var tail = store.Log.TailAddress;   // the caller reads the tail, then looks in the cold store
            _ = bContext.Upsert(key, SpanByte.FromPinnedVariable(ref newer), Empty.Default);   // meanwhile a writer's record lands AT that address
            var after = store.Log.TailAddress;
            var status = Insert(key, ref stale, tail);

            ClassicAssert.IsTrue(status.Found && !status.Record.Copied, $"a record for the key is at the bound itself, so nothing is copied: {status}");
            ClassicAssert.AreEqual(after, store.Log.TailAddress, "nothing appended");
            ClassicAssert.AreEqual(9, Read(key).Field, "the writer's value stands; MUTANT (the bound excludes its own address) reads the stale 3");
        }

        [Test]
        [Category("TsavoriteKV")]
        public void An_older_record_below_the_bound_does_not_stop_it_and_a_search_of_the_whole_log_finds_it()
        {
            var key = new KeyStruct { kfield1 = 81, kfield2 = 82 };
            var old = new ValueStruct { vfield1 = 1, vfield2 = 1 };
            var copy = new ValueStruct { vfield1 = 5, vfield2 = 5 };
            _ = bContext.Upsert(key, SpanByte.FromPinnedVariable(ref old), Empty.Default);
            store.Log.ShiftReadOnlyAddress(store.Log.TailAddress, wait: true);

            var whole = Insert(key, ref copy, LogAddress.kInvalidAddress);
            ClassicAssert.IsTrue(whole.Found && !whole.Record.Copied, $"asked of the whole log, the old record is found: {whole}");
            ClassicAssert.AreEqual(1, Read(key).Field);

            var since = Insert(key, ref copy, store.Log.TailAddress);
            ClassicAssert.IsTrue(since.Record.Copied, $"nothing for the key is at or above the bound: {since}");
            ClassicAssert.AreEqual(5, Read(key).Field, "the appended record is the key's newest");
        }

        [Test]
        [Category("TsavoriteKV")]
        public void A_delete_since_the_bound_stops_the_insert_and_a_tombstone_is_appended_from_bytes()
        {
            var deleted = new KeyStruct { kfield1 = 91, kfield2 = 92 };
            var value = new ValueStruct { vfield1 = 4, vfield2 = 4 };
            var tail = store.Log.TailAddress;
            _ = bContext.Delete(deleted, Empty.Default);   // this store keeps every delete's tombstone, so a delete of a key it never held is a record
            var status = Insert(deleted, ref value, tail);
            ClassicAssert.IsTrue(status.Found && !status.Record.Copied, $"the delete is newer than what the caller holds: {status}");
            var read = Read(deleted).Status;
            ClassicAssert.IsTrue(read.NotFound && read.IsTombstoned, $"and the key still reads deleted: {read}");

            var gone = new KeyStruct { kfield1 = 93, kfield2 = 94 };
            ClassicAssert.IsFalse(Read(gone).Status.IsTombstoned, "CONTROL: a key never written is absent, not deleted");
            var tombstone = bContext.ConditionalInsertTombstone(gone, store.Log.TailAddress);
            if (tombstone.IsPending) _ = bContext.CompletePending(wait: true);
            ClassicAssert.IsTrue(tombstone.Record.Copied, $"a tombstone is appended for a key this log never held: {tombstone}");
            read = Read(gone).Status;
            ClassicAssert.IsTrue(read.NotFound && read.IsTombstoned, $"and it reads as a delete: {read}");
        }
    }
}
