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
    /// A kill loses every write since the last fold-over checkpoint, flushed or not: recovery stops at the checkpoint's final address.
    /// Given an address the caller recorded as flushed, recovery replays the log to it, never past it, and undoes nothing past final:
    /// a record written in the checkpoint's next version below final (J) is part of the flushed prefix a later record (K) was built on.
    /// </summary>
    [TestFixture]
    internal class ARecoveryReplaysPastItsCheckpointToAFlushedMarkTests : TestBase
    {
        private const int Before = 100, After = 2_000;

        private TsavoriteKV<StructStoreFunctions, StructAllocator> Open(IDevice device)
            => new(new() { IndexSize = 1L << 16, LogDevice = device, LogMemorySize = 1L << 20, PageSize = 1L << 12, CheckpointDir = MethodTestDir },
                StoreFunctions.Create(KeyStruct.Comparer.Instance, SpanByteRecordTriggers.Instance),
                (allocatorSettings, storeFunctions) => new(allocatorSettings, storeFunctions));

        private static KeyStruct Key(int i) => new() { kfield1 = i, kfield2 = i + 1 };

        private static void Upsert(BasicContext<KeyStruct, InputStruct, OutputStruct, Empty, Functions, StructStoreFunctions, StructAllocator> bContext, int i, long value)
        {
            var v = new ValueStruct { vfield1 = value, vfield2 = value + 1 };
            _ = bContext.Upsert(Key(i), SpanByte.FromPinnedVariable(ref v), Empty.Default);
        }

        private static void Upsert(TsavoriteKV<StructStoreFunctions, StructAllocator> store, int from, int count, long value)
        {
            using var session = store.NewSession<KeyStruct, InputStruct, OutputStruct, Empty, Functions>(new Functions());
            for (var i = from; i < from + count; i++) Upsert(session.BasicContext, i, value);
            _ = session.BasicContext.CompletePending(true);
        }

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

        private static long Flushed(TsavoriteKV<StructStoreFunctions, StructAllocator> store)
        {
            store.Log.ShiftReadOnlyAddress(store.Log.TailAddress, wait: true);
            return store.Log.FlushedUntilAddress;
        }

        private async Task<(Guid Index, Guid Log)> CheckpointAsync(TsavoriteKV<StructStoreFunctions, StructAllocator> store)
        {
            var (ok, token) = await store.TakeFullCheckpointAsync(CheckpointType.FoldOver);
            ClassicAssert.IsTrue(ok, "the checkpoint completes");
            return (token, token);
        }

        [SetUp]
        public void Setup() { DeleteDirectory(MethodTestDir); _ = Directory.CreateDirectory(MethodTestDir); }

        [TearDown]
        public void TearDown() => OnTearDown();

        [Test]
        [Category("TsavoriteKV")]
        [Category("CheckpointRestore")]
        public async Task Writes_flushed_after_the_checkpoint_come_back_to_the_mark_and_none_past_it()
        {
            (Guid Index, Guid Log) tokens;
            long mark, final;
            {
                using var device = Devices.CreateLogDevice(Path.Join(MethodTestDir, "mark.log"));
                using var store = Open(device);
                Upsert(store, 0, Before, 1);
                tokens = await CheckpointAsync(store);
                final = store.Log.TailAddress;
                Upsert(store, Before, After, 2);
                mark = Flushed(store);
                Upsert(store, Before + After, After, 3);   // flushed too, but past the mark this caller recorded
                _ = Flushed(store);
            }   // killed: no checkpoint after the writes
            ClassicAssert.Greater(mark, final, "control: the writes after the checkpoint reached the device");

            using (var device = Devices.CreateLogDevice(Path.Join(MethodTestDir, "mark.log")))
            using (var store = Open(device))
            {
                _ = await store.RecoverAsync(tokens.Index, tokens.Log);
                ClassicAssert.IsNull(Read(store, Before), "control: recovery to the checkpoint alone loses the flushed write");
            }

            using (var device = Devices.CreateLogDevice(Path.Join(MethodTestDir, "mark.log")))
            using (var store = Open(device))
            {
                _ = await store.RecoverAsync(tokens.Index, tokens.Log, mark);
                // A mark on a page boundary resumes past that page's header, as every page's first record does.
                ClassicAssert.AreEqual(Math.Max(mark, store.hlogBase.GetFirstValidLogicalAddressOnPage(store.hlogBase.GetPage(mark))), store.Log.TailAddress, "the log resumes at the mark");
                ClassicAssert.AreEqual(mark - final, store.LastReplayedPastFinal, "replayed past final to the mark");
                for (var i = 0; i < Before; i++) ClassicAssert.AreEqual(1, Read(store, i), $"key {i} from the checkpoint");
                for (var i = Before; i < Before + After; i++) ClassicAssert.AreEqual(2, Read(store, i), $"key {i}, flushed before the mark");
                for (var i = Before + After; i < Before + 2 * After; i++) ClassicAssert.IsNull(Read(store, i), $"key {i} lies past the mark: never replayed");
            }
        }

        [Test]
        [Category("TsavoriteKV")]
        [Category("CheckpointRestore")]
        public async Task A_next_version_write_below_final_and_a_later_write_built_on_it_both_come_back()
        {
            const int J = 7, K = 5_000;
            (Guid Index, Guid Log) tokens;
            long mark;
            {
                using var device = Devices.CreateLogDevice(Path.Join(MethodTestDir, "jk.log"));
                using var store = Open(device);
                Upsert(store, 0, Before, 1);   // J reads 1 at version v

                // J is written again in the checkpoint's next version, while the checkpoint is in flight: below its final address.
                // The checkpoint is held at the edge of IN_PROGRESS until J is written, so the write always lands in that window.
                using (var session = store.NewSession<KeyStruct, InputStruct, OutputStruct, Empty, Functions>(new Functions()))
                {
                    var bContext = session.BasicContext;
                    using var jWritten = new ManualResetEventSlim();
                    store.stateMachineDriver.UnsafeRegisterCallback(new HoldPast(Phase.IN_PROGRESS, jWritten));
                    var checkpoint = CheckpointAsync(store);
                    var wroteJ = false;
                    for (var spin = 0; !checkpoint.IsCompleted && !wroteJ && spin < 1_000_000; spin++)
                    {
                        if (store.stateMachineDriver.SystemState.Phase == Phase.IN_PROGRESS) { Upsert(bContext, J, 2); wroteJ = session.ctx.phase == Phase.IN_PROGRESS; jWritten.Set(); }
                        else await Task.Yield();
                    }
                    jWritten.Set();
                    tokens = await checkpoint;
                    ClassicAssert.IsTrue(wroteJ, "control: J was written while the checkpoint was in its next version");
                }

                var j = Read(store, J);
                ClassicAssert.AreEqual(2, j, "control: J reads its next-version write before the kill");
                Upsert(store, K, 1, j.Value + 100);   // K is built on J
                mark = Flushed(store);
            }   // killed

            using (var device = Devices.CreateLogDevice(Path.Join(MethodTestDir, "jk.log")))
            using (var store = Open(device))
            {
                _ = await store.RecoverAsync(tokens.Index, tokens.Log, mark);
                ClassicAssert.AreEqual(102, Read(store, K), "K, past final, comes back");
                ClassicAssert.AreEqual(2, Read(store, J), "J, the next-version write K read, comes back with it, not undone to its version-v value");
            }
        }

        /// <summary>Holds the driver before it leaves <paramref name="phase"/> until the test has done its write in it (bounded).</summary>
        private sealed class HoldPast(Phase phase, ManualResetEventSlim done) : IStateMachineCallback
        {
            private bool _inPhase;
            public void BeforeEnteringState(SystemState next)
            {
                if (_inPhase) { _inPhase = false; _ = done.Wait(TimeSpan.FromSeconds(30)); }
                if (next.Phase == phase) _inPhase = true;
            }
        }
    }
}
