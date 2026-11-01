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
    /// Ziltch mesh 11-01: its index halved in place live (33,554,432 -> 16,777,216 buckets) and the process gained nothing. The table was a
    /// pinned managed array, so the GC took the old one back but the pinned heap kept its 2.15 GB committed (POH fragmentation 2.31 GB through
    /// 128 later gen2s); only a restart returned it. KVSettings.NativeHashIndex puts one store's table in direct virtual memory, log pages
    /// and frames staying managed, so the table a halving retires is unmapped at once, or when the index-checkpoint write that may read it
    /// drains. Mutant: the setting is not passed to the store (every fact red).
    /// </summary>
    [TestFixture]
    [NonParallelizable]   // the native table counters and the direct-VM byte count are process-wide
    internal class AStoresHashTableIsNativeWhenItsSettingsSayTests : TestBase
    {
        private const int Keys = 400;

        private sealed class Quiet : Functions
        {
            public override void ReadCompletionCallback(ref DiskLogRecord diskLogRecord, ref InputStruct input, ref OutputStruct output, Empty ctx, Status status, RecordMetadata recordMetadata) { }
        }

        private readonly List<IDisposable> owned = [];

        [SetUp]
        public void Setup()
        {
            DeleteDirectory(MethodTestDir, wait: true);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            ClassicAssert.IsFalse(NativeAllocatorInitializer.Enabled, "CONTROL: the process-wide native allocator is off, so only the setting can make a table native");
        }

        [TearDown]
        public void TearDown()
        {
            for (var i = owned.Count - 1; i >= 0; i--) owned[i].Dispose();
            owned.Clear();
            OnTearDown();
        }

        private TsavoriteKV<StructStoreFunctions, StructAllocator> Open(long indexBytes, bool native, string name)
        {
            var log = Devices.CreateLogDevice(Path.Join(MethodTestDir, name + ".log"), deleteOnClose: true);
            owned.Add(log);
            var store = new TsavoriteKV<StructStoreFunctions, StructAllocator>(new KVSettings
            {
                IndexSize = indexBytes, NativeHashIndex = native, LogDevice = log, PageSize = 1L << 12, LogMemorySize = 1L << 20, SegmentSize = 1L << 22
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

        private static (long Freed, long Deferred) Counts() =>
            (Interlocked.Read(ref TsavoriteBase.NativeIndexTableFreeCount), Interlocked.Read(ref TsavoriteBase.NativeIndexTableDeferredCount));

        [Test]
        [Category("TsavoriteKV")]
        [Category("Index")]
        public void A_store_whose_settings_say_native_keeps_its_table_out_of_the_managed_heap()
        {
            const long table = 1L << 26;   // 64 MB, 1,048,576 buckets
            var managedBefore = GC.GetAllocatedBytesForCurrentThread();
            var managed = Open(table, native: false, "managed");
            var managedBytes = GC.GetAllocatedBytesForCurrentThread() - managedBefore;
            ClassicAssert.IsFalse(managed.HashIndexIsNative, "CONTROL: a store whose settings do not say native keeps a managed table");
            ClassicAssert.GreaterOrEqual(managedBytes, table, "CONTROL: a managed table is allocated on the managed heap");

            var nativeBefore = (Heap: GC.GetAllocatedBytesForCurrentThread(), Vm: NativeMemoryTracker.DirectVmBytes);
            var store = Open(table, native: true, "native");
            var heap = GC.GetAllocatedBytesForCurrentThread() - nativeBefore.Heap;
            var vm = NativeMemoryTracker.DirectVmBytes - nativeBefore.Vm;
            ClassicAssert.IsTrue(store.HashIndexIsNative, "the setting puts the table in direct virtual memory");
            ClassicAssert.Less(heap, table / 4, $"the store's construction allocated {heap:N0} managed bytes (a managed table: {managedBytes:N0})");
            ClassicAssert.GreaterOrEqual(vm, table, $"the table is mapped: {vm:N0} direct-VM bytes");

            using var session = store.NewSession<KeyStruct, InputStruct, OutputStruct, Empty, Quiet>(new Quiet());
            for (var i = 0; i < Keys; i++) Write(session.BasicContext, i);
            for (var i = 0; i < Keys; i++) ClassicAssert.AreEqual((true, i * 10L), Read(session.BasicContext, i), $"key {i} reads back");
        }

        [Test]
        [Category("TsavoriteKV")]
        [Category("Index")]
        public async Task A_halving_unmaps_the_table_it_retires_and_every_key_reads_back()
        {
            const long table = 1L << 20;   // 16,384 buckets
            var store = Open(table, native: true, "halve");
            using var session = store.NewSession<KeyStruct, InputStruct, OutputStruct, Empty, Quiet>(new Quiet());
            var keys = KeysWithoutCollisions(store.IndexSize);
            foreach (var i in keys) Write(session.BasicContext, i);

            var (freed, deferred) = Counts();
            var vm = NativeMemoryTracker.DirectVmBytes;
            var h = await store.HalveIndexAsync();
            ClassicAssert.IsTrue(h.Halved, h.Refusal);
            ClassicAssert.AreEqual(1, Counts().Freed - freed, "the retired table is unmapped at the halving");
            ClassicAssert.AreEqual(0, Counts().Deferred - deferred, "no index-checkpoint write is out, so nothing is parked");
            ClassicAssert.LessOrEqual(NativeMemoryTracker.DirectVmBytes - vm, -(table / 2), $"the process maps half a table less: {NativeMemoryTracker.DirectVmBytes - vm:N0}");
            foreach (var i in keys) ClassicAssert.AreEqual((true, i * 10L), Read(session.BasicContext, i), $"key {i} reads back after the halving");
        }

        [Test]
        [Category("TsavoriteKV")]
        [Category("Index")]
        public async Task A_table_retired_while_an_index_checkpoint_write_is_out_is_parked_until_it_drains()
        {
            const long table = 1L << 20;
            var store = Open(table, native: true, "parked");
            using var session = store.NewSession<KeyStruct, InputStruct, OutputStruct, Empty, Quiet>(new Quiet());
            var keys = KeysWithoutCollisions(store.IndexSize);
            foreach (var i in keys) Write(session.BasicContext, i);

            var (freed, deferred) = Counts();
            var vm = NativeMemoryTracker.DirectVmBytes;
            store.BeginNativeIndexCheckpointIo();   // a write of the index still reads the table it was issued from
            var h = await store.HalveIndexAsync();
            ClassicAssert.IsTrue(h.Halved, h.Refusal);
            ClassicAssert.AreEqual(1, Counts().Deferred - deferred, "the retired table is parked while the write is out");
            ClassicAssert.AreEqual(0, Counts().Freed - freed, "nothing is unmapped under the write");
            ClassicAssert.Greater(NativeMemoryTracker.DirectVmBytes - vm, 0, "the old table and the new are both mapped");
            foreach (var i in keys) ClassicAssert.AreEqual((true, i * 10L), Read(session.BasicContext, i), $"key {i} reads back while the old table is parked");

            store.EndNativeIndexCheckpointIo();
            ClassicAssert.AreEqual(1, Counts().Freed - freed, "the parked table is unmapped when the write drains");
            ClassicAssert.LessOrEqual(NativeMemoryTracker.DirectVmBytes - vm, -(table / 2), "the process maps half a table less");
            foreach (var i in keys) ClassicAssert.AreEqual((true, i * 10L), Read(session.BasicContext, i), $"key {i} reads back after the drain");
        }
    }
}
