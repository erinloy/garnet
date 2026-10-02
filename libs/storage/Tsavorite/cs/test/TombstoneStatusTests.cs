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
    }
}
