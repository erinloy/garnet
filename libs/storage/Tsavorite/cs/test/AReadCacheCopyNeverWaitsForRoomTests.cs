// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

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
    /// Ziltch broker 10-30: one stack sample caught 17 pool threads at once inside a cold answer's read-cache offer, each in
    /// TryAllocateRetryNow's wait for the read cache's page turn. A read-cache copy is an offer, so it never waits for room: a cache whose
    /// next page cannot turn yet refuses the copy at once and counts it no-room.
    /// </summary>
    [TestFixture]
    internal class AReadCacheCopyNeverWaitsForRoomTests : TestBase
    {
        private const int PageBits = 12;

        private TsavoriteKV<StructStoreFunctions, StructAllocator> store;
        private ClientSession<KeyStruct, InputStruct, OutputStruct, Empty, Functions, StructStoreFunctions, StructAllocator> session;
        private IDevice log;

        [SetUp]
        public void Setup()
        {
            DeleteDirectory(MethodTestDir, wait: true);
            log = Devices.CreateLogDevice(Path.Join(MethodTestDir, "room.log"), deleteOnClose: true);
            store = new(new KVSettings { IndexSize = 1L << 16, LogDevice = log, PageSize = 1L << PageBits, LogMemorySize = 1L << 16, SegmentSize = 1L << 22,
                                         ReadCacheEnabled = true, ReadCacheMemorySize = 4L << PageBits, ReadCachePageSize = 1L << PageBits,
                                         ReadCacheSecondChanceFraction = 0.9 }   // a page goes read-only, so flushed, as soon as the next one opens
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

        [Test]
        [Category("TsavoriteKV")]
        [Category("ReadCache")]
        public void A_copy_into_a_cache_whose_page_cannot_turn_is_refused_at_once()
        {
            var bContext = session.BasicContext;
            var rc = store.readcacheBase;
            var fill = new ValueStruct { vfield1 = 7, vfield2 = 8 };
            var offered = 0;
            for (; rc.GetPage(rc.GetTailAddress()) < 3; offered++)   // the cache's last page opens: the next turn wraps onto page 0's frame
                ClassicAssert.IsTrue(bContext.ConditionalInsertReadCache(new KeyStruct { kfield1 = offered, kfield2 = 1 }, SpanByte.FromPinnedVariable(ref fill), store.Log.TailAddress), "CONTROL: a cache with room takes the copy");
            for (var drains = 0; rc.GetPage(rc.FlushedUntilAddress) < 1 && drains < 1_000; drains++)
            {
                store.epoch.Resume();
                store.epoch.ProtectAndDrain();
                store.epoch.Suspend();
            }
            ClassicAssert.GreaterOrEqual(rc.GetPage(rc.FlushedUntilAddress), 1, "CONTROL: page 0 is flushed, so the wrap waits only on its close");

            // Another thread now holds the epoch, so page 0 can never close: the turn onto its frame is RETRY_NOW, again and again.
            using var release = new ManualResetEventSlim();
            using var holding = new ManualResetEventSlim();
            var holder = new Thread(() =>
            {
                store.epoch.Resume();
                holding.Set();
                release.Wait();
                store.epoch.Suspend();
            }) { IsBackground = true };
            holder.Start();
            holding.Wait();

            var landed = offered;
            var offers = Task.Run(() =>
            {
                var value = new ValueStruct { vfield1 = 7, vfield2 = 8 };
                for (; offered < 4_000; Volatile.Write(ref offered, offered + 1))
                {
                    var key = new KeyStruct { kfield1 = offered, kfield2 = 1 };
                    if (!bContext.ConditionalInsertReadCache(key, SpanByte.FromPinnedVariable(ref value), store.Log.TailAddress))
                        return;
                    Volatile.Write(ref landed, landed + 1);
                }
            });
            try
            {
                ClassicAssert.IsTrue(offers.Wait(10_000),
                    $"the offers came back within 10 s ({Volatile.Read(ref landed)} landed, offer {Volatile.Read(ref offered)} in flight). MUTANT (a copy waits for room): it waits for a page turn that cannot come");
            }
            finally
            {
                release.Set();
                holder.Join();
                offers.Wait();
            }
            TestContext.Progress.WriteLine($"{landed} copies landed; offer {offered} refused; no-room {store.ReadCacheNoRoom}");
            ClassicAssert.AreEqual(1, store.ReadCacheNoRoom, "the refused copy is counted no-room");

            var cached = new ValueStruct { vfield1 = 7, vfield2 = 8 };
            var after = new KeyStruct { kfield1 = 1_000_000, kfield2 = 1 };
            for (var tries = 0; tries < 1_000 && !bContext.ConditionalInsertReadCache(after, SpanByte.FromPinnedVariable(ref cached), store.Log.TailAddress); tries++)
                Thread.Yield();
            InputStruct input = default;
            OutputStruct output = default;
            var read = bContext.Read(after, ref input, ref output);
            ClassicAssert.IsTrue(read.Found, "with the epoch released the cache turns its page and takes copies again");
        }
    }
}
