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
    using StructAllocator = SpanByteAllocator<StoreFunctions<KeyStruct.Comparer, SpanByteRecordTriggers>>;
    using StructStoreFunctions = StoreFunctions<KeyStruct.Comparer, SpanByteRecordTriggers>;

    /// <summary>
    /// A scan hands every record that STARTS before its end. A scan that ended inside a page cut its device read at the sector its
    /// end fell in, so a record that started before the end and ran past that sector was handed with the rest of it as the cleared
    /// frame's zeroes: a whole-looking record with the wrong value. The read now goes on to the page's end.
    /// </summary>
    [TestFixture]
    internal class AScanThatEndsInsideARecordHandsItWholeTests : TestBase
    {
        private const int Records = 20_000;
        private const int Asked = 3_000;
        private const int PageBits = 16;

        private TsavoriteKV<StructStoreFunctions, StructAllocator> store;
        private ClientSession<KeyStruct, InputStruct, OutputStruct, Empty, Functions, StructStoreFunctions, StructAllocator> session;
        private IDevice log;

        [SetUp]
        public void Setup()
        {
            DeleteDirectory(MethodTestDir, wait: true);
            log = Devices.CreateLogDevice(Path.Join(MethodTestDir, "ends.log"), deleteOnClose: true);
            store = new(new KVSettings { IndexSize = 1L << 13, LogDevice = log, PageSize = 1L << PageBits, LogMemorySize = 1L << 20, SegmentSize = 1L << 22 }
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

        private sealed class Seen : IScanIteratorFunctions
        {
            public readonly List<long> Addresses = [];
            public long Handed, Wrong, FirstWrong = -1;
            public bool OnStart(long beginAddress, long endAddress) => true;
            public bool Reader<TSourceLogRecord>(in TSourceLogRecord logRecord, RecordMetadata recordMetadata, long numberOfRecords, out CursorRecordResult cursorRecordResult)
                where TSourceLogRecord : ISourceLogRecord
            {
                cursorRecordResult = CursorRecordResult.Accept;
                Handed++;
                Addresses.Add(recordMetadata.Address);
                var key = MemoryMarshal.Read<KeyStruct>(logRecord.Key);
                var value = MemoryMarshal.Read<ValueStruct>(logRecord.ValueSpan);
                if (value.vfield1 != key.kfield1 + 1 || value.vfield2 != key.kfield1 + 2) { Wrong++; if (FirstWrong < 0) FirstWrong = recordMetadata.Address; }
                return true;
            }
            public void OnException(Exception exception, long numberOfRecords) => throw exception;
            public void OnStop(bool completed, long numberOfRecords) { }
        }

        [Test]
        [Category("TsavoriteKV")]
        public void A_scan_of_one_address_hands_the_record_there_with_its_own_value([Values(DiskScanBufferingMode.SinglePageBuffering, DiskScanBufferingMode.DoublePageBuffering)] DiskScanBufferingMode buffering)   // NoBuffering scans memory only and refuses below head
        {
            var bContext = session.BasicContext;
            for (var i = 0; i < Records; i++)
            {
                var key = new KeyStruct { kfield1 = i, kfield2 = i };
                var value = new ValueStruct { vfield1 = i + 1, vfield2 = i + 2 };
                _ = bContext.Upsert(key, SpanByte.FromPinnedVariable(ref value), Empty.Default);
            }
            store.Log.FlushAndEvict(wait: true);
            ClassicAssert.AreEqual(store.Log.TailAddress, store.Log.HeadAddress, "CONTROL: every page is read from the device");

            var whole = new Seen();
            _ = store.Log.Scan(ref whole, store.Log.BeginAddress, store.Log.TailAddress, buffering);
            ClassicAssert.AreEqual(Records, whole.Handed, "CONTROL: a scan to the tail hands every record");
            ClassicAssert.AreEqual(0, whole.Wrong, "CONTROL: a scan to the tail hands every record with its own value");

            // Records whose bytes cross a 512 B sector boundary: the ones a read cut at the end's sector would hand short.
            var crossing = 0;
            var one = new Seen();
            for (var i = 0; i < Asked; i++)
            {
                var at = whole.Addresses[i];
                var end = i + 1 < whole.Addresses.Count ? whole.Addresses[i + 1] : store.Log.TailAddress;
                if (at >> 9 != (end - 1) >> 9 && at >> PageBits == (end - 1) >> PageBits) crossing++;
                _ = store.Log.Scan(ref one, at, at + 1, buffering);
            }
            ClassicAssert.Greater(crossing, 50, "CONTROL: many of the records asked for cross a sector boundary inside their page");
            ClassicAssert.AreEqual(Asked, one.Handed, "a scan of [a, a + 1) hands the one record that starts at a");
            ClassicAssert.AreEqual(0, one.Wrong, $"a record handed by a scan that ends inside it carries its own value (the first that did not is at {one.FirstWrong})");
        }
    }
}
