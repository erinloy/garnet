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
    /// A PENDING READ THE DEVICE WILL NOT SERVE FAILS; IT DOES NOT RE-ISSUE FOREVER.
    ///
    /// <para>Measured on a real store (2026-09-13, a copy of a production cell's log whose segment files end short of the
    /// segment size): a point read whose hash chain entered the missing tail issued ~300,000 device reads in 12 s, every one
    /// answered ERROR_HANDLE_EOF with zero bytes, and never returned. AsyncGetFromDiskCallback logged the error code and
    /// handed the empty buffer to the incomplete-record path, which re-read the same address, and the calling thread stayed
    /// parked in CompletePending at full CPU.</para>
    ///
    /// <para>Two arms, one per way a device can refuse: an ERROR CODE fails the read on the first completion; a SHORT READ
    /// WITH NO ERROR fails after <see cref="LogSettings.PendingReadNoProgressLimit"/> reads of the same address that made no
    /// progress. Every arm runs the read under a deadline, so the old behaviour is a red test and not a hung run, and the
    /// control proves the harness really issues a pending disk read that completes when the device serves it.</para>
    /// </summary>
    [TestFixture]
    internal class APendingReadTheDeviceRefusesFailsTests : TestBase
    {
        private const int Records = 700;
        private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);

        private enum Refusal { None, ErrorCode, EmptyNoError }

        /// <summary>A device that serves writes and, once armed, answers every read as <see cref="Refusal"/> says, counting them.</summary>
        private sealed class RefusingDevice : StorageDeviceBase
        {
            private readonly IDevice underlying;
            public volatile Refusal Mode;
            public int ArmedReads;

            public RefusingDevice(IDevice underlying) : base(underlying.FileName, underlying.SectorSize, underlying.Capacity)
                => this.underlying = underlying;

            public override void Initialize(long segmentSize, LightEpoch epoch = null, bool omitSegmentIdFromFilename = false)
            {
                base.Initialize(segmentSize, epoch, omitSegmentIdFromFilename);
                underlying.Initialize(segmentSize, epoch, omitSegmentIdFromFilename);
            }

            public override void RemoveSegmentAsync(int segment, AsyncCallback callback, IAsyncResult result)
                => underlying.RemoveSegmentAsync(segment, callback, result);

            public override void WriteAsync(IntPtr sourceAddress, int segmentId, ulong destinationAddress, uint numBytesToWrite, DeviceIOCompletionCallback callback, object context)
                => underlying.WriteAsync(sourceAddress, segmentId, destinationAddress, numBytesToWrite, callback, context);

            public override void ReadAsync(int segmentId, ulong sourceAddress, IntPtr destinationAddress, uint readLength, DeviceIOCompletionCallback callback, object context)
            {
                switch (Mode)
                {
                    case Refusal.ErrorCode:
                        _ = Interlocked.Increment(ref ArmedReads);
                        callback(38, 0, context);   // ERROR_HANDLE_EOF, the code measured
                        return;
                    case Refusal.EmptyNoError:
                        _ = Interlocked.Increment(ref ArmedReads);
                        callback(0, 0, context);    // "success", nothing transferred
                        return;
                    default:
                        if (Mode == Refusal.None && armedForCount) _ = Interlocked.Increment(ref ArmedReads);
                        underlying.ReadAsync(segmentId, sourceAddress, destinationAddress, readLength, callback, context);
                        return;
                }
            }

            public volatile bool armedForCount;

            public override void Dispose() => underlying.Dispose();
        }

        private (TsavoriteKV<StructStoreFunctions, StructAllocator> store, RefusingDevice device) Build(int noProgressLimit)
        {
            DeleteDirectory(MethodTestDir);
            var device = new RefusingDevice(Devices.CreateLogDevice(Path.Join(MethodTestDir, "refusing.log"), deleteOnClose: true));
            var store = new TsavoriteKV<StructStoreFunctions, StructAllocator>(
                new()
                {
                    IndexSize = 1L << 26,
                    LogDevice = device,
                    LogMemorySize = 1L << 15,
                    PageSize = MinKvLogPageSize,
                    PendingReadNoProgressLimit = noProgressLimit,
                }, StoreFunctions.Create(KeyStruct.Comparer.Instance, SpanByteRecordTriggers.Instance),
                (allocatorSettings, storeFunctions) => new(allocatorSettings, storeFunctions));
            return (store, device);
        }

        /// <summary>Writes enough records that the first ones leave memory, arms the device, and reads record 0 (which must go
        /// pending) under a deadline. Returns the exception CompletePending raised, or null if it completed.</summary>
        private static Exception ReadEvictedRecord(TsavoriteKV<StructStoreFunctions, StructAllocator> store, RefusingDevice device, Refusal refusal)
        {
            var session = store.NewSession<KeyStruct, InputStruct, OutputStruct, Empty, Functions>(new Functions());
            var bContext = session.BasicContext;
            for (var i = 0; i < Records; i++)
            {
                var key = new KeyStruct { kfield1 = i, kfield2 = i + 1 };
                var value = new ValueStruct { vfield1 = i, vfield2 = i + 1 };
                _ = bContext.Upsert(key, SpanByte.FromPinnedVariable(ref value), Empty.Default);
            }
            _ = bContext.CompletePending(true);

            device.Mode = refusal;
            device.armedForCount = true;
            InputStruct input = default;
            OutputStruct output = default;
            var first = new KeyStruct { kfield1 = 0, kfield2 = 1 };
            var status = bContext.Read(first, ref input, ref output, Empty.Default);
            ClassicAssert.IsTrue(status.IsPending, "the control: record 0 must have left memory, or no device read is under test");

            Exception thrown = null;
            var completion = Task.Factory.StartNew(() =>
            {
                try { _ = bContext.CompletePending(true); }
                catch (Exception ex) { thrown = ex; }
            }, TaskCreationOptions.LongRunning);
            if (!completion.Wait(Deadline))
            {
                // THE OLD BEHAVIOUR, REPORTED AS A FAILURE RATHER THAN A HUNG RUN. The read is still spinning on its thread, so
                // neither the session nor the store can be disposed (both wait for it); they are deliberately abandoned, and
                // the spinning thread is a background thread the test host does not wait for.
                abandoned = true;
                Assert.Fail($"CompletePending did not return within {Deadline.TotalSeconds:F0} s under {refusal} after {device.ArmedReads} " +
                            "device read(s): the pending read is re-issuing instead of failing");
            }
            device.Mode = Refusal.None;
            session.Dispose();
            return thrown;
        }

        [ThreadStatic] private static bool abandoned;

        private void Release(TsavoriteKV<StructStoreFunctions, StructAllocator> store, RefusingDevice device)
        {
            if (abandoned) { abandoned = false; return; }   // a spinning read still holds them; see ReadEvictedRecord
            store.Dispose();
            device.Dispose();
            DeleteDirectory(MethodTestDir);
        }

        [Test]
        [Category("TsavoriteKV")]
        public void The_control_a_served_pending_read_completes()
        {
            var (store, device) = Build(LogSettings.DefaultPendingReadNoProgressLimit);
            try
            {
                var thrown = ReadEvictedRecord(store, device, Refusal.None);
                ClassicAssert.IsNull(thrown, $"a device that serves the read must not fail it: {thrown}");
                ClassicAssert.Greater(device.ArmedReads, 0, "the read went to the device, so the refusal arms below are refusing a real disk read");
            }
            finally { Release(store, device); }
        }

        [Test]
        [Category("TsavoriteKV")]
        public void A_device_error_code_fails_the_read_on_its_first_completion()
        {
            var (store, device) = Build(LogSettings.DefaultPendingReadNoProgressLimit);
            try
            {
                var thrown = ReadEvictedRecord(store, device, Refusal.ErrorCode);
                ClassicAssert.IsInstanceOf<TsavoriteException>(thrown, $"the read must fail with a TsavoriteException, got {thrown?.GetType().Name ?? "no exception"}");
                StringAssert.Contains("error code 38", thrown.Message);
                ClassicAssert.AreEqual(1, device.ArmedReads, "a refused address is not asked for again: exactly one device read");
            }
            finally { Release(store, device); }
        }

        [Test]
        [Category("TsavoriteKV")]
        public void An_empty_read_with_no_error_fails_after_the_no_progress_limit([Values(LogSettings.DefaultPendingReadNoProgressLimit, 5)] int limit)
        {
            var (store, device) = Build(limit);
            try
            {
                var thrown = ReadEvictedRecord(store, device, Refusal.EmptyNoError);
                ClassicAssert.IsInstanceOf<TsavoriteException>(thrown, $"the read must fail with a TsavoriteException, got {thrown?.GetType().Name ?? "no exception"}");
                StringAssert.Contains("made no progress", thrown.Message);
                ClassicAssert.AreEqual(limit, device.ArmedReads, $"the read fails at exactly {nameof(LogSettings.PendingReadNoProgressLimit)} = {limit} reads of one address, not before and not never");
            }
            finally { Release(store, device); }
        }
    }
}
