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
    /// PROBE (Ziltch broker 10-26): the first index checkpoint taken under a build with the read cache's second chance (002e08dbb) held
    /// 1,914 entries past the log's final address and 344 off the record alignment; recovery zeroed each, and every key behind them read
    /// absent. Readers re-reading a hot set (promotions), an upserter detaching read-cache prefixes, and checkpoints taken throughout;
    /// then every key reads its last value live and after recovery, and recovery clears nothing. secondChance 0 is the arm without promotion.
    /// </summary>
    [TestFixture]
    internal class AReadCachePromotionUnderCheckpointsKeepsEveryChainTests : TestBase
    {
        private const int Keys = 4_000, Hot = 200, Readers = 4;

        /// <summary>The shared Functions asserts value == key at every pending completion; here values move, and the test judges them.</summary>
        internal sealed class Quiet : Functions
        {
            public override void ReadCompletionCallback(ref DiskLogRecord diskLogRecord, ref InputStruct input, ref OutputStruct output, Empty ctx, Status status, RecordMetadata recordMetadata) => LastCompletion = $"{status} found={status.Found} ok={status.IsCompletedSuccessfully} faulted={status.IsFaulted}";
        }

        private TsavoriteKV<StructStoreFunctions, StructAllocator> Open(IDevice device, double secondChance)
            => new(new KVSettings { IndexSize = 1L << 12, LogDevice = device, PageSize = 1L << 12, LogMemorySize = 1L << 16, SegmentSize = 1L << 22,
                                    CheckpointDir = MethodTestDir, ReadCacheEnabled = true, ReadCacheMemorySize = 1L << 15, ReadCachePageSize = 1L << 12,
                                    ReadCacheSecondChanceFraction = secondChance },
                StoreFunctions.Create(KeyStruct.Comparer.Instance, SpanByteRecordTriggers.Instance),
                (allocatorSettings, storeFunctions) => new(allocatorSettings, storeFunctions));

        private static KeyStruct Key(int i) => new() { kfield1 = i, kfield2 = i + 1 };

        private static void Upsert(BasicContext<KeyStruct, InputStruct, OutputStruct, Empty, Functions, StructStoreFunctions, StructAllocator> bContext, int i, long value)
        {
            var v = new ValueStruct { vfield1 = value, vfield2 = value + 1 };
            _ = bContext.Upsert(Key(i), SpanByte.FromPinnedVariable(ref v), Empty.Default);
        }

        [ThreadStatic] private static string LastAbsent;
        [ThreadStatic] private static string LastCompletion;

        private static long? Read(BasicContext<KeyStruct, InputStruct, OutputStruct, Empty, Functions, StructStoreFunctions, StructAllocator> bContext, int i)
        {
            InputStruct input = default;
            OutputStruct output = default;
            LastCompletion = "no callback";
            var status = bContext.Read(Key(i), ref input, ref output, Empty.Default);
            var wasPending = status.IsPending;
            if (status.IsPending)
            {
                _ = bContext.CompletePendingWithOutputs(out var outputs, wait: true);
                using (outputs)
                {
                    if (!outputs.Next()) { LastAbsent = "pending gave no output; its completion: " + LastCompletion; return null; }
                    (status, output) = (outputs.Current.Status, outputs.Current.Output);
                }
            }
            if (!status.Found) LastAbsent = $"status {status} pending={wasPending}";
            return status.Found ? output.value.vfield1 : null;
        }

        [SetUp]
        public void Setup() { DeleteDirectory(MethodTestDir); _ = Directory.CreateDirectory(MethodTestDir); }

        [TearDown]
        public void TearDown() => OnTearDown();

        [Test]
        [Category("TsavoriteKV")]
        [Category("ReadCache")]
        [Category("CheckpointRestore")]
        public async Task Every_key_reads_its_last_value_live_and_after_recovery([Values(0.0, 0.5)] double secondChance, [Values(4, 12)] int seconds)
        {
            var expected = new long[Keys];
            (Guid Index, Guid Log) tokens;
            long promoted, checkpoints = 0, wrong = 0, upserts = 0;
            string firstWrong = null;
            using (var device = Devices.CreateLogDevice(Path.Join(MethodTestDir, "promote.log")))
            using (var store = Open(device, secondChance))
            {
                using (var s = store.NewSession<KeyStruct, InputStruct, OutputStruct, Empty, Functions>(new Quiet()))
                {
                    for (var i = 0; i < Keys; i++) { Upsert(s.BasicContext, i, 1); expected[i] = 1; }
                    _ = s.BasicContext.CompletePending(true);
                }
                store.Log.FlushAndEvict(wait: true);

                using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
                var readers = new Task[Readers];
                for (var r = 0; r < Readers; r++)
                {
                    var seed = r;
                    readers[r] = Task.Run(() =>
                    {
                        var rnd = new Random(seed);
                        using var s = store.NewSession<KeyStruct, InputStruct, OutputStruct, Empty, Functions>(new Quiet());
                        while (!stop.IsCancellationRequested)
                        {
                            var i = rnd.Next(10) < 7 ? rnd.Next(Hot) : rnd.Next(Keys);
                            var floor = Volatile.Read(ref expected[i]);
                            var got = Read(s.BasicContext, i);
                            if (got is null || got < floor)
                            {
                                Interlocked.Increment(ref wrong);
                                Interlocked.CompareExchange(ref firstWrong, $"key {i} read {got?.ToString() ?? "absent (" + LastAbsent + ")"} under {floor} (live, mid-run)", null);
                            }
                        }
                    });
                }
                var writer = Task.Run(() =>
                {
                    var rnd = new Random(99);
                    using var s = store.NewSession<KeyStruct, InputStruct, OutputStruct, Empty, Functions>(new Quiet());
                    while (!stop.IsCancellationRequested)
                    {
                        var i = rnd.Next(10) < 5 ? rnd.Next(Hot) : rnd.Next(Keys);
                        var v = expected[i] + 1;
                        Upsert(s.BasicContext, i, v);
                        Volatile.Write(ref expected[i], v);
                        upserts++;
                        if ((upserts & 63) == 0) _ = s.BasicContext.CompletePending(true);
                    }
                    _ = s.BasicContext.CompletePending(true);
                });
                while (!stop.IsCancellationRequested)
                {
                    var (ok, _) = checkpoints % 2 == 0 ? await store.TakeIndexCheckpointAsync() : await store.TakeFullCheckpointAsync(CheckpointType.FoldOver);
                    if (ok) checkpoints++;
                }
                await Task.WhenAll(readers);
                await writer;

                var (done, token) = await store.TakeFullCheckpointAsync(CheckpointType.FoldOver);
                ClassicAssert.IsTrue(done, "the last checkpoint completes");
                tokens = (token, token);
                promoted = store.ReadCachePromotions.Promoted;

                using (var s = store.NewSession<KeyStruct, InputStruct, OutputStruct, Empty, Functions>(new Quiet()))
                    for (var i = 0; i < Keys; i++)
                        if (Read(s.BasicContext, i) is var got && got != expected[i])
                        {
                            wrong++;
                            firstWrong ??= $"key {i} read {got?.ToString() ?? "absent"}, last written {expected[i]} (live, after the run)";
                        }
            }
            TestContext.Progress.WriteLine($"secondChance={secondChance} s={seconds}: promoted={promoted} checkpoints={checkpoints} upserts={upserts} wrong-live={wrong}");
            if (secondChance > 0) ClassicAssert.Greater(promoted, 0, "CONTROL: hits were promoted, so the arm ran what it names");
            ClassicAssert.Greater(checkpoints, 2, "CONTROL: checkpoints ran during the reads");

            using (var device = Devices.CreateLogDevice(Path.Join(MethodTestDir, "promote.log")))
            using (var store = Open(device, secondChance))
            {
                _ = await store.RecoverAsync(tokens.Index, tokens.Log);
                var lost = 0;
                using (var s = store.NewSession<KeyStruct, InputStruct, OutputStruct, Empty, Functions>(new Quiet()))
                    for (var i = 0; i < Keys; i++)
                        if (Read(s.BasicContext, i) is var got && got != expected[i])
                        {
                            lost++;
                            firstWrong ??= $"key {i} read {got?.ToString() ?? "absent"}, last written {expected[i]} (recovered)";
                        }
                TestContext.Progress.WriteLine($"  recovered: scrubbed={store.RecoveryScrubbedEntries} misaligned={store.RecoveryMisalignedEntries} lost={lost}");
                ClassicAssert.AreEqual(0, store.RecoveryScrubbedEntries, "the checkpoint's index named no address past its log");
                ClassicAssert.AreEqual(0, store.RecoveryMisalignedEntries, "the checkpoint's index named no address off the record alignment");
                ClassicAssert.AreEqual(0, lost, "every key reads its last value after recovery: " + firstWrong);
            }
            ClassicAssert.AreEqual(0, wrong, "every read saw its key's value: " + firstWrong);
        }
    }
}
