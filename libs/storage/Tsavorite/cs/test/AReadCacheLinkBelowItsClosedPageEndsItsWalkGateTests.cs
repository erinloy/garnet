// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.Collections.Concurrent;
using System.IO;
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
    /// A read-cache link left pointing at a record whose page has closed cannot be mended by waiting. Before this gate a reader met it,
    /// waited out a close that had already happened, re-read the same slot and went round again forever (the Ziltch broker's readers, 10-30),
    /// and an index checkpoint's rewalk did the same with the checkpoint held open. Now each walk throws a named
    /// <see cref="ReadCacheChainLostException"/> within a bound, and the store counts the missed eviction and tells its observer.
    /// The cause of the 10-30 dangling links is still unknown; this gate plants one by hand.
    /// </summary>
    [TestFixture]
    internal class AReadCacheLinkBelowItsClosedPageEndsItsWalkGateTests : TestBase
    {
        static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

        private TsavoriteKV<StructStoreFunctions, StructAllocator> store;
        private ClientSession<KeyStruct, InputStruct, OutputStruct, Empty, Functions, StructStoreFunctions, StructAllocator> session;
        private IDevice log;
        private readonly ConcurrentQueue<(long count, string walker)> told = new();

        [SetUp]
        public void Setup()
        {
            DeleteDirectory(MethodTestDir, wait: true);
            told.Clear();
            log = Devices.CreateLogDevice(Path.Join(MethodTestDir, "lost.log"), deleteOnClose: true);
            store = new(new KVSettings { IndexSize = 1L << 13, LogDevice = log, PageSize = 1L << 12, LogMemorySize = 1L << 16, SegmentSize = 1L << 22,
                                         CheckpointDir = MethodTestDir, ReadCacheEnabled = true, ReadCacheMemorySize = 1L << 15, ReadCachePageSize = 1L << 12 }
                , StoreFunctions.Create(KeyStruct.Comparer.Instance, SpanByteRecordTriggers.Instance)
                , (allocatorSettings, storeFunctions) => new(allocatorSettings, storeFunctions));
            store.ReadCacheChainLost = (count, walker) => told.Enqueue((count, walker));
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
        public void A_read_that_meets_the_lost_link_throws_it_within_a_bound_and_counts_each_one()
        {
            var key = PlantLostLink();

            for (var hit = 1; hit <= 2; hit++)
            {
                var read = Task.Run(() =>
                {
                    using var reader = store.NewSession<KeyStruct, InputStruct, OutputStruct, Empty, Functions>(new Functions());
                    InputStruct input = default;
                    OutputStruct output = default;
                    _ = reader.BasicContext.Read(key, ref input, ref output);
                });
                ClassicAssert.IsTrue(Ends(read), $"read {hit} ends within {Bound}: it does not wait out a close that has already happened");
                var lost = FindLost(read.Exception);
                ClassicAssert.IsNotNull(lost, $"read {hit} throws ReadCacheChainLostException, not {read.Exception?.GetBaseException().GetType().Name ?? "nothing"}");
                ClassicAssert.AreEqual(ReadCacheChainLostException.ReadWalker, lost.Walker);
                ClassicAssert.AreEqual(hit, lost.MissedEvictions);
                ClassicAssert.AreEqual(hit, store.ReadCacheMissedEvictions, "every hit is counted");
            }
            CollectionAssert.AreEqual(new[] { (1L, "read"), (2L, "read") }, told.ToArray(), "the observer hears each hit, in order");

            AssertTheStoreStillWrites(424242);
        }

        [Test]
        [Category("TsavoriteKV")]
        [Category("ReadCache")]
        [Category("CheckPointRestore")]
        public void An_index_checkpoint_that_meets_the_lost_link_fails_within_a_bound_and_counts_it()
        {
            _ = PlantLostLink();

            var checkpoint = Task.Run(async () => await store.TakeIndexCheckpointAsync());
            ClassicAssert.IsTrue(Ends(checkpoint), $"the index checkpoint ends within {Bound}: its rewalk does not spin on the lost link");
            ClassicAssert.IsFalse(checkpoint.Status == TaskStatus.RanToCompletion && checkpoint.Result.success,
                "an index checkpoint that could not walk a bucket is not reported taken");
            ClassicAssert.AreEqual(1, store.ReadCacheMissedEvictions, "the hit is counted");
            CollectionAssert.AreEqual(new[] { (1L, "checkpoint") }, told.ToArray(), "the observer hears it from the checkpoint walker");

            AssertTheStoreStillWrites(434343);
        }

        // A key whose hash slot names a read-cache copy that was evicted: what the 10-30 dump found. The copy is cached, its page is closed
        // (which unlinks it), and the old link is written back into the slot.
        private KeyStruct PlantLostLink()
        {
            var bContext = session.BasicContext;
            var key = new KeyStruct { kfield1 = 7, kfield2 = 1 };
            var value = new ValueStruct { vfield1 = 7, vfield2 = 8 };
            _ = bContext.Upsert(key, SpanByte.FromPinnedVariable(ref value), Empty.Default);
            store.Log.FlushAndEvict(wait: true);
            ClassicAssert.IsTrue(bContext.ConditionalInsertReadCache(key, SpanByte.FromPinnedVariable(ref value), store.Log.TailAddress), "CONTROL: the key is cached");

            HashEntryInfo hei = new(store.storeFunctions.GetKeyHashCode64(key));
            ClassicAssert.IsTrue(store.FindTag(ref hei) && hei.IsReadCache, "CONTROL: a read-cache copy heads the key's chain");
            var cachedLink = hei.Address;

            var neighbour = 1000;
            while (store.readcacheBase.ClosedUntilAddress <= LogAddress.AbsoluteAddress(cachedLink))
                Cache(neighbour++);

            hei = new(store.storeFunctions.GetKeyHashCode64(key));
            ClassicAssert.IsTrue(store.FindTag(ref hei), "CONTROL: the key keeps its tag");
            ClassicAssert.IsFalse(hei.IsReadCache, "CONTROL: the close unlinked the copy");
            ClassicAssert.IsTrue(hei.TryCAS(cachedLink), "CONTROL: the lost link is planted");
            ClassicAssert.Less(LogAddress.AbsoluteAddress(cachedLink), store.readcacheBase.ClosedUntilAddress, "CONTROL: the planted link is below the closed page");
            return key;
        }

        private void AssertTheStoreStillWrites(int kfield1)
        {
            var other = new KeyStruct { kfield1 = kfield1, kfield2 = 3 };
            var value = new ValueStruct { vfield1 = 1, vfield2 = 2 };
            var write = Task.Run(() =>
            {
                using var writer = store.NewSession<KeyStruct, InputStruct, OutputStruct, Empty, Functions>(new Functions());
                _ = writer.BasicContext.Upsert(other, SpanByte.FromPinnedVariable(ref value), Empty.Default);
            });
            ClassicAssert.IsTrue(Ends(write) && write.Status == TaskStatus.RanToCompletion,
                "CONTROL: the store still serves other keys: the throw left no epoch or bucket lock held");
        }

        private void Cache(int kfield1)
        {
            var key = new KeyStruct { kfield1 = kfield1, kfield2 = 1 };
            var value = new ValueStruct { vfield1 = kfield1, vfield2 = 0 };
            _ = session.BasicContext.ConditionalInsertReadCache(key, SpanByte.FromPinnedVariable(ref value), store.Log.TailAddress);
        }

        private static bool Ends(Task task)
        {
            try
            {
                return task.Wait(Bound);
            }
            catch (AggregateException)
            {
                return true;
            }
        }

        private static ReadCacheChainLostException FindLost(Exception exception)
        {
            for (var e = exception; e is not null; e = e.InnerException)
            {
                if (e is ReadCacheChainLostException lost)
                    return lost;
                if (e is AggregateException aggregate)
                    foreach (var inner in aggregate.InnerExceptions)
                        if (FindLost(inner) is { } found)
                            return found;
            }
            return null;
        }
    }
}
