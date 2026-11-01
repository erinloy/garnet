// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.Collections.Generic;
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
    /// A store whose settings say NativeHashIndex keeps its overflow buckets in direct-VM pages too (Ziltch mesh, 11-01: the table's own
    /// mapping gave its POH hole back, 2.32 GB -> 488 B, but overflow pages are 4 MB pinned arrays, and mesh's past 111M overflow buckets
    /// were 7 GB of them). An empty allocator maps nothing; a page a trim drops, or an allocator a halving or compaction retires, is
    /// unmapped, or parked while an overflow checkpoint write may read it and unmapped when the write drains. Mutant: the pages stay
    /// managed (every fact red).
    /// </summary>
    [TestFixture]
    [NonParallelizable]   // the page counters and the direct-VM byte count are process-wide
    internal class AnIndexsOverflowPagesAreMappedWhenItsTableIsTests : TestBase
    {
        private const int Keys = 4000;
        private const long PageBytes = (1L << 16) * 64;

        private sealed class Quiet : Functions
        {
            public override void ReadCompletionCallback(ref DiskLogRecord diskLogRecord, ref InputStruct input, ref OutputStruct output, Empty ctx, Status status, RecordMetadata recordMetadata) { }
        }

        // holds every write until told, as a slow device holds an overflow checkpoint's
        private sealed class HeldDevice : StorageDeviceBase
        {
            public readonly List<(DeviceIOCompletionCallback Callback, uint Bytes, object Context)> Held = [];
            public HeldDevice() : base("held", IDevice.MinDeviceSectorSize, Devices.CAPACITY_UNSPECIFIED) { }
            public override void ReadAsync(int segmentId, ulong alignedSourceAddress, IntPtr alignedDestinationAddress, uint length, DeviceIOCompletionCallback callback, object context) => callback(0, length, context);
            public override void WriteAsync(IntPtr alignedSourceAddress, int segmentId, ulong alignedDestinationAddress, uint numBytesToWrite, DeviceIOCompletionCallback callback, object context)
            {
                lock (Held) Held.Add((callback, numBytesToWrite, context));
            }
            public void Release()
            {
                List<(DeviceIOCompletionCallback Callback, uint Bytes, object Context)> held;
                lock (Held) { held = [.. Held]; Held.Clear(); }
                foreach (var (callback, bytes, context) in held) callback(0, bytes, context);
            }
            public override void RemoveSegment(int segment) { }
            public override void RemoveSegmentAsync(int segment, AsyncCallback callback, IAsyncResult result) => callback(result);
            public override void Dispose() { }
        }

        private readonly List<IDisposable> owned = [];

        [SetUp]
        public void Setup()
        {
            DeleteDirectory(MethodTestDir, wait: true);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            ClassicAssert.IsFalse(NativeAllocatorInitializer.Enabled, "CONTROL: the process-wide native allocator is off");
        }

        [TearDown]
        public void TearDown()
        {
            for (var i = owned.Count - 1; i >= 0; i--) owned[i].Dispose();
            owned.Clear();
            OnTearDown();
        }

        private TsavoriteKV<StructStoreFunctions, StructAllocator> Open(long indexBytes, bool native, string name, bool keep = false)
        {
            var log = Devices.CreateLogDevice(Path.Join(MethodTestDir, name + ".log"), deleteOnClose: !keep);
            owned.Add(log);
            var store = new TsavoriteKV<StructStoreFunctions, StructAllocator>(new KVSettings
            {
                IndexSize = indexBytes, NativeHashIndex = native, LogDevice = log, CheckpointDir = Path.Join(MethodTestDir, name + ".cp"), PageSize = 1L << 12, LogMemorySize = 1L << 20, SegmentSize = 1L << 22
            }, StoreFunctions.Create(KeyStruct.Comparer.Instance, SpanByteRecordTriggers.Instance), (allocatorSettings, storeFunctions) => new(allocatorSettings, storeFunctions));
            owned.Add(store);
            return store;
        }

        private static KeyStruct Key(int i) => new() { kfield1 = i, kfield2 = i };

        private static (bool Found, long Value) Read(BasicContext<KeyStruct, InputStruct, OutputStruct, Empty, Quiet, StructStoreFunctions, StructAllocator> context, int i)
        {
            InputStruct input = default;
            OutputStruct output = default;
            var status = context.Read(Key(i), ref input, ref output);
            if (status.IsPending)
            {
                _ = context.CompletePendingWithOutputs(out var outputs, wait: true);
                (status, output) = GetSinglePendingResult(outputs);
            }
            return (status.Found, status.Found ? output.value.vfield2 : -1);
        }

        private static void Write(BasicContext<KeyStruct, InputStruct, OutputStruct, Empty, Quiet, StructStoreFunctions, StructAllocator> context, int i)
        {
            var value = new ValueStruct { vfield1 = i, vfield2 = i * 10 };
            _ = context.Upsert(Key(i), SpanByte.FromPinnedVariable(ref value), Empty.Default);
        }

        // Keys whose tag no other bucket brings to their cell in the halved table: a halving with no collision.
        private static List<int> KeysWithoutCollisions(long size)
        {
            var half = size / 2;
            var cells = new Dictionary<(long, ushort), long>();
            var keys = new List<int>();
            for (var i = 0; keys.Count < Keys; i++)
            {
                var hash = Utility.GetHashCode((long)i);
                var (bucket, tag) = (hash & (size - 1), HashBucketEntry.GetTag(hash));
                if (cells.TryGetValue((bucket & (half - 1), tag), out var held) && held != bucket) continue;
                cells[(bucket & (half - 1), tag)] = bucket;
                keys.Add(i);
            }
            return keys;
        }

        private static long Freed => Interlocked.Read(ref MallocFixedPageSize<HashBucket>.NativePagesFreed);

        [Test]
        [Category("TsavoriteKV")]
        [Category("Index")]
        public void An_empty_store_whose_table_is_mapped_maps_no_overflow_page()
        {
            var managed = Open(1L << 20, native: false, "managed");
            ClassicAssert.AreEqual(2 * PageBytes, managed.IndexOverflowPageBytes, "CONTROL: a managed store pins two overflow pages at construction");

            var vm = NativeMemoryTracker.DirectVmBytes;
            var store = Open(1L << 20, native: true, "native");
            ClassicAssert.AreEqual(0, store.IndexOverflowPageBytes, "an empty store whose settings say native holds no overflow page");
            ClassicAssert.Less(NativeMemoryTracker.DirectVmBytes - vm, (1L << 20) + PageBytes, "the store maps its table and no overflow page");

            using var session = store.NewSession<KeyStruct, InputStruct, OutputStruct, Empty, Quiet>(new Quiet());
            for (var i = 0; i < Keys; i++) Write(session.BasicContext, i);
            for (var i = 0; i < Keys; i++) ClassicAssert.AreEqual((true, i * 10L), Read(session.BasicContext, i), $"key {i} reads back");
        }

        [Test]
        [Category("TsavoriteKV")]
        [Category("Index")]
        public async Task A_halving_unmaps_the_overflow_it_retires_and_every_key_reads_back()
        {
            var store = Open(1L << 12, native: true, "halve");   // 64 buckets: 4,000 keys spill into the overflow
            using var session = store.NewSession<KeyStruct, InputStruct, OutputStruct, Empty, Quiet>(new Quiet());
            var keys = KeysWithoutCollisions(store.IndexSize);
            foreach (var i in keys) Write(session.BasicContext, i);
            ClassicAssert.Greater(store.OverflowBucketCount, 64, "CONTROL: the keys' chains spill into the overflow");
            var pages = store.IndexOverflowPageBytes;
            ClassicAssert.AreEqual(2 * PageBytes, pages, "CONTROL: the overflow maps its first two pages");

            var freed = Freed;
            var vm = NativeMemoryTracker.DirectVmBytes;
            var h = await store.HalveIndexAsync();
            ClassicAssert.IsTrue(h.Halved, h.Refusal);
            ClassicAssert.AreEqual(pages / PageBytes, Freed - freed, "the retired overflow's pages are unmapped at the halving");
            ClassicAssert.LessOrEqual(NativeMemoryTracker.DirectVmBytes - vm, store.IndexOverflowPageBytes - pages,
                $"the process maps the freed pages less, beside the new overflow's: {NativeMemoryTracker.DirectVmBytes - vm:N0}");
            foreach (var i in keys) ClassicAssert.AreEqual((true, i * 10L), Read(session.BasicContext, i), $"key {i} reads back after the halving");
        }

        [Test]
        [Category("TsavoriteKV")]
        [Category("Index")]
        public void A_trim_unmaps_the_pages_past_its_line()
        {
            var overflow = new MallocFixedPageSize<HashBucket>(native: true);
            ClassicAssert.AreEqual(0, overflow.PageBytes, "an empty allocator maps nothing");
            for (var i = 0; i < 3 << 16; i++) _ = overflow.Allocate();
            var pages = overflow.PageBytes;
            ClassicAssert.AreEqual(5 * PageBytes, pages, "CONTROL: three pages of buckets hold five pages (each page's entry makes the next)");

            var freed = Freed;
            var vm = NativeMemoryTracker.DirectVmBytes;
            ClassicAssert.IsTrue(overflow.CanTrimTo(1000), "CONTROL: no checkpoint is writing the pages");
            overflow.TrimTo(1000);
            ClassicAssert.AreEqual(2 * PageBytes, overflow.PageBytes, "a trim inside the first page keeps the first two");
            ClassicAssert.AreEqual(3, Freed - freed, "the three pages past the line are unmapped");
            ClassicAssert.LessOrEqual(NativeMemoryTracker.DirectVmBytes - vm, -3 * PageBytes, "the process maps three pages less");
            for (var i = 0; i < 2000; i++) ClassicAssert.Less(overflow.Allocate(), 1L << 17, "allocation goes on below the kept pages");

            overflow.Dispose();
            ClassicAssert.AreEqual(5, Freed - freed, "a dispose unmaps the rest");
        }

        [Test]
        [Category("TsavoriteKV")]
        [Category("Index")]
        public async Task A_dispose_under_an_overflow_checkpoint_write_parks_until_it_drains()
        {
            var overflow = new MallocFixedPageSize<HashBucket>(native: true);
            for (var i = 0; i < 100; i++) _ = overflow.Allocate();
            ClassicAssert.AreEqual(2 * PageBytes, overflow.PageBytes, "CONTROL: the first allocation maps two pages");

            var device = new HeldDevice();
            overflow.BeginCheckpoint(device, 0, out var written);
            ClassicAssert.Greater(written, 0UL, "CONTROL: the checkpoint writes the first level");
            var (freed, parked, vm) = (Freed, Interlocked.Read(ref MallocFixedPageSize<HashBucket>.NativeDisposesParked), NativeMemoryTracker.DirectVmBytes);
            overflow.Dispose();
            ClassicAssert.AreEqual(1, Interlocked.Read(ref MallocFixedPageSize<HashBucket>.NativeDisposesParked) - parked, "the dispose is parked under the write");
            ClassicAssert.AreEqual(0, Freed - freed, "nothing is unmapped while the write may read it");
            ClassicAssert.AreEqual(vm, NativeMemoryTracker.DirectVmBytes, "the pages stay mapped");

            device.Release();
            await overflow.IsCheckpointCompletedAsync();
            ClassicAssert.AreEqual(2, Freed - freed, "the drain unmaps the parked pages");
            ClassicAssert.LessOrEqual(NativeMemoryTracker.DirectVmBytes - vm, -2 * PageBytes, "the process maps two pages less");
        }

        [Test]
        [Category("TsavoriteKV")]
        [Category("Index")]
        public async Task A_store_recovers_its_mapped_overflow_and_every_key_reads_back([Values] bool spilled)
        {
            var indexBytes = spilled ? 1L << 12 : 1L << 20;   // 64 buckets spill 4,000 keys into the overflow; 16,384 hold a few keys
            var count = spilled ? Keys : 8;
            Guid index, log;
            var store = Open(indexBytes, native: true, "recover", keep: true);
            using (var session = store.NewSession<KeyStruct, InputStruct, OutputStruct, Empty, Quiet>(new Quiet()))
                for (var i = 0; i < count; i++) Write(session.BasicContext, i);
            var pages = store.IndexOverflowPageBytes;
            if (spilled) ClassicAssert.Greater(pages, 0, "CONTROL: a spilled chain maps an overflow page");
            else ClassicAssert.AreEqual(0, pages, "CONTROL: an unspilled store maps no overflow page");
            bool done;
            (done, index) = await store.TakeIndexCheckpointAsync();
            ClassicAssert.IsTrue(done, "the index checkpoint completes");
            (done, log) = await store.TakeHybridLogCheckpointAsync(CheckpointType.FoldOver);
            ClassicAssert.IsTrue(done, "the log checkpoint completes");
            var at = owned.IndexOf(store);   // the store and its log device (added just before it) close before the reopen
            var device = owned[at - 1];
            owned.RemoveRange(at - 1, 2);
            store.Dispose();
            device.Dispose();

            var again = Open(indexBytes, native: true, "recover", keep: true);
            _ = await again.RecoverAsync(index, log);
            ClassicAssert.AreEqual(pages, again.IndexOverflowPageBytes, "the recovered overflow maps what the checkpoint held, and no page for an empty one");
            using var s2 = again.NewSession<KeyStruct, InputStruct, OutputStruct, Empty, Quiet>(new Quiet());
            for (var i = 0; i < count; i++) ClassicAssert.AreEqual((true, i * 10L), Read(s2.BasicContext, i), $"key {i} reads back after recovery");
        }
    }
}
