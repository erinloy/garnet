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
    /// AN INDEX CHECKPOINT TAKEN WHILE READS CHURN THE READ CACHE KEEPS EVERY KEY'S CHAIN (Ziltch broker 10-26).
    ///
    /// <para>Every key is written once and flushed, then readers fill, evict and promote the read cache while index checkpoints are
    /// taken. The last index checkpoint taken under the reads is recovered with a log checkpoint taken after them. No key was
    /// written after the index checkpoint, so replay cannot restore an entry the checkpoint lost: every key must read its value, and
    /// recovery must clear nothing. The promotion soak recovers only its quiet last checkpoint, so it never reads one taken under load.
    /// MEASURED, AND SAID PLAINLY: the base before garnet e50670b88 also passed this arm (4 of 4 recoveries). One recovered checkpoint
    /// rarely meets the walk into a closed page; that window is pinned by AnIndexCheckpointCopyNeverWalksAClosedReadCachePageTests. This
    /// arm holds the rewalk (~600-1,000 per run here) to losing nothing.</para>
    /// </summary>
    [TestFixture]
    internal class AnIndexCheckpointTakenUnderReadsKeepsEveryChainTests : TestBase
    {
        private const int Keys = 4_000, Hot = 200, Readers = 4;

        private TsavoriteKV<StructStoreFunctions, StructAllocator> Open(IDevice device, double secondChance)
            => new(new KVSettings { IndexSize = 1L << 12, LogDevice = device, PageSize = 1L << 12, LogMemorySize = 1L << 16, SegmentSize = 1L << 22,
                                    CheckpointDir = MethodTestDir, ReadCacheEnabled = true, ReadCacheMemorySize = 1L << 15, ReadCachePageSize = 1L << 12,
                                    ReadCacheSecondChanceFraction = secondChance },
                StoreFunctions.Create(KeyStruct.Comparer.Instance, SpanByteRecordTriggers.Instance),
                (allocatorSettings, storeFunctions) => new(allocatorSettings, storeFunctions));

        private static KeyStruct Key(int i) => new() { kfield1 = i, kfield2 = i + 1 };

        private static bool Found(BasicContext<KeyStruct, InputStruct, OutputStruct, Empty, Functions, StructStoreFunctions, StructAllocator> bContext, int i)
        {
            InputStruct input = default;
            OutputStruct output = default;
            var status = bContext.Read(Key(i), ref input, ref output, Empty.Default);
            if (status.IsPending)
            {
                _ = bContext.CompletePendingWithOutputs(out var outputs, wait: true);
                using (outputs)
                {
                    if (!outputs.Next()) return false;
                    (status, output) = (outputs.Current.Status, outputs.Current.Output);
                }
            }
            return status.Found && output.value.vfield1 == i;
        }

        [SetUp]
        public void Setup() { DeleteDirectory(MethodTestDir); _ = Directory.CreateDirectory(MethodTestDir); }

        [TearDown]
        public void TearDown() => OnTearDown();

        [Test]
        [Category("TsavoriteKV")]
        [Category("ReadCache")]
        [Category("CheckpointRestore")]
        public async Task Every_key_reads_after_recovering_an_index_checkpoint_taken_under_reads([Values(0.0, 0.5)] double secondChance)
        {
            Guid index = default, log;
            long indexCheckpoints = 0, rewalks;
            using (var device = Devices.CreateLogDevice(Path.Join(MethodTestDir, "reads.log")))
            using (var store = Open(device, secondChance))
            {
                using (var s = store.NewSession<KeyStruct, InputStruct, OutputStruct, Empty, Functions>(new Functions()))
                {
                    for (var i = 0; i < Keys; i++)
                    {
                        var v = new ValueStruct { vfield1 = i, vfield2 = i + 1 };
                        _ = s.BasicContext.Upsert(Key(i), SpanByte.FromPinnedVariable(ref v), Empty.Default);
                    }
                    _ = s.BasicContext.CompletePending(true);
                }
                store.Log.FlushAndEvict(wait: true);

                using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(4));
                var readers = new Task[Readers];
                for (var r = 0; r < Readers; r++)
                {
                    var seed = r;
                    readers[r] = Task.Run(() =>
                    {
                        var rnd = new Random(seed);
                        using var s = store.NewSession<KeyStruct, InputStruct, OutputStruct, Empty, Functions>(new AReadCachePromotionUnderCheckpointsKeepsEveryChainTests.Quiet());
                        while (!stop.IsCancellationRequested)
                            _ = Found(s.BasicContext, rnd.Next(10) < 7 ? rnd.Next(Hot) : rnd.Next(Keys));
                    });
                }
                while (!stop.IsCancellationRequested)
                {
                    var (ok, token) = await store.TakeIndexCheckpointAsync();
                    if (ok) { index = token; indexCheckpoints++; }
                }
                await Task.WhenAll(readers);

                bool done;
                (done, log) = await store.TakeHybridLogCheckpointAsync(CheckpointType.FoldOver);
                ClassicAssert.IsTrue(done, "the log checkpoint completes");
                rewalks = store.CheckpointClosedReadCacheRewalks;
            }
            TestContext.Progress.WriteLine($"secondChance={secondChance}: index checkpoints under reads={indexCheckpoints} rewalks={rewalks}");
            ClassicAssert.Greater(indexCheckpoints, 2, "CONTROL: index checkpoints ran during the reads");

            using (var device = Devices.CreateLogDevice(Path.Join(MethodTestDir, "reads.log")))
            using (var store = Open(device, secondChance))
            {
                _ = await store.RecoverAsync(index, log);
                var lost = 0;
                string first = null;
                using (var s = store.NewSession<KeyStruct, InputStruct, OutputStruct, Empty, Functions>(new AReadCachePromotionUnderCheckpointsKeepsEveryChainTests.Quiet()))
                    for (var i = 0; i < Keys; i++)
                        if (!Found(s.BasicContext, i)) { lost++; first ??= $"key {i}"; }
                TestContext.Progress.WriteLine($"  recovered: scrubbed={store.RecoveryScrubbedEntries} misaligned={store.RecoveryMisalignedEntries} lost={lost}");
                ClassicAssert.AreEqual(0, store.RecoveryScrubbedEntries, "the index checkpoint named no address past its log");
                ClassicAssert.AreEqual(0, store.RecoveryMisalignedEntries, "the index checkpoint named no address off the record alignment");
                ClassicAssert.AreEqual(0, lost, $"every key reads its value after recovery; first absent: {first}");
            }
        }
    }
}
