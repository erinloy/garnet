// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.IO;
using System.Threading;
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
    /// A FAILED LOG PAGE FLUSH IS RETRIED, AND A CHECKPOINT NEVER WAITS FOREVER ON ONE.
    ///
    /// <para>Before this, AsyncFlushPageCallback put a failed range in the error list and nothing on the KV path ever wrote it
    /// again: FlushedUntilAddress froze below it, the checkpoint's WAIT_FLUSH never completed, and a host awaiting the checkpoint
    /// under its checkpoint lock hung quietly. That became reachable on RG's cluster cells the moment its tiered device stopped
    /// reporting a failed local write as a success. Three arms: a device that serves every write (the control), one that fails a
    /// page write ONCE (the checkpoint completes after the retry), and one that fails EVERY write (the checkpoint FAILS with a
    /// <see cref="TsavoriteFlushFaultException"/> within the retry budget, and completes once the device serves writes again).
    /// Every checkpoint runs under a deadline, so the old behaviour is a red arm, never a hung run.</para>
    /// </summary>
    [TestFixture]
    internal class AFailedPageFlushIsRetriedAndACheckpointNeverHangsTests : TestBase
    {
        private const int Records = 400;
        private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

        /// <summary>A device whose log page writes fail while <see cref="FailWrites"/> is positive (decremented per failure)
        /// or negative (every write fails).</summary>
        private sealed class FailingWriteDevice : StorageDeviceBase
        {
            private readonly IDevice underlying;
            public int FailWrites;
            public int FailedWrites;

            public FailingWriteDevice(IDevice underlying) : base(underlying.FileName, underlying.SectorSize, underlying.Capacity)
                => this.underlying = underlying;

            public override void Initialize(long segmentSize, LightEpoch epoch = null, bool omitSegmentIdFromFilename = false)
            {
                base.Initialize(segmentSize, epoch, omitSegmentIdFromFilename);
                underlying.Initialize(segmentSize, epoch, omitSegmentIdFromFilename);
            }

            public override void RemoveSegmentAsync(int segment, AsyncCallback callback, IAsyncResult result)
                => underlying.RemoveSegmentAsync(segment, callback, result);

            public override void WriteAsync(IntPtr sourceAddress, int segmentId, ulong destinationAddress, uint numBytesToWrite, DeviceIOCompletionCallback callback, object context)
            {
                var remaining = Volatile.Read(ref FailWrites);
                if (remaining < 0 || (remaining > 0 && Interlocked.CompareExchange(ref FailWrites, remaining - 1, remaining) == remaining))
                {
                    _ = Interlocked.Increment(ref FailedWrites);
                    callback(112, 0, context);   // ERROR_DISK_FULL
                    return;
                }
                underlying.WriteAsync(sourceAddress, segmentId, destinationAddress, numBytesToWrite, callback, context);
            }

            public override void ReadAsync(int segmentId, ulong sourceAddress, IntPtr destinationAddress, uint readLength, DeviceIOCompletionCallback callback, object context)
                => underlying.ReadAsync(segmentId, sourceAddress, destinationAddress, readLength, callback, context);

            public override void Dispose() => underlying.Dispose();
        }

        private int flushErrors;

        private (TsavoriteKV<StructStoreFunctions, StructAllocator> store, FailingWriteDevice device) Build()
        {
            DeleteDirectory(MethodTestDir);
            _ = Directory.CreateDirectory(MethodTestDir);
            flushErrors = 0;
            var device = new FailingWriteDevice(Devices.CreateLogDevice(Path.Join(MethodTestDir, "flush.log"), deleteOnClose: true));
            var store = new TsavoriteKV<StructStoreFunctions, StructAllocator>(
                new()
                {
                    IndexSize = 1L << 20,
                    LogDevice = device,
                    LogMemorySize = 1L << 15,
                    PageSize = MinKvLogPageSize,
                    CheckpointDir = MethodTestDir,
                    FlushRetryLimit = 2,
                    FlushErrorCallback = (_, _) => Interlocked.Increment(ref flushErrors),
                }, StoreFunctions.Create(KeyStruct.Comparer.Instance, SpanByteRecordTriggers.Instance),
                (allocatorSettings, storeFunctions) => new(allocatorSettings, storeFunctions));
            return (store, device);
        }

        private static void Write(TsavoriteKV<StructStoreFunctions, StructAllocator> store, int start, int count = Records)
        {
            using var session = store.NewSession<KeyStruct, InputStruct, OutputStruct, Empty, Functions>(new Functions());
            var bContext = session.BasicContext;
            for (var i = start; i < start + count; i++)
            {
                var key = new KeyStruct { kfield1 = i, kfield2 = i + 1 };
                var value = new ValueStruct { vfield1 = i, vfield2 = i + 1 };
                _ = bContext.Upsert(key, SpanByte.FromPinnedVariable(ref value), Empty.Default);
            }
            _ = bContext.CompletePending(true);
        }

        /// <summary>Runs a FoldOver checkpoint under the deadline: (completed, success, exception).</summary>
        private static (bool Completed, bool Success, Exception Thrown) Checkpoint(TsavoriteKV<StructStoreFunctions, StructAllocator> store)
        {
            var task = store.TakeHybridLogCheckpointAsync(CheckpointType.FoldOver).AsTask();
            try
            {
                if (!task.Wait(Deadline))
                    return (false, false, null);
                return (true, task.Result.success, null);
            }
            catch (AggregateException ex)
            {
                return (true, false, ex.InnerException);
            }
        }

        private static void Release(TsavoriteKV<StructStoreFunctions, StructAllocator> store, FailingWriteDevice device, bool completed)
        {
            if (!completed)
                return;   // a checkpoint is still waiting on the store; disposing would wait with it
            store.Dispose();
            device.Dispose();
            DeleteDirectory(MethodTestDir);
        }

        [Test]
        [Category("TsavoriteKV")]
        public void The_control_a_device_that_serves_every_write_checkpoints()
        {
            var (store, device) = Build();
            var (completed, success, thrown) = (false, false, (Exception)null);
            try
            {
                Write(store, 0);
                (completed, success, thrown) = Checkpoint(store);
                ClassicAssert.IsTrue(completed, $"the control checkpoint did not finish within {Deadline.TotalSeconds:F0} s");
                ClassicAssert.IsNull(thrown, $"the control checkpoint threw: {thrown}");
                ClassicAssert.IsTrue(success);
                ClassicAssert.AreEqual(0, flushErrors, "the control saw no flush error");
            }
            finally { Release(store, device, completed); }
        }

        [Test]
        [Category("TsavoriteKV")]
        public void A_page_write_that_fails_ONCE_is_retried_and_the_checkpoint_completes()
        {
            var (store, device) = Build();
            var completed = false;
            try
            {
                Write(store, 0);
                device.FailWrites = 1;
                Write(store, Records, count: 8);   // fits the in-memory region: the CHECKPOINT's flush is the write that fails
                (completed, var success, var thrown) = Checkpoint(store);

                ClassicAssert.IsTrue(completed,
                    $"the checkpoint did not finish within {Deadline.TotalSeconds:F0} s after {device.FailedWrites} failed page write(s): the failed page was never re-issued");
                ClassicAssert.IsNull(thrown, $"one failed write inside the retry budget must not fail the checkpoint: {thrown}");
                ClassicAssert.IsTrue(success);
                ClassicAssert.AreEqual(1, device.FailedWrites, "the control: exactly one page write failed");
                ClassicAssert.GreaterOrEqual(flushErrors, 1, "the host's FlushErrorCallback heard the failure");
                ClassicAssert.IsNull(store.hlogBase.FlushFault, "no fault stands once the retry landed");
            }
            finally { Release(store, device, completed); }
        }

        [Test]
        [Category("TsavoriteKV")]
        public void A_page_write_that_KEEPS_failing_fails_the_checkpoint_within_the_budget_and_it_completes_once_the_device_serves_again()
        {
            var (store, device) = Build();
            var completed = false;
            try
            {
                Write(store, 0);
                device.FailWrites = -1;
                // FEW records, not a page's worth: against a device refusing EVERY write no page can be evicted, so an
                // Upsert that needs a new page blocks until a flush lands - the write path's own bound, separate from the
                // checkpoint's, and not what this arm measures. These fit the in-memory region; the checkpoint must flush them.
                Write(store, Records, count: 8);
                (completed, _, var thrown) = Checkpoint(store);

                ClassicAssert.IsTrue(completed,
                    $"the checkpoint did not finish within {Deadline.TotalSeconds:F0} s against a device refusing every write: it is waiting on a flush that cannot land");
                ClassicAssert.IsInstanceOf<TsavoriteFlushFaultException>(thrown, $"the checkpoint must fail with the flush fault, got {thrown?.GetType().Name ?? "no exception"}");
                var fault = (TsavoriteFlushFaultException)thrown;
                ClassicAssert.AreEqual(112u, fault.ErrorCode);
                ClassicAssert.GreaterOrEqual(fault.Attempts, 2, "raised at FlushRetryLimit = 2, not before");
                ClassicAssert.IsNotNull(store.hlogBase.FlushFault, "the fault stands while the device refuses");

                // The device recovers: the background retry lands, the fault clears, and a checkpoint completes.
                device.FailWrites = 0;
                var healed = SpinWait.SpinUntil(() => store.hlogBase.FlushFault is null, Deadline);
                ClassicAssert.IsTrue(healed, "the background retry did not land within the deadline once the device served writes");
                (completed, var success, var again) = Checkpoint(store);
                ClassicAssert.IsTrue(completed, "the checkpoint after recovery did not finish within the deadline");
                ClassicAssert.IsNull(again, $"the checkpoint after recovery threw: {again}");
                ClassicAssert.IsTrue(success);
            }
            finally { Release(store, device, completed); }
        }
    }
}
