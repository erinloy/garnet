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
    /// A pending read drained by a session still in v's PREPARE, over a key a v+1 session has since written in memory, met CPR_SHIFT_DETECTED in
    /// ContinuePendingRead. The drain handed it to HandleOperationStatus, which only asserts it was handled, so a Release build completed the
    /// read as Error: no output and no callback (1-3 a run under checkpoints every few ms). The reader is an UnsafeContext because its drain
    /// does not refresh first, which is how a session's PREPARE outlives the system's move to v+1 in a real checkpoint.
    /// </summary>
    [TestFixture]
    internal class APendingReadThatMeetsANewerVersionRetriesTests : TestBase
    {
        private const int Keys = 64, Asked = 7, Newer = 1_000;

        /// <summary>The shared Functions asserts value == key at a pending completion; here the read must see the newer value.</summary>
        private sealed class Quiet : Functions
        {
            public override void ReadCompletionCallback(ref DiskLogRecord diskLogRecord, ref InputStruct input, ref OutputStruct output, Empty ctx, Status status, RecordMetadata recordMetadata) { }
        }

        [SetUp]
        public void Setup() { DeleteDirectory(MethodTestDir); _ = Directory.CreateDirectory(MethodTestDir); }

        [TearDown]
        public void TearDown() => OnTearDown();

        private static KeyStruct Key(int i) => new() { kfield1 = i, kfield2 = i + 1 };

        [Test]
        [Category("TsavoriteKV")]
        [Category("CheckpointRestore")]
        public void A_pending_read_that_meets_a_newer_version_in_memory_reads_it([Values] bool newerVersion)
        {
            using var device = Devices.CreateLogDevice(Path.Join(MethodTestDir, "shift.log"));
            using var store = new TsavoriteKV<StructStoreFunctions, StructAllocator>(
                new KVSettings { IndexSize = 1L << 12, LogDevice = device, PageSize = 1L << 12, LogMemorySize = 1L << 16, CheckpointDir = MethodTestDir },
                StoreFunctions.Create(KeyStruct.Comparer.Instance, SpanByteRecordTriggers.Instance),
                (allocatorSettings, storeFunctions) => new(allocatorSettings, storeFunctions));
            using var reader = store.NewSession<KeyStruct, InputStruct, OutputStruct, Empty, Functions>(new Quiet());
            using var writer = store.NewSession<KeyStruct, InputStruct, OutputStruct, Empty, Functions>(new Functions());

            for (var i = 0; i < Keys; i++)
            {
                var v = new ValueStruct { vfield1 = i, vfield2 = i + 1 };
                _ = writer.BasicContext.Upsert(Key(i), SpanByte.FromPinnedVariable(ref v), Empty.Default);
            }
            store.Log.FlushAndEvict(wait: true);
            var v1 = store.CurrentVersion;

            // The reader enters v's PREPARE and its read of a key on the device goes pending.
            store.stateMachineDriver.SetSystemState(SystemState.Make(Phase.PREPARE, v1));
            var u = reader.UnsafeContext;
            u.BeginUnsafe();
            try
            {
                InputStruct input = default;
                OutputStruct output = default;
                var status = u.Read(Key(Asked), ref input, ref output, Empty.Default);
                ClassicAssert.IsTrue(status.IsPending, "the asked key is on the device, so its read goes pending");

                // The system moves to v+1 and a writer there lands the key in memory (the control writes it from v's PREPARE: no shift).
                if (newerVersion) store.stateMachineDriver.SetSystemState(SystemState.Make(Phase.IN_PROGRESS, v1 + 1));
                Task.Run(() =>
                {
                    var newer = new ValueStruct { vfield1 = Newer, vfield2 = Newer + 1 };
                    _ = writer.BasicContext.Upsert(Key(Asked), SpanByte.FromPinnedVariable(ref newer), Empty.Default);
                }).GetAwaiter().GetResult();

                var deadline = Environment.TickCount64 + 10_000;
                while (reader.ctx.readyResponses.Count == 0 && Environment.TickCount64 < deadline) _ = Thread.Yield();
                ClassicAssert.Greater(reader.ctx.readyResponses.Count, 0, "the pending read completed on the device within 10 s");
                ClassicAssert.AreEqual(Phase.PREPARE, reader.ctx.phase, "the reader drains from v's PREPARE");

                _ = u.CompletePendingWithOutputs(out var completed, wait: true);
                using (completed)
                {
                    ClassicAssert.IsTrue(completed.Next(), "the pending read produced a completion: a read that met a newer version is retried, "
                        + "not failed (it completed as Error with no output, and a reader took that for an absent key)");
                    ClassicAssert.IsTrue(completed.Current.Status.Found, $"the key is there: {completed.Current.Status}");
                    ClassicAssert.AreEqual(Newer, completed.Current.Output.value.vfield1, "the read sees the write made while it was pending");
                }
            }
            finally
            {
                u.EndUnsafe();
                store.stateMachineDriver.SetSystemState(SystemState.Make(Phase.REST, v1 + 1));
            }
        }
    }
}
