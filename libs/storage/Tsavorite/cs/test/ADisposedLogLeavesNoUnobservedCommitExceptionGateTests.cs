// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Garnet.test;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using Tsavorite.core;
using static Tsavorite.test.TestUtils;

namespace Tsavorite.test
{
    /// <summary>
    /// A log's dispose faults its pending commit task with "TsavoriteLog has been disposed". Nothing awaits that task when no commit
    /// waiter is parked, so the finalizer reported it as an unobserved task exception. Ziltch prod logged one at every roll from
    /// 0000272 through 0000300 (11-14..11-20), as the replica's read-only cold log was disposed at its writer take.
    /// The dispose now observes the exception it sets. A waiter that is parked still gets it.
    /// </summary>
    [TestFixture]
    internal class ADisposedLogLeavesNoUnobservedCommitExceptionGateTests : TestBase
    {
        [SetUp]
        public void Setup() => DeleteDirectory(MethodTestDir, wait: true);

        [TearDown]
        public void TearDown() => OnTearDown();

        [Test]
        [Category("TsavoriteLog")]
        public void A_disposed_log_leaves_no_unobserved_commit_exception()
        {
            var unobserved = new ConcurrentQueue<string>();
            void Note(object _, UnobservedTaskExceptionEventArgs e)
            {
                if (e.Exception.ToString().Contains("TsavoriteLog has been disposed")) unobserved.Enqueue(e.Exception.ToString());
            }
            TaskScheduler.UnobservedTaskException += Note;
            try
            {
                OpenWriteAndDispose(MethodTestDir);
                for (var i = 0; i < 5; i++)
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                }
                ClassicAssert.IsEmpty(unobserved, "a disposed log left its commit task's exception for the finalizer, which a host logs as a crash: "
                    + (unobserved.TryPeek(out var first) ? first : ""));
            }
            finally { TaskScheduler.UnobservedTaskException -= Note; }
        }

        [Test]
        [Category("TsavoriteLog")]
        public void A_commit_waiter_parked_at_the_dispose_still_gets_the_disposed_exception()
        {
            using var device = Devices.CreateLogDevice(Path.Join(MethodTestDir, "waiter.log"), deleteOnClose: true);
            var log = new TsavoriteLog(new TsavoriteLogSettings { LogDevice = device, PageSizeBits = 12, MemorySizeBits = 14, SegmentSizeBits = 22, LogCommitDir = MethodTestDir });
            var waiter = log.CommitTask;
            log.Dispose();
            var thrown = Assert.ThrowsAsync<ObjectDisposedException>(async () => await waiter);
            ClassicAssert.IsTrue(thrown.Message.Contains("TsavoriteLog has been disposed"), thrown.Message);
        }

        // its own frame, so nothing on the test's stack roots the log or its commit task when the finalizers run
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void OpenWriteAndDispose(string dir)
        {
            using var device = Devices.CreateLogDevice(Path.Join(dir, "disposed.log"), deleteOnClose: true);
            var log = new TsavoriteLog(new TsavoriteLogSettings { LogDevice = device, PageSizeBits = 12, MemorySizeBits = 14, SegmentSizeBits = 22, LogCommitDir = dir });
            _ = log.Enqueue(new byte[100]);
            log.Commit(spinWait: true);
            log.Dispose();
        }
    }
}
