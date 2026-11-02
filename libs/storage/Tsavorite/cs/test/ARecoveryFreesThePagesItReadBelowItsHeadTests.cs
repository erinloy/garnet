// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.IO;
using System.Threading;
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
    /// SPARK 11-01: recovery over an old index reads the log from the index's start to replay it, and keeps resident only the pages from the
    /// checkpoint's head. The pages read below the head stayed allocated, and a size tracker counted them against the log's budget for the
    /// store's life; on a budget they filled, the first page turn waited on an eviction that could free nothing, and every write hung. They are
    /// returned whether the tracker counts the log during recovery or is attached after it (as a window opened over a recovered store is).
    /// </summary>
    [TestFixture]
    internal class ARecoveryFreesThePagesItReadBelowItsHeadTests : TestBase
    {
        private const int PageBits = 12, Pages = 64, BudgetPages = 32;

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
        public async Task Recovery_over_an_old_index_keeps_no_page_below_its_head_and_a_budgeted_log_still_turns_its_page([Values] bool trackerAfterRecovery)
        {
            Guid indexToken, logToken;
            long head, tail;
            {
                using var device = Devices.CreateLogDevice(Path.Join(MethodTestDir, "free.log"));
                using var store = Open(device);
                Upsert(store, 0, 100, 1);
                var (indexOk, index) = await store.TakeIndexCheckpointAsync();
                ClassicAssert.IsTrue(indexOk, "the index checkpoint completes");
                indexToken = index;
                Upsert(store, 100, 40 << (PageBits - 5), 2);   // ~40 pages past the index's start
                store.Log.ShiftReadOnlyAddress(store.Log.TailAddress, wait: true);
                store.Log.ShiftHeadAddress(store.hlogBase.GetFirstValidLogicalAddressOnPage(store.hlogBase.GetPage(store.Log.TailAddress)), wait: true);
                var (logOk, log) = await store.TakeHybridLogCheckpointAsync(CheckpointType.FoldOver);
                ClassicAssert.IsTrue(logOk, "the log checkpoint completes");
                logToken = log;
                (head, tail) = (store.Log.HeadAddress, store.Log.TailAddress);
            }
            ClassicAssert.Less((tail - head) >> PageBits, 2, "control: the checkpoint's head sits on its tail's page");

            var device2 = Devices.CreateLogDevice(Path.Join(MethodTestDir, "free.log"));
            var store2 = Open(device2);
            var budget = (long)BudgetPages << PageBits;
            var tracker = new LogSizeTracker<StructStoreFunctions, StructAllocator>(store2.Log, budget, budget / 8, budget / 16, logger: null);
            if (!trackerAfterRecovery) store2.Log.SetLogSizeTracker(tracker);
            _ = await store2.RecoverAsync(indexToken, logToken);
            var readBelowHead = store2.hlogBase.GetPage(store2.Log.HeadAddress) - store2.hlogBase.GetPage(store2.hlogBase.BeginAddress);
            if (trackerAfterRecovery) store2.Log.SetLogSizeTracker(tracker);
            tracker.Start(CancellationToken.None);

            var (allocated, window, returned) = (store2.Log.AllocatedPageCount, store2.Log.ResidentWindowPages, store2.Log.PagesReturnedOutsideResident);
            TestContext.Progress.WriteLine($"rcfree: allocated {allocated}, resident window {window}, returned {returned}, pages below head {readBelowHead}");
            ClassicAssert.Greater(readBelowHead, BudgetPages, "control: recovery replayed more pages below its head than the budget holds");

            // A write that turns pages past the one the head sits on, bounded: today's recovery hangs it forever, so a hung writer is left behind.
            var write = Task.Run(() => Upsert(store2, 1_000_000, 4 << (PageBits - 5), 3));
            if (!write.Wait(TimeSpan.FromSeconds(20)))
                Assert.Fail($"a write past the head's page hung: {allocated} pages allocated for a resident window of {window}; MUTANT (the pages read below the head stay allocated)");

            ClassicAssert.LessOrEqual(allocated, window, "recovery keeps allocated only the pages of its resident window");
            ClassicAssert.Greater(returned, BudgetPages, "the pages read below the head were returned");
            store2.Dispose();
            device2.Dispose();
        }
    }
}
