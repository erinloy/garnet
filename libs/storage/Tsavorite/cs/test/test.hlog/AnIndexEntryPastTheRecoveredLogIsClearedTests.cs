// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Garnet.test;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using Tsavorite.core;
using static Tsavorite.test.TestUtils;

namespace Tsavorite.test
{
    using StructAllocator = SpanByteAllocator<StoreFunctions<KeyStruct.Comparer, SpanByteRecordTriggers>>;
    using StructStoreFunctions = StoreFunctions<KeyStruct.Comparer, SpanByteRecordTriggers>;

    /// <summary>
    /// A HASH INDEX ENTRY PAST THE RECOVERED LOG'S FINAL ADDRESS IS CLEARED AT RECOVERY.
    ///
    /// <para>Recovery pairs an index checkpoint only with a log checkpoint at or past it, so a CONSISTENT pair has no such entry.
    /// This is defence in depth for the inconsistent pair a partially restored or hand-assembled store can present: an index
    /// snapshot naming records its log checkpoint does not contain. Without the scrub those entries survive recovery as pointers
    /// to bytes the store will never have - the shape of a read that goes to the device past what the log holds. The pair is
    /// BUILT here, not simulated: a log checkpoint, more writes, an index checkpoint, and the index metadata's start/final
    /// addresses rewritten down to the log's (checksum re-derived) so recovery accepts it.</para>
    ///
    /// <para>MEASURED, AND SAID PLAINLY: with the scrub removed, the past-the-log key in this construction STILL reads NotFound -
    /// the stale entry points above the recovered tail, where no record was ever placed, and the read rejects it. What the
    /// scrub demonstrably changes is that the stale pointers do not survive into the running store (where a later append can
    /// place a DIFFERENT record at that address) and that recovery REPORTS the inconsistency instead of absorbing it. The
    /// mutation (scrub call removed) turns this arm red on that report.</para>
    /// </summary>
    [TestFixture]
    internal class AnIndexEntryPastTheRecoveredLogIsClearedTests : TestBase
    {
        private const int Before = 100, After = 100;

        private sealed class CapturingLogger : ILogger
        {
            public readonly List<string> Warnings = [];
            public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
            {
                if (logLevel == LogLevel.Warning)
                    lock (Warnings) Warnings.Add(formatter(state, exception));
            }
        }

        private TsavoriteKV<StructStoreFunctions, StructAllocator> Open(IDevice device, ILogger logger = null)
            => new(new()
            {
                IndexSize = 1L << 16,
                LogDevice = device,
                LogMemorySize = 1L << 20,
                PageSize = 1L << 12,
                CheckpointDir = MethodTestDir,
                logger = logger,
            }, StoreFunctions.Create(KeyStruct.Comparer.Instance, SpanByteRecordTriggers.Instance),
            (allocatorSettings, storeFunctions) => new(allocatorSettings, storeFunctions));

        private static void Upsert(TsavoriteKV<StructStoreFunctions, StructAllocator> store, int from, int count)
        {
            using var session = store.NewSession<KeyStruct, InputStruct, OutputStruct, Empty, Functions>(new Functions());
            var bContext = session.BasicContext;
            for (var i = from; i < from + count; i++)
            {
                var key = new KeyStruct { kfield1 = i, kfield2 = i + 1 };
                var value = new ValueStruct { vfield1 = i, vfield2 = i + 1 };
                _ = bContext.Upsert(key, SpanByte.FromPinnedVariable(ref value), Empty.Default);
            }
            _ = bContext.CompletePending(true);
        }

        private static Status Read(TsavoriteKV<StructStoreFunctions, StructAllocator> store, int i)
        {
            using var session = store.NewSession<KeyStruct, InputStruct, OutputStruct, Empty, Functions>(new Functions());
            var bContext = session.BasicContext;
            InputStruct input = default;
            OutputStruct output = default;
            var key = new KeyStruct { kfield1 = i, kfield2 = i + 1 };
            var status = bContext.Read(key, ref input, ref output, Empty.Default);
            if (status.IsPending)
            {
                _ = bContext.CompletePendingWithOutputs(out var outputs, wait: true);
                using (outputs)
                {
                    ClassicAssert.IsTrue(outputs.Next(), "a pending read completes");
                    status = outputs.Current.Status;
                }
            }
            return status;
        }

        private static long LineAsLong(string[] lines, int index) => long.Parse(lines[index]);

        [Test]
        [Category("TsavoriteKV")]
        public async Task An_index_snapshot_naming_records_past_its_log_checkpoint_recovers_with_those_entries_cleared()
        {
            DeleteDirectory(MethodTestDir);
            _ = Directory.CreateDirectory(MethodTestDir);
            Guid logToken, indexToken;
            long logFinal;
            {
                using var device = Devices.CreateLogDevice(Path.Join(MethodTestDir, "scrub.log"));
                using var store = Open(device);
                Upsert(store, 0, Before);
                (var logOk, logToken) = await store.TakeHybridLogCheckpointAsync(CheckpointType.FoldOver);
                ClassicAssert.IsTrue(logOk);
                var logLines = Encoding.UTF8.GetString(store.CheckpointManager.GetLogCheckpointMetadata(logToken)).Split('\n').Select(l => l.Trim()).ToArray();
                logFinal = LineAsLong(logLines, 9);   // HybridLogRecoveryInfo: ..., startLogicalAddress (8), finalLogicalAddress (9)

                Upsert(store, Before, After);
                (var indexOk, indexToken) = await store.TakeIndexCheckpointAsync();
                ClassicAssert.IsTrue(indexOk);

                // The inconsistent pair: rewrite the index snapshot's start/final addresses down to the log's, re-deriving the
                // XOR checksum, so recovery's compatibility check (index final <= log final) accepts an index whose entries for
                // the After keys point past the log checkpoint's final address.
                var indexLines = Encoding.UTF8.GetString(store.CheckpointManager.GetIndexCheckpointMetadata(indexToken)).Split('\n').Select(l => l.Trim()).ToArray();
                var checksum = LineAsLong(indexLines, 1);
                var start = LineAsLong(indexLines, 7);
                var final = LineAsLong(indexLines, 8);
                ClassicAssert.Greater(final, logFinal, "the control: the index checkpoint was taken after writes the log checkpoint does not contain");
                indexLines[1] = (checksum ^ start ^ final ^ logFinal ^ logFinal).ToString();
                indexLines[7] = logFinal.ToString();
                indexLines[8] = logFinal.ToString();
                var rewritten = string.Join(Environment.NewLine, indexLines.Take(9)) + Environment.NewLine;
                store.CheckpointManager.CommitIndexCheckpoint(indexToken, Encoding.UTF8.GetBytes(rewritten));
            }

            var logger = new CapturingLogger();
            using (var device = Devices.CreateLogDevice(Path.Join(MethodTestDir, "scrub.log")))
            using (var store = Open(device, logger))
            {
                _ = await store.RecoverAsync(indexToken, logToken);

                ClassicAssert.IsTrue(Read(store, Before / 2).Found, "the control: a record inside the log checkpoint recovers and reads");
                ClassicAssert.IsTrue(Read(store, Before + After / 2).NotFound,
                    "a record only the index snapshot named - past the log checkpoint - must read as absent, not follow an entry into bytes the log does not have");
                ClassicAssert.IsTrue(logger.Warnings.Any(w => w.Contains("Recovery cleared")),
                    "recovery must report clearing the index entries past the log's final address; it reported: " + string.Join(" | ", logger.Warnings));
            }
            DeleteDirectory(MethodTestDir);
        }
    }
}
