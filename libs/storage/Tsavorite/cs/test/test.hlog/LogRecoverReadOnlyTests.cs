// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Garnet.test;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using Tsavorite.core;
using static Tsavorite.test.TestUtils;

namespace Tsavorite.test.recovery
{
    [TestFixture]
    public class LogRecoverReadOnlyTests : TestBase
    {
        const int ProducerPauseMs = 1;
        const int CommitPeriodMs = 20;
        const int RestorePeriodMs = 5;
        const int NumElements = 100;

        string deviceName;
        CancellationTokenSource cts;
        SemaphoreSlim done;

        [SetUp]
        public void Setup()
        {
            deviceName = Path.Join(TestUtils.MethodTestDir, "testlog");

            // Clean up log files from previous test runs in case they weren't cleaned up
            TestUtils.DeleteDirectory(TestUtils.MethodTestDir, wait: true);

            cts = new CancellationTokenSource();
            done = new SemaphoreSlim(0);
        }

        [TearDown]
        public void TearDown()
        {
            cts?.Dispose();
            cts = default;
            done?.Dispose();
            done = default;
            TestUtils.OnTearDown();
        }

        [Test]
        [Category("TsavoriteLog")]
        public async Task RecoverReadOnlyCheck1()
        {
            using var device = Devices.CreateLogDevice(deviceName);
            var logSettings = new TsavoriteLogSettings { LogDevice = device, MemorySizeBits = MinKvLogMemorySizeBits, PageSizeBits = MinKvLogPageSizeBits, MutableFraction = 0.5, SegmentSizeBits = MinKvLogPageSizeBits + 1, TryRecoverLatest = false };
            using var log = await TsavoriteLog.CreateAsync(logSettings).ConfigureAwait(false);

            await Task.WhenAll(ProducerAsync(log, cts),
                               CommitterAsync(log, cts.Token),
                               ReadOnlyConsumerAsync(deviceName, cts.Token)).ConfigureAwait(false);
        }

        // Ziltch local 11-13: a read-only follower's BeginAddress stayed at the allocator's first address after the writer truncated, so a scan
        // from it read segments the writer had deleted and failed. A follow takes the commit's begin, at open and on every later follow.
        [Test]
        [Category("TsavoriteLog")]
        public async Task AReadOnlyFollowTakesTheWritersTruncatedBegin()
        {
            using var device = Devices.CreateLogDevice(deviceName);
            var logSettings = new TsavoriteLogSettings { LogDevice = device, MemorySizeBits = MinKvLogMemorySizeBits, PageSizeBits = MinKvLogPageSizeBits, SegmentSizeBits = MinKvLogPageSizeBits + 1, TryRecoverLatest = false };
            using var log = await TsavoriteLog.CreateAsync(logSettings).ConfigureAwait(false);
            var entry = new byte[100];
            long Fill()
            {
                for (var i = 0; i < 200; i++) log.Enqueue(entry);
                return log.TailAddress;
            }

            var firstCut = Fill();
            await log.CommitAsync().ConfigureAwait(false);
            log.TruncateUntil(firstCut);
            _ = Fill();
            await log.CommitAsync().ConfigureAwait(false);

            using var roDevice = Devices.CreateLogDevice(deviceName);
            var roSettings = new TsavoriteLogSettings { LogDevice = roDevice, ReadOnlyMode = true, PageSizeBits = MinKvLogPageSizeBits, SegmentSizeBits = MinKvLogPageSizeBits + 1 };
            using var follower = await TsavoriteLog.CreateAsync(roSettings).ConfigureAwait(false);
            ClassicAssert.AreEqual(log.BeginAddress, follower.BeginAddress, "the follower's begin at open is the writer's committed begin");
            ClassicAssert.AreEqual(firstCut, follower.BeginAddress);

            var secondCut = Fill();
            await log.CommitAsync().ConfigureAwait(false);
            log.TruncateUntil(secondCut);
            _ = Fill();
            await log.CommitAsync().ConfigureAwait(false);
            await follower.RecoverReadOnlyAsync().ConfigureAwait(false);
            ClassicAssert.AreEqual(secondCut, follower.BeginAddress, "a later follow moves the begin to the writer's newer cut");
        }

        private async Task ProducerAsync(TsavoriteLog log, CancellationTokenSource cts)
        {
            for (var i = 0L; i < NumElements; ++i)
            {
                log.Enqueue(Encoding.UTF8.GetBytes(i.ToString()));
                await Task.Delay(TimeSpan.FromMilliseconds(ProducerPauseMs)).ConfigureAwait(false);
            }
            // Ensure the reader had time to see all data
            await done.WaitAsync().ConfigureAwait(false);
            cts.Cancel();
        }

        private static async Task CommitterAsync(TsavoriteLog log, CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(CommitPeriodMs), cancellationToken).ConfigureAwait(false);
                    await log.CommitAsync(token: cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
        }

        // This creates a separate TsavoriteLog over the same log file, using RecoverReadOnlyAsync to continuously update
        // to the primary TsavoriteLog's commits.
        private async Task ReadOnlyConsumerAsync(string deviceName, CancellationToken cancellationToken)
        {
            using var device = Devices.CreateLogDevice(deviceName);
            var logSettings = new TsavoriteLogSettings { LogDevice = device, ReadOnlyMode = true, PageSizeBits = MinKvLogPageSizeBits, SegmentSizeBits = MinKvLogPageSizeBits + 1 };
            using var log = await TsavoriteLog.CreateAsync(logSettings, cancellationToken).ConfigureAwait(false);

            var _ = BeginRecoverAsyncLoop();

            // This enumerator waits asynchronously when we have reached the committed tail of the duplicate TsavoriteLog. When RecoverReadOnlyAsync
            // reads new data committed by the primary TsavoriteLog, it signals commit completion to let iter continue to the new tail.
            using var iter = log.Scan(log.BeginAddress, long.MaxValue);
            var prevValue = -1L;
            try
            {
                await foreach (var (result, _, _, nextAddress) in iter.GetAsyncEnumerable(cancellationToken).ConfigureAwait(false))
                {
                    var value = long.Parse(Encoding.UTF8.GetString(result));
                    ClassicAssert.AreEqual(prevValue + 1, value);
                    prevValue = value;
                    if (prevValue == NumElements - 1)
                        done.Release();
                }
            }
            catch (OperationCanceledException) { }
            ClassicAssert.AreEqual(NumElements - 1, prevValue);

            async Task BeginRecoverAsyncLoop()
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    // Delay for a while before recovering to the last commit by the primary TsavoriteLog instance.
                    await Task.Delay(TimeSpan.FromMilliseconds(RestorePeriodMs), cancellationToken).ConfigureAwait(false);
                    if (cancellationToken.IsCancellationRequested)
                        break;
                    long startTime = DateTimeOffset.UtcNow.Ticks;
                    while (true)
                    {
                        try
                        {
                            await log.RecoverReadOnlyAsync(cancellationToken).ConfigureAwait(false);
                            break;
                        }
                        catch
                        { }
                        Thread.Yield();
                        // retry until timeout
                        if (DateTimeOffset.UtcNow.Ticks - startTime > TimeSpan.FromSeconds(5).Ticks)
                            throw new Exception("Timed out retrying RecoverReadOnlyAsync");
                    }
                }
            }
        }
    }
}