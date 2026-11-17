// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

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
    /// ONE PREDICATE SAYS WHETHER 16 BYTES ARE A RECORD'S HEADER, AND EVERY RECORD THE STORE WROTE PASSES IT.
    ///
    /// <para><see cref="RecordHeaderSanity.IsRecordHeader"/> is shared by the scan iterator's guard and the pending-read walk. A
    /// false refusal there loses a live record, so the first arm walks every record a store wrote (upserts, updates, deletes,
    /// across pages) and requires every one to pass. The second arm takes a real header and breaks one property at a time; each
    /// must be refused with its own reason. The length arm is prod's case (webfrontend 11-16, log:137977485728: a header 612,768
    /// bytes into a 1 MiB page asking for 14,418,320 bytes). Mutant: a predicate that always answers true turns the second arm red.</para>
    /// </summary>
    [TestFixture]
    internal class ARecordHeaderIsKnownFromAnyOtherSixteenBytesTests : TestBase
    {
        private const int Records = 3_000;
        private const int PageBits = 12;

        private TsavoriteKV<StructStoreFunctions, StructAllocator> store;
        private ClientSession<KeyStruct, InputStruct, OutputStruct, Empty, Functions, StructStoreFunctions, StructAllocator> session;
        private IDevice log;

        [SetUp]
        public void Setup()
        {
            DeleteDirectory(MethodTestDir, wait: true);
            log = Devices.CreateLogDevice(Path.Join(MethodTestDir, "headersanity.log"), deleteOnClose: true);
            // Memory holds the whole log, so every record is read in place.
            store = new(new KVSettings { IndexSize = 1L << 16, LogDevice = log, PageSize = 1L << PageBits, LogMemorySize = 1L << 22, SegmentSize = 1L << 22 }
                , StoreFunctions.Create(KeyStruct.Comparer.Instance, SpanByteRecordTriggers.Instance)
                , (allocatorSettings, storeFunctions) => new(allocatorSettings, storeFunctions));
            session = store.NewSession<KeyStruct, InputStruct, OutputStruct, Empty, Functions>(new Functions());

            var bContext = session.BasicContext;
            for (var i = 0; i < Records; i++)
            {
                var key = new KeyStruct { kfield1 = i, kfield2 = i + 1 };
                var value = new ValueStruct { vfield1 = i, vfield2 = -i };
                _ = bContext.Upsert(key, SpanByte.FromPinnedVariable(ref value), Empty.Default);
            }
            // A second record for some keys (a chain link to an older record) and a tombstone for others.
            store.Log.ShiftReadOnlyAddress(store.Log.TailAddress, wait: true);
            for (var i = 0; i < Records; i += 3)
            {
                var key = new KeyStruct { kfield1 = i, kfield2 = i + 1 };
                var value = new ValueStruct { vfield1 = i + 7, vfield2 = i };
                _ = bContext.Upsert(key, SpanByte.FromPinnedVariable(ref value), Empty.Default);
            }
            for (var i = 1; i < Records; i += 5)
                _ = bContext.Delete(new KeyStruct { kfield1 = i, kfield2 = i + 1 }, Empty.Default);
        }

        [TearDown]
        public void TearDown()
        {
            session?.Dispose(); session = null;
            store?.Dispose(); store = null;
            log?.Dispose(); log = null;
            OnTearDown();
        }

        private (long info, ulong data) WordsAt(long address)
        {
            var physical = store.hlogBase.GetPhysicalAddress(address);
            return (Marshal.ReadInt64((nint)physical), (ulong)Marshal.ReadInt64((nint)physical + 8));
        }

        private bool Judge(long info, ulong data, long address, out string reason)
            => RecordHeaderSanity.IsRecordHeader(info, data, address, (int)store.hlogBase.GetOffsetOnPage(address), store.hlogBase.PageSize, out reason);

        [Test]
        [Category("TsavoriteKV")]
        public void Every_record_the_store_wrote_is_a_header()
        {
            ClassicAssert.LessOrEqual(store.Log.HeadAddress, store.Log.BeginAddress, "CONTROL: the whole log is in memory");
            var addresses = new List<long>();
            using (var scan = store.Log.Scan(store.Log.BeginAddress, store.Log.TailAddress))
                while (scan.GetNext())
                    addresses.Add(scan.CurrentAddress);
            // The scan hands out live records only (a tombstone is not handed out), so the floor is the writes and the updates.
            ClassicAssert.GreaterOrEqual(addresses.Count, Records + Records / 3, "CONTROL: the scan reached the writes and the updates");
            ClassicAssert.Greater(store.Log.TailAddress >> PageBits, 4L, "CONTROL: the records span several pages");

            var refused = new List<string>();
            var linked = 0;
            foreach (var address in addresses)
            {
                var (info, data) = WordsAt(address);
                if ((info & ((1L << 48) - 1)) != 0)
                    linked++;
                if (!Judge(info, data, address, out var reason))
                    refused.Add($"{address}: {reason}");
            }
            ClassicAssert.Greater(linked, Records / 4, "CONTROL: many records link to an older record of their key");
            CollectionAssert.IsEmpty(refused, "a record the store wrote is never refused as a header");
        }

        [Test]
        [Category("TsavoriteKV")]
        public void Each_broken_property_is_refused_with_its_own_reason()
        {
            // A real record with an older record of its key, so its previous address is set: key 0's update.
            long address = 0;
            using (var scan = store.Log.Scan(store.Log.BeginAddress, store.Log.TailAddress))
                while (scan.GetNext())
                    if ((WordsAt(scan.CurrentAddress).info & ((1L << 48) - 1)) != 0) { address = scan.CurrentAddress; break; }
            ClassicAssert.Greater(address, 0L, "CONTROL: a linked record exists");
            var (info, data) = WordsAt(address);
            ClassicAssert.IsTrue(Judge(info, data, address, out var none), $"CONTROL: the real header passes ({none})");

            var header = new RecordDataHeader { word = data };
            header.ValueLength = 14_418_320;
            Refused(info, header.word, address, "runs past");

            Refused(info | (1L << 53), data, address, "reserved bits");
            Refused((info & ~((1L << 48) - 1)) | address, data, address, "not below");
            Refused((info & ~((1L << 48) - 1)) | (1L << 47) | 64, data, address, "read cache");
            Refused(info, data | (1UL << 5), address, "unused bit");
            Refused(info, data | (1UL << 1) | (1UL << 2), address, "both inline and an object");
        }

        private void Refused(long info, ulong data, long address, string expected)
        {
            ClassicAssert.IsFalse(Judge(info, data, address, out var reason), $"refused: {expected}");
            StringAssert.Contains(expected, reason);
        }
    }
}
