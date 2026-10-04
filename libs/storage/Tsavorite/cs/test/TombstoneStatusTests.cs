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
    /// A read that lands on a delete's tombstone says so: it is NotFound AND IsTombstoned, while a key never written is NotFound alone.
    /// A two-tier store (F2: hot log over cold log) needs the difference - a hot tombstone ends the lookup, an absent key goes on to cold.
    /// </summary>
    /// <summary>The test Functions, minus the completion callback's assert that every pending read was Found.</summary>
    internal sealed class QuietFunctions : Functions
    {
        public override void ReadCompletionCallback(ref DiskLogRecord diskLogRecord, ref InputStruct input, ref OutputStruct output, Empty ctx, Status status, RecordMetadata recordMetadata) { }
    }

    [TestFixture]
    internal class TombstoneStatusTests : TestBase
    {
        private TsavoriteKV<StructStoreFunctions, StructAllocator> store;
        private ClientSession<KeyStruct, InputStruct, OutputStruct, Empty, QuietFunctions, StructStoreFunctions, StructAllocator> session;
        private BasicContext<KeyStruct, InputStruct, OutputStruct, Empty, QuietFunctions, StructStoreFunctions, StructAllocator> bContext;
        private IDevice log;

        [SetUp]
        public void Setup() => Open(keepDeleteTombstones: false);

        private void Open(bool keepDeleteTombstones)
        {
            TearDown();
            DeleteDirectory(MethodTestDir, wait: true);
            log = Devices.CreateLogDevice(Path.Join(MethodTestDir, "tombstone.log"), deleteOnClose: true);
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

        private Status Read(KeyStruct key, bool reportTombstone = true)
        {
            InputStruct input = default;
            OutputStruct output = default;
            ReadOptions options = new() { ReportTombstone = reportTombstone };
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
        public void A_read_of_a_deleted_key_is_tombstoned_and_a_never_written_key_is_not([Values] bool onDisk)
        {
            var deleted = new KeyStruct { kfield1 = 1, kfield2 = 2 };
            var never = new KeyStruct { kfield1 = 3, kfield2 = 4 };
            var value = new ValueStruct { vfield1 = 5, vfield2 = 6 };
            _ = bContext.Upsert(deleted, SpanByte.FromPinnedVariable(ref value), Empty.Default);
            // The written record leaves the mutable region first, so the delete APPENDS a tombstone; a delete of a mutable
            // record with no older version is elided (no tombstone at all), which is the forced-tombstone fix's case, not this one.
            store.Log.FlushAndEvict(wait: true);
            _ = bContext.Delete(deleted, Empty.Default);
            if (onDisk)
                store.Log.FlushAndEvict(wait: true);

            var gone = Read(deleted);
            ClassicAssert.IsTrue(gone.NotFound, gone.ToString());
            ClassicAssert.IsTrue(gone.IsTombstoned, $"a delete's tombstone is reported as one ({gone})");

            var absent = Read(never);
            ClassicAssert.IsTrue(absent.NotFound, absent.ToString());
            ClassicAssert.IsFalse(absent.IsTombstoned, $"CONTROL: a key never written has no tombstone ({absent})");

            var plain = Read(deleted, reportTombstone: false);
            ClassicAssert.AreEqual(new Status(StatusCode.NotFound), plain, "a read that did not ask is the plain NotFound it always was");
        }

        [Test]
        [Category("TsavoriteKV")]
        public void A_store_that_keeps_tombstones_never_elides_a_delete([Values] bool keep)
        {
            Open(keepDeleteTombstones: keep);
            var key = new KeyStruct { kfield1 = 7, kfield2 = 8 };
            var value = new ValueStruct { vfield1 = 9, vfield2 = 10 };
            // A fresh key in the MUTABLE region with no older version: the case a default store elides (record freed, no tombstone).
            _ = bContext.Upsert(key, SpanByte.FromPinnedVariable(ref value), Empty.Default);
            _ = bContext.Delete(key, Empty.Default);

            var status = Read(key);
            ClassicAssert.IsTrue(status.NotFound, status.ToString());
            if (keep)
                ClassicAssert.IsTrue(status.IsTombstoned, $"a store that keeps tombstones leaves one for a mutable delete ({status})");
            else
                ClassicAssert.IsFalse(status.IsTombstoned, $"CONTROL: the default store elides it, so no tombstone answers ({status})");
        }

        [Test]
        [Category("TsavoriteKV")]
        public void A_store_that_keeps_tombstones_carries_them_through_compaction([Values] bool keep)
        {
            Open(keepDeleteTombstones: keep);
            var key = new KeyStruct { kfield1 = 11, kfield2 = 12 };
            var value = new ValueStruct { vfield1 = 13, vfield2 = 14 };
            _ = bContext.Upsert(key, SpanByte.FromPinnedVariable(ref value), Empty.Default);
            store.Log.FlushAndEvict(wait: true);
            _ = bContext.Delete(key, Empty.Default);
            store.Log.FlushAndEvict(wait: true);
            var compacted = session.Compact(store.Log.SafeReadOnlyAddress, CompactionType.Lookup);
            ClassicAssert.Greater(compacted, 0L);

            var status = Read(key);
            ClassicAssert.IsTrue(status.NotFound, status.ToString());
            if (keep)
                ClassicAssert.IsTrue(status.IsTombstoned, $"the tombstone survives compaction in a store that keeps them ({status})");
            else
                ClassicAssert.IsFalse(status.IsTombstoned, $"CONTROL: a default store drops it at compaction ({status})");
        }

        [Test]
        [Category("TsavoriteKV")]
        public void A_read_whose_record_went_below_begin_is_counted_and_a_never_written_key_is_not()
        {
            var key = new KeyStruct { kfield1 = 21, kfield2 = 22 };
            var value = new ValueStruct { vfield1 = 23, vfield2 = 24 };
            _ = bContext.Upsert(key, SpanByte.FromPinnedVariable(ref value), Empty.Default);
            store.Log.FlushAndEvict(wait: true);
            store.Log.ShiftBeginAddress(store.Log.TailAddress, truncateLog: false);

            var before = ReadsBelowBegin.Count;
            var status = Read(key, reportTombstone: false);
            ClassicAssert.IsTrue(status.NotFound, status.ToString());
            ClassicAssert.AreEqual(before + 1, ReadsBelowBegin.Count, "the key had a record; its chain ends below BeginAddress");

            before = ReadsBelowBegin.Count;
            status = Read(new KeyStruct { kfield1 = 9001, kfield2 = 9002 }, reportTombstone: false);
            ClassicAssert.IsTrue(status.NotFound, status.ToString());
            ClassicAssert.AreEqual(before, ReadsBelowBegin.Count, "CONTROL: a never-written key is not a false absence");
        }

        [Test]
        [Category("TsavoriteKV")]
        public void A_conditional_insert_copies_only_when_nothing_newer_is_here()
        {
            var stale = new KeyStruct { kfield1 = 31, kfield2 = 32 };
            var kept = new KeyStruct { kfield1 = 41, kfield2 = 42 };
            var v1 = new ValueStruct { vfield1 = 1, vfield2 = 1 };
            var v2 = new ValueStruct { vfield1 = 2, vfield2 = 2 };
            _ = bContext.Upsert(stale, SpanByte.FromPinnedVariable(ref v1), Empty.Default);
            _ = bContext.Upsert(kept, SpanByte.FromPinnedVariable(ref v1), Empty.Default);
            var end = store.Log.TailAddress;
            store.Log.ShiftReadOnlyAddress(end, wait: true);   // v1 is immutable, so the newer write is a new record above it
            _ = bContext.Upsert(stale, SpanByte.FromPinnedVariable(ref v2), Empty.Default);   // newer than the copy the caller holds

            using var iter = store.Log.Scan(store.Log.BeginAddress, end);
            while (iter.GetNext())
            {
                var tail = store.Log.TailAddress;
                var source = iter as ISourceLogRecord;
                var status = bContext.ConditionalInsert(in source, iter.CurrentAddress);
                if (status.IsPending) _ = bContext.CompletePending(wait: true);
                var isStale = System.Runtime.InteropServices.MemoryMarshal.Read<KeyStruct>(iter.Key).kfield1 == 31;
                if (isStale)
                {
                    ClassicAssert.IsTrue(status.Found && !status.Record.Copied, $"a newer record is here, so nothing is copied: {status}");
                    ClassicAssert.AreEqual(tail, store.Log.TailAddress, $"nothing appended (record at {iter.CurrentAddress}, next {iter.NextAddress}, end {end})");
                }
                else
                {
                    ClassicAssert.Greater(store.Log.TailAddress, tail, $"CONTROL: nothing newer, so the record is copied to the tail: {status}");
                    ClassicAssert.IsTrue(status.Record.Copied, $"the copy says so: {status}");
                }
            }

            InputStruct input = default;
            OutputStruct output = default;
            ClassicAssert.IsTrue(bContext.Read(stale, ref input, ref output).Found);
            ClassicAssert.AreEqual(2, output.value.vfield1, "the newer value still answers");
        }
    }
}
