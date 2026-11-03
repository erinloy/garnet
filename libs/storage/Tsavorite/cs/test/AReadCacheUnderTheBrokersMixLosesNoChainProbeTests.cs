// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.IO;
using System.Runtime.InteropServices;
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
    using Ctx = BasicContext<KeyStruct, InputStruct, OutputStruct, Empty, Functions, StoreFunctions<KeyStruct.Comparer, SpanByteRecordTriggers>, SpanByteAllocator<StoreFunctions<KeyStruct.Comparer, SpanByteRecordTriggers>>>;

    /// <summary>
    /// CURE GATE, run by name (Ziltch mesh_broker 11-02, rg ruling: reproduce before any re-arm). Before cures A and B it lost links in 5 of 6 120 s runs;
    /// with them 6 of 6 360 s runs at a 128 KiB read cache met no lost link, no unwritten start and no failed checkpoint. With the read cache on, the broker lost a chain 16-25 min into
    /// three lives: a read walk or the index checkpoint met a read-cache link below ClosedUntilAddress (07:18Z checkpoint, 09:11Z read). Neither
    /// the stride walk (91934fbeb, overshoots 0) nor the plain RecordInfo writes (57c679873, CAS retries 0) was the cause. The earlier probes ran
    /// reads, promotions, upserts and checkpoints; this one adds what the broker's cold tier does and they lacked: cold answers offered to the read
    /// cache (ConditionalInsertReadCache, found and tombstone, for keys with no record and keys cut below begin), deletes, the log's begin moved
    /// up under traffic, and the live scrub of index entries below begin. It counts every lost link a walk meets and names the first.
    /// </summary>
    [TestFixture]
    internal class AReadCacheUnderTheBrokersMixLosesNoChainProbeTests : TestBase
    {
        private const int Keys = 4_000, Hot = 200, Readers = 4, Absent = 2_000;

        private TsavoriteKV<StructStoreFunctions, StructAllocator> Open(IDevice device, int readCacheBits)
            => new(new KVSettings { IndexSize = 1L << 12, LogDevice = device, PageSize = 1L << 12, LogMemorySize = 1L << 16, SegmentSize = 1L << 22,
                                    CheckpointDir = MethodTestDir, ReadCacheEnabled = true, ReadCacheMemorySize = 1L << readCacheBits, ReadCachePageSize = 1L << 12,
                                    ReadCacheSecondChanceFraction = 0.5 },
                StoreFunctions.Create(KeyStruct.Comparer.Instance, SpanByteRecordTriggers.Instance),
                (allocatorSettings, storeFunctions) => new(allocatorSettings, storeFunctions));

        private static KeyStruct Key(int i) => new() { kfield1 = i, kfield2 = i + 1 };

        private static void Upsert(Ctx bContext, int i, long value)
        {
            var v = new ValueStruct { vfield1 = value, vfield2 = value + 1 };
            _ = bContext.Upsert(Key(i), SpanByte.FromPinnedVariable(ref v), Empty.Default);
        }

        private static void Read(Ctx bContext, int i)
        {
            InputStruct input = default;
            OutputStruct output = default;
            var status = bContext.Read(Key(i), ref input, ref output, Empty.Default);
            if (status.IsPending)
            {
                _ = bContext.CompletePendingWithOutputs(out var outputs, wait: true);
                outputs.Dispose();
            }
        }

        private long lost;
        private string firstLost;
        private long other;
        private string firstOther;

        private void Note(Exception ex, string who)
        {
            var chain = ex as ReadCacheChainLostException ?? ex.InnerException as ReadCacheChainLostException ?? (ex as AggregateException)?.InnerException as ReadCacheChainLostException;
            if (chain is null)
            {
                _ = Interlocked.Increment(ref other);
                _ = Interlocked.CompareExchange(ref firstOther, $"{who}: {ex.GetType().Name}: {ex.Message} @ {(ex.StackTrace ?? "").Split('\n')[0].Trim()} | missed-so-far={lost} at {DateTime.UtcNow:HH:mm:ss.fff}", null);
                return;
            }
            _ = Interlocked.Increment(ref lost);
            _ = Interlocked.CompareExchange(ref firstLost, $"{who}: {chain.Walker} walk met {chain.Address:X} below closed {chain.ClosedUntilAddress:X}", null);
        }

        [SetUp]
        public void Setup() { DeleteDirectory(MethodTestDir); _ = Directory.CreateDirectory(MethodTestDir); }

        [TearDown]
        public void TearDown() => OnTearDown();

        [Test]
        [Explicit("soak: 6 x 360 s; the read-cache cure gate, run by name")]
        [Category("ReadCache")]
        [Repeat(6)]
        public async Task No_walk_meets_a_closed_read_cache_link([Values(17)] int readCacheBits, [Values(360)] int seconds)
        {
            long offers = 0, deletes = 0, shifts = 0, scrubbed = 0, checkpoints = 0, failedCheckpoints = 0;
            using var device = Devices.CreateLogDevice(Path.Join(MethodTestDir, "mix.log"));
            using var store = Open(device, readCacheBits);
            using (var s = store.NewSession<KeyStruct, InputStruct, OutputStruct, Empty, Functions>(new AReadCachePromotionUnderCheckpointsKeepsEveryChainTests.Quiet()))
            {
                for (var i = 0; i < Keys; i++) Upsert(s.BasicContext, i, 1);
                _ = s.BasicContext.CompletePending(true);
            }
            store.Log.FlushAndEvict(wait: true);

            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
            Task Session(Action<Ctx, Random> step, int seed) => Task.Run(() =>
            {
                var rnd = new Random(seed);
                using var s = store.NewSession<KeyStruct, InputStruct, OutputStruct, Empty, Functions>(new AReadCachePromotionUnderCheckpointsKeepsEveryChainTests.Quiet());
                while (!stop.IsCancellationRequested)
                {
                    try { step(s.BasicContext, rnd); }
                    catch (Exception ex) { Note(ex, "session " + seed); }
                }
                try { _ = s.BasicContext.CompletePending(true); } catch (Exception ex) { Note(ex, "session " + seed + " drain"); }
            });
            var tasks = new System.Collections.Generic.List<Task>();
            for (var r = 0; r < Readers; r++)
                tasks.Add(Session((c, rnd) => Read(c, rnd.Next(10) < 7 ? rnd.Next(Hot) : rnd.Next(Keys + Absent)), r));
            tasks.Add(Session((c, rnd) =>
            {
                var i = rnd.Next(10) < 5 ? rnd.Next(Hot) : rnd.Next(Keys);
                if (rnd.Next(10) == 0) { _ = c.Delete(Key(i)); Interlocked.Increment(ref deletes); }
                else Upsert(c, i, rnd.Next());
                if ((rnd.Next() & 63) == 0) _ = c.CompletePending(true);
            }, 99));
            tasks.Add(Session((c, rnd) =>
            {
                // a cold answer: the tail is read before the (cold) read that missed here, and the answer is offered against it
                var since = store.Log.TailAddress;
                var i = rnd.Next(Keys + Absent);
                var v = new ValueStruct { vfield1 = i, vfield2 = i + 1 };
                var landed = rnd.Next(3) == 0
                    ? c.ConditionalInsertReadCacheTombstone(Key(i), since)
                    : c.ConditionalInsertReadCache(Key(i), MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref v, 1)), since);
                if (landed) Interlocked.Increment(ref offers);
            }, 7));
            tasks.Add(Task.Run(async () =>
            {
                // the cold cut: begin moves up toward the read-only line, then the scrub clears index entries below it, in steps
                while (!stop.IsCancellationRequested)
                {
                    await Task.Delay(150);
                    try
                    {
                        var target = store.Log.BeginAddress + (store.Log.ReadOnlyAddress - store.Log.BeginAddress) / 4;
                        if (target > store.Log.BeginAddress)
                        {
                            store.Log.ShiftBeginAddress(target, snapToPageStart: true, truncateLog: true);
                            Interlocked.Increment(ref shifts);
                        }
                        for (long from = 0; ;)
                        {
                            var step = store.ScrubEntriesBelowBegin(from, 1 << 10);
                            scrubbed += step.Cleared;
                            if (step.Done || step.Refused) break;
                            from = step.Next;
                        }
                    }
                    catch (Exception ex) { Note(ex, "cut"); }
                }
            }));
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    var (ok, _) = checkpoints % 2 == 0 ? await store.TakeIndexCheckpointAsync() : await store.TakeFullCheckpointAsync(CheckpointType.FoldOver);
                    if (ok) checkpoints++;
                    else failedCheckpoints++;
                }
                catch (Exception ex) { failedCheckpoints++; Note(ex, "checkpoint"); }
            }
            await Task.WhenAll(tasks);

            var (promoted, _) = store.ReadCachePromotions;
            var (landedInserts, held, below, lostInserts, _) = store.ConditionalReadCacheInserts;
            TestContext.Progress.WriteLine($"rc=2^{readCacheBits} s={seconds}: lost-links={lost} missed={store.ReadCacheMissedEvictions} promoted={promoted} offers={offers} " +
                $"(landed {landedInserts} held {held} below {below} lost {lostInserts}) deletes={deletes} shifts={shifts} scrubbed={scrubbed} " +
                $"checkpoints={checkpoints} failed={failedCheckpoints} overshoots={store.ReadCacheStrideOvershoots} unwritten={store.ReadCacheUnwrittenStarts} zero-cas={store.ZeroCasRefusals} other={other} first: {firstLost} first-other: {firstOther}");
            ClassicAssert.Greater(promoted, 0, "CONTROL: hits were promoted");
            ClassicAssert.Greater(offers, 0, "CONTROL: cold answers landed in the read cache");
            ClassicAssert.Greater(shifts, 0, "CONTROL: begin moved under traffic");
            ClassicAssert.Greater(checkpoints, 2, "CONTROL: checkpoints ran");
            ClassicAssert.AreEqual(0, store.ReadCacheMissedEvictions, "no walk met a read-cache link below its closed page: " + firstLost);
        }
    }
}
