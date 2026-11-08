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
    /// Recovery issues a batch of page reads at once and waits on them in order. When one failed, it threw at once, while the batch's other
    /// reads were still in flight; the caller disposed the store, and the reads landed in freed pages (a native access violation after
    /// "AsyncReadPagesForRecoveryCallback error"). Recovery now waits for every read it issued before it throws.
    /// </summary>
    [TestFixture]
    internal class ARecoveryThatFailsAReadWaitsForItsOtherReadsTests : TestBase
    {
        private const int PageBits = 12, Pages = 64;
        private static readonly TimeSpan Hold = TimeSpan.FromMilliseconds(500);

        /// <summary>Once armed, fails the first read and holds every later one for <see cref="Hold"/>, counting reads not yet delivered.</summary>
        private sealed class FailFirstHoldRestDevice : StorageDeviceBase
        {
            private readonly IDevice underlying;
            private int seen, undelivered;
            public volatile bool Armed;
            public int Undelivered => Volatile.Read(ref undelivered);
            public int Seen => Volatile.Read(ref seen);

            public FailFirstHoldRestDevice(IDevice underlying) : base(underlying.FileName, underlying.SectorSize, underlying.Capacity)
                => this.underlying = underlying;

            public override void Initialize(long segmentSize, LightEpoch epoch = null, bool omitSegmentIdFromFilename = false)
            {
                base.Initialize(segmentSize, epoch, omitSegmentIdFromFilename);
                underlying.Initialize(segmentSize, epoch, omitSegmentIdFromFilename);
            }

            public override void RemoveSegmentAsync(int segment, AsyncCallback callback, IAsyncResult result) => underlying.RemoveSegmentAsync(segment, callback, result);

            public override void WriteAsync(IntPtr sourceAddress, int segmentId, ulong destinationAddress, uint numBytesToWrite, DeviceIOCompletionCallback callback, object context)
                => underlying.WriteAsync(sourceAddress, segmentId, destinationAddress, numBytesToWrite, callback, context);

            public override void ReadAsync(int segmentId, ulong sourceAddress, IntPtr destinationAddress, uint readLength, DeviceIOCompletionCallback callback, object context)
            {
                if (!Armed)
                {
                    underlying.ReadAsync(segmentId, sourceAddress, destinationAddress, readLength, callback, context);
                    return;
                }
                if (Interlocked.Increment(ref seen) == 1)
                {
                    callback(42, readLength, context);
                    return;
                }
                _ = Interlocked.Increment(ref undelivered);
                _ = Task.Run(async () =>
                {
                    await Task.Delay(Hold).ConfigureAwait(false);
                    underlying.ReadAsync(segmentId, sourceAddress, destinationAddress, readLength, (e, n, c) =>
                    {
                        _ = Interlocked.Decrement(ref undelivered);
                        callback(e, n, c);
                    }, context);
                });
            }

            public override void Dispose() => underlying.Dispose();
        }

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
        public async Task A_recovery_whose_first_read_fails_throws_only_after_every_read_it_issued_has_landed()
        {
            Guid indexToken, logToken;
            {
                using var device = Devices.CreateLogDevice(Path.Join(MethodTestDir, "drain.log"));
                using var store = Open(device);
                Upsert(store, 0, 100, 1);
                var (indexOk, index) = await store.TakeIndexCheckpointAsync();
                ClassicAssert.IsTrue(indexOk, "the index checkpoint completes");
                indexToken = index;
                Upsert(store, 100, 40 << (PageBits - 5), 2);   // ~40 pages past the index's start: one read batch
                store.Log.ShiftReadOnlyAddress(store.Log.TailAddress, wait: true);
                var (logOk, log) = await store.TakeHybridLogCheckpointAsync(CheckpointType.FoldOver);
                ClassicAssert.IsTrue(logOk, "the log checkpoint completes");
                logToken = log;
            }

            var device2 = new FailFirstHoldRestDevice(Devices.CreateLogDevice(Path.Join(MethodTestDir, "drain.log")));
            var store2 = Open(device2);
            device2.Armed = true;
            Exception thrown = null;
            var undeliveredAtThrow = -1;
            try { _ = await store2.RecoverAsync(indexToken, logToken); }
            catch (Exception e) { undeliveredAtThrow = device2.Undelivered; thrown = e; }

            // Let every held read land before the store is freed, so a mutant fails this gate rather than crashing the host.
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
            while (device2.Undelivered > 0 && DateTime.UtcNow < deadline) await Task.Delay(50);
            TestContext.Progress.WriteLine($"rcdrain: reads {device2.Seen}, undelivered at throw {undeliveredAtThrow}, thrown {thrown?.GetType().Name}: {thrown?.Message}");

            ClassicAssert.IsNotNull(thrown, "a recovery whose read fails throws");
            ClassicAssert.IsInstanceOf<TsavoriteException>(thrown, "the failed read surfaces as a TsavoriteException");
            ClassicAssert.Greater(device2.Seen, 1, "control: the failed read had others in its batch");
            ClassicAssert.AreEqual(0, undeliveredAtThrow, "MUTANT (recovery threw with its batch's other reads still in flight)");
            store2.Dispose();
            device2.Dispose();
        }
    }
}
