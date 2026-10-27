// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
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
    /// A store asked to lock its index at recovery holds the main table and every overflow page locked in physical memory before
    /// recovery's bucket pass, so a box under memory pressure cannot trim it into a hard fault per bucket (kh 10-27: a working-set
    /// minimum alone was trimmed to 0.1 GB). A lock the OS refuses fails the recovery by name, never a silently unlocked table.
    /// </summary>
    [TestFixture]
    internal class ARecoveredIndexIsLockedResidentWhenAskedTests : TestBase
    {
        private const int Keys = 20_000;   // ~20 per bucket of 1,024: most chains spill into overflow buckets

        private TsavoriteKV<StructStoreFunctions, StructAllocator> Open(IDevice device)
            => new(new() { IndexSize = 1L << 16, LogDevice = device, LogMemorySize = 1L << 22, PageSize = 1L << 16, CheckpointDir = MethodTestDir },
                StoreFunctions.Create(KeyStruct.Comparer.Instance, SpanByteRecordTriggers.Instance),
                (allocatorSettings, storeFunctions) => new(allocatorSettings, storeFunctions));

        private static KeyStruct Key(int i) => new() { kfield1 = i, kfield2 = i + 1 };

        private static long? Read(TsavoriteKV<StructStoreFunctions, StructAllocator> store, int i)
        {
            using var session = store.NewSession<KeyStruct, InputStruct, OutputStruct, Empty, Functions>(new Functions());
            var bContext = session.BasicContext;
            InputStruct input = default;
            OutputStruct output = default;
            var status = bContext.Read(Key(i), ref input, ref output, Empty.Default);
            if (status.IsPending)
            {
                _ = bContext.CompletePendingWithOutputs(out var outputs, wait: true);
                using (outputs)
                {
                    ClassicAssert.IsTrue(outputs.Next(), "a pending read completes");
                    (status, output) = (outputs.Current.Status, outputs.Current.Output);
                }
            }
            return status.Found ? output.value.vfield1 : null;
        }

        private async Task<Guid> WrittenAsync()
        {
            using var device = Devices.CreateLogDevice(Path.Join(MethodTestDir, "hlog.log"), deleteOnClose: false);
            using var store = Open(device);
            using (var session = store.NewSession<KeyStruct, InputStruct, OutputStruct, Empty, Functions>(new Functions()))
            {
                for (var i = 0; i < Keys; i++)
                {
                    var v = new ValueStruct { vfield1 = i, vfield2 = i + 1 };
                    _ = session.BasicContext.Upsert(Key(i), SpanByte.FromPinnedVariable(ref v), Empty.Default);
                }
                _ = session.BasicContext.CompletePending(true);
            }
            ClassicAssert.Greater(store.OverflowBucketCount, 0, "anti-vacuity: the index has overflow buckets to lock");
            var (ok, token) = await store.TakeFullCheckpointAsync(CheckpointType.FoldOver);
            ClassicAssert.IsTrue(ok, "the checkpoint completes");
            return token;
        }

        private static List<(nint At, nint Bytes)> IndexRanges(TsavoriteKV<StructStoreFunctions, StructAllocator> store)
        {
            var table = store.state[store.resizeInfo.version];
            List<(nint, nint)> ranges;
            unsafe { ranges = [((nint)table.tableAligned, (nint)(table.size * sizeof(HashBucket)))]; }
            store.overflowBucketsAllocator.ForEachPage((at, bytes) => ranges.Add((at, bytes)));
            return ranges;
        }

        [SetUp]
        public void Setup()
        {
            if (!OperatingSystem.IsWindows()) Assert.Ignore("the residency probe reads QueryWorkingSetEx");
            DeleteDirectory(MethodTestDir);
            _ = Directory.CreateDirectory(MethodTestDir);
        }

        [TearDown]
        public void TearDown() { WorkingSet(DefaultMin, DefaultMax); OnTearDown(); }

        [Test]
        [Category("TsavoriteKV")]
        [Category("CheckpointRestore")]
        public async Task Asked_the_table_and_every_overflow_page_are_locked_before_the_bucket_pass_and_every_key_reads()
        {
            var token = await WrittenAsync();
            WorkingSet((nint)(256L << 20), (nint)(1L << 30));
            using var device = Devices.CreateLogDevice(Path.Join(MethodTestDir, "hlog.log"), deleteOnClose: false);
            using var store = Open(device);
            store.LockIndexResidentAtRecovery = true;
            _ = await store.RecoverAsync(token, token);

            var ranges = IndexRanges(store);
            ClassicAssert.Greater(ranges.Count, 1, "anti-vacuity: the recovered index has overflow pages");
            long bytes = 0;
            foreach (var (at, length) in ranges)
            {
                bytes += length;
                ClassicAssert.IsTrue(Locked(at) && Locked(at + length - 1), $"the index range at 0x{at:X} ({length:N0} bytes) is locked at both ends");
            }
            ClassicAssert.AreEqual(bytes, store.LockedIndexBytes, "the store counts every byte it locked: the table and every overflow page");
            for (var i = 0; i < Keys; i += 97) ClassicAssert.AreEqual((long)i, Read(store, i), $"key {i} reads after a locked recovery");
        }

        [Test]
        [Category("TsavoriteKV")]
        [Category("CheckpointRestore")]
        public async Task Unasked_nothing_is_locked()
        {
            var token = await WrittenAsync();
            WorkingSet((nint)(256L << 20), (nint)(1L << 30));
            using var device = Devices.CreateLogDevice(Path.Join(MethodTestDir, "hlog.log"), deleteOnClose: false);
            using var store = Open(device);
            _ = await store.RecoverAsync(token, token);
            ClassicAssert.AreEqual(0L, store.LockedIndexBytes, "control: an unasked recovery locks nothing");
            ClassicAssert.IsFalse(Locked(IndexRanges(store)[0].At), "control: the table is not locked");
        }

        [Test]
        [Category("TsavoriteKV")]
        [Category("CheckpointRestore")]
        public async Task A_lock_the_OS_refuses_fails_the_recovery_by_name()
        {
            var token = await WrittenAsync();
            WorkingSet(DefaultMin, DefaultMax);   // a 4 MB overflow page is past what a default working-set minimum may lock
            using var device = Devices.CreateLogDevice(Path.Join(MethodTestDir, "hlog.log"), deleteOnClose: false);
            using var store = Open(device);
            store.LockIndexResidentAtRecovery = true;
            var refused = Assert.ThrowsAsync<TsavoriteException>(async () => await store.RecoverAsync(token, token));
            StringAssert.Contains("LockIndexResident", refused.Message, "the refusal names the lock, not a silent unlocked table");
            ClassicAssert.AreEqual(0L, store.LockedIndexBytes, "a refused lock leaves nothing counted as locked");
        }

        private static readonly nint DefaultMin = Process.GetCurrentProcess().MinWorkingSet, DefaultMax = Process.GetCurrentProcess().MaxWorkingSet;

        private static void WorkingSet(nint min, nint max)
        {
            using var self = Process.GetCurrentProcess();
            ClassicAssert.IsTrue(SetProcessWorkingSetSize(self.Handle, min, max), $"the test sets its working set to [{min:N0}, {max:N0}]");
        }

        private static bool Locked(nint address)
        {
            var info = new WorkingSetExInformation[] { new() { VirtualAddress = address } };
            using var self = Process.GetCurrentProcess();
            ClassicAssert.IsTrue(QueryWorkingSetEx(self.Handle, info, Marshal.SizeOf<WorkingSetExInformation>()), "QueryWorkingSetEx answers");
            return ((ulong)info[0].VirtualAttributes & 1UL) != 0 && ((ulong)info[0].VirtualAttributes >> 22 & 1UL) != 0;   // Valid, Locked
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WorkingSetExInformation { public nint VirtualAddress; public nuint VirtualAttributes; }

        [DllImport("psapi.dll", SetLastError = true)]
        private static extern bool QueryWorkingSetEx(nint process, [In, Out] WorkingSetExInformation[] info, int size);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetProcessWorkingSetSize(nint process, nint minimum, nint maximum);
    }
}
