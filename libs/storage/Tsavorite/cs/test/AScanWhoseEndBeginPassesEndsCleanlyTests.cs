// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.Collections.Generic;
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
    /// GetNext checked its cursor against the scan's end, and only then moved a cursor below BeginAddress up to it. When begin had
    /// moved past the end, begin's page was never loaded into a frame (BufferAndLoad loads nothing at or past the end), yet the scan
    /// waited on that frame and read from it: a frame never used held no completion event (NullReferenceException, the broker's
    /// 'prefix scan FAULTED' at 10-31 08:16Z and 10:03Z), and a frame used before held another page, whose records came back at
    /// addresses past the end. A scan whose end begin has passed now ends there.
    /// </summary>
    [TestFixture]
    internal class AScanWhoseEndBeginPassesEndsCleanlyTests : TestBase
    {
        private const int Records = 40_000;
        private const int PageBits = 12;

        private TsavoriteKV<StructStoreFunctions, StructAllocator> store;
        private ClientSession<KeyStruct, InputStruct, OutputStruct, Empty, Functions, StructStoreFunctions, StructAllocator> session;
        private IDevice log;

        [SetUp]
        public void Setup()
        {
            DeleteDirectory(MethodTestDir, wait: true);
            log = Devices.CreateLogDevice(Path.Join(MethodTestDir, "beginpast.log"), deleteOnClose: true);
            store = new(new KVSettings { IndexSize = 1L << 16, LogDevice = log, PageSize = 1L << PageBits, LogMemorySize = 1L << 15, SegmentSize = 1L << 22 }
                , StoreFunctions.Create(KeyStruct.Comparer.Instance, SpanByteRecordTriggers.Instance)
                , (allocatorSettings, storeFunctions) => new(allocatorSettings, storeFunctions));
            session = store.NewSession<KeyStruct, InputStruct, OutputStruct, Empty, Functions>(new Functions());

            var bContext = session.BasicContext;
            var value = new ValueStruct { vfield1 = 7, vfield2 = 8 };
            for (var i = 0; i < Records; i++)
            {
                var key = new KeyStruct { kfield1 = i, kfield2 = i };
                _ = bContext.Upsert(key, SpanByte.FromPinnedVariable(ref value), Empty.Default);
            }
            store.Log.FlushAndEvict(wait: true);
        }

        [TearDown]
        public void TearDown()
        {
            session?.Dispose(); session = null;
            store?.Dispose(); store = null;
            log?.Dispose(); log = null;
            OnTearDown();
        }

        /// <summary>The first record address on or after <paramref name="page"/>'s start, so begin moves to a record.</summary>
        private long RecordOnPage(List<long> addresses, long page)
            => addresses.Find(a => a >= page << PageBits);

        private List<long> RecordAddresses()
        {
            var addresses = new List<long>();
            using var all = store.Log.Scan(store.Log.BeginAddress, store.Log.TailAddress);
            while (all.GetNext())
                addresses.Add(all.CurrentAddress);
            return addresses;
        }

        [Test]
        [Category("TsavoriteKV")]
        public void A_scan_whose_end_begin_passed_before_its_first_record_ends_with_none()
        {
            var addresses = RecordAddresses();
            ClassicAssert.Greater(store.Log.HeadAddress, 12L << PageBits, "CONTROL: the pages the scan reads are on disk, below head");
            var end = 4L << PageBits;
            using var scan = store.Log.Scan(store.Log.BeginAddress, end);

            store.Log.ShiftBeginAddress(RecordOnPage(addresses, 6));

            var handed = new List<long>();
            Assert.DoesNotThrow(() => { while (scan.GetNext()) handed.Add(scan.CurrentAddress); }, "a scan whose end begin passed ends, never faults on a frame it never loaded");
            CollectionAssert.IsEmpty(handed, $"nothing lies below the end ({end}) at or above begin ({store.Log.BeginAddress})");
        }

        [Test]
        [Category("TsavoriteKV")]
        public void A_scan_whose_end_begin_passes_midway_hands_out_nothing_past_its_end()
        {
            var addresses = RecordAddresses();
            ClassicAssert.Greater(store.Log.HeadAddress, 12L << PageBits, "CONTROL: the pages the scan reads are on disk, below head");
            var end = 5L << PageBits;
            using var scan = store.Log.Scan(store.Log.BeginAddress, end);

            var handed = new List<long>();
            while (scan.GetNext() && scan.CurrentAddress < 2L << PageBits)
                handed.Add(scan.CurrentAddress);
            ClassicAssert.Greater(handed.Count, 10, "CONTROL: the scan read from its frames before begin moved");

            // Page 8 maps onto the frame page 2 was just loaded into, so a scan reading on reads page 2's records as page 8's.
            store.Log.ShiftBeginAddress(RecordOnPage(addresses, 8));
            var past = new List<long>();
            Assert.DoesNotThrow(() => { while (scan.GetNext()) past.Add(scan.CurrentAddress); }, "the scan ends where begin passed its end");
            CollectionAssert.IsEmpty(past, $"no record is handed out at or past the end ({end}) once begin ({store.Log.BeginAddress}) passed it");
        }

        [Test]
        [Category("TsavoriteKV")]
        public void A_scan_whose_end_begin_has_not_passed_reads_from_begin_to_its_end()
        {
            var addresses = RecordAddresses();
            var end = 9L << PageBits;
            using var scan = store.Log.Scan(store.Log.BeginAddress, end);

            var from = RecordOnPage(addresses, 6);
            store.Log.ShiftBeginAddress(from);
            var handed = new List<long>();
            while (scan.GetNext())
                handed.Add(scan.CurrentAddress);

            var expected = addresses.FindAll(a => a >= from && a < end);
            ClassicAssert.Greater(expected.Count, 10, "CONTROL: records lie between begin and the end");
            CollectionAssert.AreEqual(expected, handed, "a scan whose end begin has not passed hands out every record from begin to its end, once");
        }
    }
}
