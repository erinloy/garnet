// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Garnet.test;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using Tsavorite.core;
using static Tsavorite.test.TestUtils;

namespace Tsavorite.test
{
    using StructAllocator = SpanByteAllocator<StoreFunctions<AScansLivenessSaysWhatEachRecordCostTests.Chained, SpanByteRecordTriggers>>;
    using StructStoreFunctions = StoreFunctions<AScansLivenessSaysWhatEachRecordCostTests.Chained, SpanByteRecordTriggers>;

    /// <summary>
    /// mesh 10-30 18:03Z life: the hot pass examined 541k records; 218,727 liveness proofs began with a read and 220,218 ended superseded,
    /// only 283 pushed after a read. The reads went to deciding NOT to copy a record, at ~73 ms each on C:. A scan asked to push unproven
    /// hands out a record whose proof would need a read instead of reading: the caller takes records in address order, so where a newer
    /// record of the key exists it is handed that one too, later, and the last one handed out is the key's newest.
    /// </summary>
    [TestFixture]
    internal class AScanMayPushWhatItCannotProveWithoutAReadTests : TestBase
    {
        private const int Keys = 2_000;

        private TsavoriteKV<StructStoreFunctions, StructAllocator> store;
        private ClientSession<KeyStruct, InputStruct, OutputStruct, Empty, Functions, StructStoreFunctions, StructAllocator> session;
        private IDevice log;

        [SetUp]
        public void Setup()
        {
            DeleteDirectory(MethodTestDir, wait: true);
            log = Devices.CreateLogDevice(Path.Join(MethodTestDir, "unproven.log"), deleteOnClose: true);
            store = new(new KVSettings { IndexSize = 1L << 20, LogDevice = log, PageSize = 1L << 12, LogMemorySize = 1L << 16, SegmentSize = 1L << 22 }
                , StoreFunctions.Create(new AScansLivenessSaysWhatEachRecordCostTests.Chained(), SpanByteRecordTriggers.Instance)
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

        private sealed class LastWins : IScanIteratorFunctions
        {
            public readonly Dictionary<long, long> Last = [];
            public long Seen;
            public bool OnStart(long beginAddress, long endAddress) => true;
            public bool Reader<TSourceLogRecord>(in TSourceLogRecord logRecord, RecordMetadata recordMetadata, long numberOfRecords, out CursorRecordResult cursorRecordResult)
                where TSourceLogRecord : ISourceLogRecord
            {
                cursorRecordResult = CursorRecordResult.Accept;
                Seen++;
                Last[MemoryMarshal.Read<KeyStruct>(logRecord.Key).kfield1] = MemoryMarshal.Read<ValueStruct>(logRecord.ValueSpan).vfield1;
                return true;
            }
            public void OnException(Exception exception, long numberOfRecords) => throw exception;
            public void OnStop(bool completed, long numberOfRecords) { }
        }

        /// <summary>Every key once, every even key again, all below memory; then one scan of the whole log.</summary>
        private (LastWins Scan, long Records, ScanLivenessCounts Counts) WriteTwiceEvictAndScan(bool pushUnproven)
        {
            var bContext = session.BasicContext;
            for (var round = 1; round <= 2; round++)
                for (var i = 0; i < Keys; i++)
                {
                    if (round == 2 && i % 2 != 0) continue;
                    var key = new KeyStruct { kfield1 = i, kfield2 = i };
                    var value = new ValueStruct { vfield1 = round, vfield2 = round };
                    _ = bContext.Upsert(key, SpanByte.FromPinnedVariable(ref value), Empty.Default);
                }
            store.Log.FlushAndEvict(wait: true);
            long records = 0;
            using (var iter = store.Log.Scan(store.Log.BeginAddress, store.Log.TailAddress))
                while (iter.GetNext()) records++;
            ClassicAssert.Greater(records, Keys, "CONTROL: some keys hold an older record");

            var before = store.ScanLiveness;
            var scan = new LastWins();
            long cursor = 0;
            while (session.ScanCursor(ref cursor, 256, scan, pushUnproven: pushUnproven)) { }
            var after = store.ScanLiveness;
            return (scan, records, new ScanLivenessCounts(after.PushedNoIo - before.PushedNoIo, after.PushedAfterIo - before.PushedAfterIo,
                after.Superseded - before.Superseded, after.IoIssued - before.IoIssued, after.ChainRereads - before.ChainRereads, after.PushedUnproven - before.PushedUnproven));
        }

        [Test]
        [Category("TsavoriteKV")]
        public void A_scan_asked_to_push_unproven_reads_nothing_and_hands_out_each_keys_newest_record_last()
        {
            var (scan, records, counts) = WriteTwiceEvictAndScan(pushUnproven: true);
            TestContext.Progress.WriteLine($"{Keys} keys in {records} records below memory, pushed unproven: {counts}, handed out {scan.Seen}");

            ClassicAssert.AreEqual(0, counts.IoIssued, $"no liveness proof read the device ({counts}). MUTANT (the flag ignored): every superseded record costs a read");
            ClassicAssert.Greater(counts.PushedUnproven, 0, "records a read would have decided were handed out instead");
            ClassicAssert.AreEqual(Keys, scan.Last.Count, "every key is handed out");
            for (var i = 0; i < Keys; i++)
                ClassicAssert.AreEqual(i % 2 == 0 ? 2L : 1L, scan.Last[i], $"key {i}: the last record handed out is its newest, so a caller copying in order ends on it");
            ClassicAssert.AreEqual(records, scan.Seen + counts.Superseded, "each record is handed out or proven superseded with no read");
        }

        [Test]
        [Category("TsavoriteKV")]
        public void A_scan_not_asked_reads_to_prove_each_record_as_before()
        {
            var (scan, records, counts) = WriteTwiceEvictAndScan(pushUnproven: false);
            ClassicAssert.Greater(counts.IoIssued, 0, "CONTROL: below memory, a proof reads the device");
            ClassicAssert.AreEqual(0, counts.PushedUnproven, "nothing is pushed unproven unless asked");
            ClassicAssert.AreEqual(Keys, scan.Seen, "each key is handed out once, its newest record");
        }
    }
}
