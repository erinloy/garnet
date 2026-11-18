// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
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
    /// SPARK 11-18: a recovery that replayed more pages than its ring holds left each frame past the tail's page holding a page it read below the
    /// head, and the page turns after it wrote into those frames as they found them. The bytes past a page's last record were the records of the
    /// page one ring earlier (log page 179,657 held page 179,593 from offset 1,041,136), and a scan read them as records. Every frame past the
    /// recovered tail's page is clear when recovery returns, whether a size tracker counts the log or not.
    /// </summary>
    [TestFixture]
    internal class ARecoveredLogTurnsIntoAClearedFrameTests : TestBase
    {
        private const int PageBits = 12, Pages = 64;

        private TsavoriteKV<StructStoreFunctions, StructAllocator> Open(IDevice device)
            => new(new() { IndexSize = 1L << 16, LogDevice = device, LogMemorySize = (long)Pages << PageBits, PageSize = 1L << PageBits, CheckpointDir = MethodTestDir },
                StoreFunctions.Create(KeyStruct.Comparer.Instance, SpanByteRecordTriggers.Instance),
                (allocatorSettings, storeFunctions) => new(allocatorSettings, storeFunctions));

        private static void Upsert(TsavoriteKV<StructStoreFunctions, StructAllocator> store, int from, int count, long value)
        {
            using var session = store.NewSession<KeyStruct, InputStruct, OutputStruct, Empty, Functions>(new Functions());
            for (var i = from; i < from + count; i++)
            {
                var key = new KeyStruct { kfield1 = i, kfield2 = i + 1 };
                var v = new ValueStruct { vfield1 = value, vfield2 = value + 1 };
                _ = session.BasicContext.Upsert(key, SpanByte.FromPinnedVariable(ref v), Empty.Default);
            }
            _ = session.BasicContext.CompletePending(true);
        }

        [SetUp]
        public void Setup() { DeleteDirectory(MethodTestDir); _ = Directory.CreateDirectory(MethodTestDir); }

        [TearDown]
        public void TearDown() => OnTearDown();

        [Test]
        [Category("TsavoriteKV")]
        [Category("CheckpointRestore")]
        public async Task Every_frame_past_a_recovered_tails_page_is_clear_when_the_replay_wrapped_its_ring([Values] bool trackerDuringRecovery)
        {
            Guid indexToken, logToken;
            {
                using var device = Devices.CreateLogDevice(Path.Join(MethodTestDir, "frames.log"));
                using var store = Open(device);
                Upsert(store, 0, 100, 1);
                var (indexOk, index) = await store.TakeIndexCheckpointAsync();
                ClassicAssert.IsTrue(indexOk, "the index checkpoint completes");
                indexToken = index;
                Upsert(store, 100, 100 << (PageBits - 5), 2);   // ~100 pages past the index's start: the replay wraps a 64 page ring
                store.Log.ShiftReadOnlyAddress(store.Log.TailAddress, wait: true);
                store.Log.ShiftHeadAddress(store.hlogBase.GetFirstValidLogicalAddressOnPage(store.hlogBase.GetPage(store.Log.TailAddress)), wait: true);
                var (logOk, log) = await store.TakeHybridLogCheckpointAsync(CheckpointType.FoldOver);
                ClassicAssert.IsTrue(logOk, "the log checkpoint completes");
                logToken = log;
            }

            using var device2 = Devices.CreateLogDevice(Path.Join(MethodTestDir, "frames.log"));
            using var store2 = Open(device2);
            if (trackerDuringRecovery)
            {
                var budget = (long)(Pages / 2) << PageBits;
                store2.Log.SetLogSizeTracker(new LogSizeTracker<StructStoreFunctions, StructAllocator>(store2.Log, budget, budget / 8, budget / 16, logger: null));
            }
            _ = await store2.RecoverAsync(indexToken, logToken);

            var hlog = store2.hlogBase;
            var (headPage, tailPage) = (hlog.GetPage(store2.Log.HeadAddress), hlog.GetPage(store2.Log.TailAddress));
            ClassicAssert.Greater(tailPage - hlog.GetPage(hlog.BeginAddress), Pages, "control: the replay read more pages than the ring holds");

            var page = new byte[1 << PageBits];
            var (allocatedPastTail, dirty) = (0, "");
            for (var p = tailPage + 1; p < headPage + hlog.BufferSize; p++)
            {
                if (!hlog.IsAllocated((int)(p % hlog.BufferSize))) continue;
                allocatedPastTail++;
                Marshal.Copy((IntPtr)hlog.GetPhysicalAddress(hlog.GetLogicalAddressOfStartOfPage(p)), page, 0, page.Length);
                var first = Array.FindIndex(page, PageHeader.Size, b => b != 0);
                if (first >= 0) dirty += $" page {p} (frame {p % hlog.BufferSize}) holds a byte at offset {first};";
            }
            TestContext.Progress.WriteLine($"frames: head page {headPage}, tail page {tailPage}, allocated past the tail {allocatedPastTail}, cleared {hlog.framesClearedPastTail}");
            ClassicAssert.GreaterOrEqual(allocatedPastTail, 1, "control: the allocate-ahead page past the tail is allocated");
            ClassicAssert.IsEmpty(dirty, "MUTANT (a frame past the tail keeps the page recovery read into it):" + dirty);
        }
    }
}
