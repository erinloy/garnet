// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.Buffers;
using System.IO;
using System.Threading.Tasks;
using Garnet.test;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using Tsavorite.core;
using static Tsavorite.test.TestUtils;

namespace Tsavorite.test
{
    /// <summary>
    /// A log read passed its completion callback as an instance method group, and the Windows device passed its handle factory the same
    /// way, so every read made two delegates even when the segment's handle was already open: 422 MB in 30 s on one broker. Both are
    /// now made once, in their constructors, and a read's bytes up to its issue no longer carry them.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    [TestFixture]
    internal class ALogReadMakesNoDelegateToIssueItsDeviceReadTests : TestBase
    {
        private const int Entries = 4_000, Reads = 400, Rounds = 9;

        private IDevice device;
        private TsavoriteLog log;

        [SetUp]
        public void Setup()
        {
            DeleteDirectory(MethodTestDir, wait: true);
            device = Devices.CreateLogDevice(Path.Join(MethodTestDir, "read.log"), deleteOnClose: true);
            log = new TsavoriteLog(new TsavoriteLogSettings { LogDevice = device, PageSizeBits = 12, MemorySizeBits = 14, SegmentSizeBits = 22, LogCommitDir = MethodTestDir });
        }

        [TearDown]
        public void TearDown()
        {
            log?.Dispose(); log = null;
            device?.Dispose(); device = null;
            OnTearDown();
        }

        [Test]
        [Category("TsavoriteLog")]
        public async Task A_read_allocates_no_delegate_up_to_its_issue()
        {
            var addresses = new long[Entries];
            var entry = new byte[100];
            for (var i = 0; i < Entries; i++) { entry[0] = (byte)i; addresses[i] = log.Enqueue(entry); }
            log.Commit(spinWait: true);
            ClassicAssert.IsInstanceOf<LocalStorageDevice>(device, "CONTROL: the Windows device, whose handle factory this measures");

            var rounds = new long[Rounds];
            for (var r = -1; r < Rounds; r++)   // round -1 warms the JIT, the handles and the pooled builder
            {
                long issued = 0;
                for (var i = 0; i < Reads; i++)
                {
                    var at = addresses[(r + 1) * 97 + i * 7 % Entries];
                    var before = GC.GetAllocatedBytesForCurrentThread();
                    var read = log.ReadAsync(at, MemoryPool<byte>.Shared, 100);
                    issued += GC.GetAllocatedBytesForCurrentThread() - before;
                    var (record, length) = await read;
                    ClassicAssert.AreEqual(100, length, "CONTROL: the read comes back whole");
                    record.Dispose();
                }
                if (r >= 0) rounds[r] = issued / Reads;
            }
            Array.Sort(rounds);
            var perRead = rounds[Rounds / 2];
            TestContext.Progress.WriteLine($"a log read allocates {perRead} B up to its issue (median of {Rounds} rounds of {Reads})");
            ClassicAssert.Less(perRead, BoundBytes, $"a log read allocates {perRead} B up to its issue; two delegates (about 128 B) are the mutant");
        }

        private const long BoundBytes = 144;   // measured 80 B with both made once, 208 B with both made per read: either one left is red
    }
}
