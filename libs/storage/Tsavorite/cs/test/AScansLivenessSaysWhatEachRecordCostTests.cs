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
    using StructAllocator = SpanByteAllocator<StoreFunctions<AScansLivenessSaysWhatEachRecordCostTests.Chained, SpanByteRecordTriggers>>;
    using StructStoreFunctions = StoreFunctions<AScansLivenessSaysWhatEachRecordCostTests.Chained, SpanByteRecordTriggers>;

    /// <summary>
    /// mesh 10-30 10:30Z: the broker's hot pass held Z: at a queue of 90-190, and of its ~5.5k reads/s only ~560/s were RG's own pending
    /// reads. Stack samples put the pass in ConditionalScanPush: for each record a scan hands out, the scan proves the record is the key's
    /// newest by walking the key's chain from its index entry, and every step below memory is an IO that nothing counted. A log now counts
    /// how its scans decided each record (pushed with no IO, pushed after IO, superseded) and the IOs and chain re-reads that cost.
    /// </summary>
    [TestFixture]
    internal class AScansLivenessSaysWhatEachRecordCostTests : TestBase
    {
        private const int Keys = 2_000;
        private const int Chains = 200;

        /// <summary>Keys hash to one of <see cref="Chains"/> codes, so each chain runs through ten keys' records.</summary>
        internal struct Chained : IKeyComparer
        {
            public readonly long GetHashCode64<TKey>(TKey key)
                where TKey : IKey
#if NET9_0_OR_GREATER
                    , allows ref struct
#endif
                => Utility.GetHashCode(key.KeyBytes.AsRef<KeyStruct>().kfield1 % Chains);

            public readonly bool Equals<TFirstKey, TSecondKey>(TFirstKey key1, TSecondKey key2)
                where TFirstKey : IKey
#if NET9_0_OR_GREATER
                    , allows ref struct
#endif
                where TSecondKey : IKey
#if NET9_0_OR_GREATER
                    , allows ref struct
#endif
                => key1.KeyBytes.AsRef<KeyStruct>().kfield1 == key2.KeyBytes.AsRef<KeyStruct>().kfield1;
        }

        private TsavoriteKV<StructStoreFunctions, StructAllocator> store;
        private ClientSession<KeyStruct, InputStruct, OutputStruct, Empty, Functions, StructStoreFunctions, StructAllocator> session;
        private IDevice log;

        private void Open(long indexBytes, long memoryBytes)
        {
            DeleteDirectory(MethodTestDir, wait: true);
            log = Devices.CreateLogDevice(Path.Join(MethodTestDir, "liveness.log"), deleteOnClose: true);
            store = new(new KVSettings { IndexSize = indexBytes, LogDevice = log, PageSize = 1L << 12, LogMemorySize = memoryBytes, SegmentSize = 1L << 22 }
                , StoreFunctions.Create(new Chained(), SpanByteRecordTriggers.Instance)
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

        private sealed class Newest : IScanIteratorFunctions
        {
            public long Seen, Stale, Records;
            public bool OnStart(long beginAddress, long endAddress) => true;
            public bool Reader<TSourceLogRecord>(in TSourceLogRecord logRecord, RecordMetadata recordMetadata, long numberOfRecords, out CursorRecordResult cursorRecordResult)
                where TSourceLogRecord : ISourceLogRecord
            {
                cursorRecordResult = CursorRecordResult.Accept;
                Seen++;
                var key = MemoryMarshal.Read<KeyStruct>(logRecord.Key);
                var value = MemoryMarshal.Read<ValueStruct>(logRecord.ValueSpan);
                if (value.vfield1 != (key.kfield1 % 2 == 0 ? 2 : 1)) Stale++;
                return true;
            }
            public void OnException(Exception exception, long numberOfRecords) => throw exception;
            public void OnStop(bool completed, long numberOfRecords) { }
        }

        /// <summary>Every key once, then every even key again: the odd keys' records and the even keys' second records are live.</summary>
        private Newest WriteTwiceAndScan(bool evict)
        {
            var bContext = session.BasicContext;
            for (var round = 1; round <= 2; round++)
            {
                for (var i = 0; i < Keys; i++)
                {
                    if (round == 2 && i % 2 != 0) continue;
                    var key = new KeyStruct { kfield1 = i, kfield2 = i };
                    var value = new ValueStruct { vfield1 = round, vfield2 = round };
                    _ = bContext.Upsert(key, SpanByte.FromPinnedVariable(ref value), Empty.Default);
                }
            }
            if (evict)
                store.Log.FlushAndEvict(wait: true);

            // A rewrite in the mutable region updates in place, so the records to judge are what the log holds, not two per even key.
            long records = 0;
            using (var iter = store.Log.Scan(store.Log.BeginAddress, store.Log.TailAddress))
                while (iter.GetNext()) records++;
            ClassicAssert.Greater(records, Keys, "CONTROL: some keys hold an older record");

            var newest = new Newest { Records = records };
            long cursor = 0;
            while (session.ScanCursor(ref cursor, long.MaxValue, newest)) { }
            ClassicAssert.AreEqual(Keys, newest.Seen, "the scan hands out each key once");
            ClassicAssert.AreEqual(0, newest.Stale, "and only its newest record");
            return newest;
        }

        [Test]
        [Category("TsavoriteKV")]
        public void A_scan_below_memory_counts_each_records_verdict_and_the_reads_it_cost()
        {
            Open(indexBytes: 1L << 20, memoryBytes: 1L << 16);
            var newest = WriteTwiceAndScan(evict: true);
            var liveness = store.ScanLiveness;
            TestContext.Progress.WriteLine($"{Keys} keys in {newest.Records} records, all below memory: {liveness}");

            ClassicAssert.AreEqual(Keys, liveness.PushedNoIo + liveness.PushedAfterIo,
                $"every live record is counted as pushed ({liveness}). MUTANT (a push after IO uncounted): a scan's IOs read as free");
            ClassicAssert.Greater(liveness.PushedAfterIo, 0, "a live record whose index entry heads another key's chain needed a read");
            ClassicAssert.AreEqual(newest.Records - Keys, liveness.Superseded, "every older record of a rewritten key is counted as superseded");
            ClassicAssert.GreaterOrEqual(liveness.IoIssued, liveness.PushedAfterIo, "each decision made after IO began with an IO");
            ClassicAssert.Greater(liveness.ChainRereads, 0, "CONTROL: chains ran through other keys' records on the device");
        }

        [Test]
        [Category("TsavoriteKV")]
        public void A_scan_in_memory_decides_every_record_with_no_io()
        {
            Open(indexBytes: 1L << 20, memoryBytes: 1L << 22);
            var newest = WriteTwiceAndScan(evict: false);
            var liveness = store.ScanLiveness;
            ClassicAssert.AreEqual(new ScanLivenessCounts(Keys, 0, newest.Records - Keys, 0, 0), liveness, "a log in memory proves each record from its chain alone");
        }
    }
}
