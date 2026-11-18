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
    /// A CLEAN INDEX CHECKPOINT IS TAKEN AS WRITTEN (fyre 11-18, plan:unit/a-cold-chunk-index-opens-without-reading-its-table, stage 1a).
    ///
    /// <para>Recovery ran two passes over every bucket of the table it read: one clears tentative entries and bucket lock bits, one
    /// clears links past the log's end or off the record alignment. Those passes are what stops a reopened store from mapping its
    /// table and faulting in only what it reads. A clean checkpoint runs the same passes on its copy before writing, and its commit
    /// metadata says so, with the highest address the file names. A recovery that finds the mark, with that address below the log's
    /// end, takes the table as read. A missing, torn or behind mark means the walk runs as before (rg #91885 asks 1-3).</para>
    /// </summary>
    [TestFixture]
    internal class ACleanIndexCheckpointIsTakenAsWrittenTests : TestBase
    {
        private const int Keys = 100;

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

        private TsavoriteKV<StructStoreFunctions, StructAllocator> Open(IDevice device, bool clean, ILogger logger = null, bool readCache = false)
        {
            var store = new TsavoriteKV<StructStoreFunctions, StructAllocator>(new()
            {
                IndexSize = 1L << 16,
                LogDevice = device,
                LogMemorySize = 1L << 20,
                PageSize = 1L << 12,
                CheckpointDir = MethodTestDir,
                logger = logger,
                ReadCacheEnabled = readCache,
                ReadCacheMemorySize = 1L << 16,
                ReadCachePageSize = 1L << 12,
            }, StoreFunctions.Create(KeyStruct.Comparer.Instance, SpanByteRecordTriggers.Instance),
            (allocatorSettings, storeFunctions) => new(allocatorSettings, storeFunctions));
            store.WritesCleanIndexCheckpoints = clean;
            return store;
        }

        private static KeyStruct Key(int i) => new() { kfield1 = i, kfield2 = i + 1 };

        private static void Upsert(TsavoriteKV<StructStoreFunctions, StructAllocator> store, int from, int count)
        {
            using var session = store.NewSession<KeyStruct, InputStruct, OutputStruct, Empty, Functions>(new Functions());
            var bContext = session.BasicContext;
            for (var i = from; i < from + count; i++)
            {
                var value = new ValueStruct { vfield1 = i, vfield2 = i + 1 };
                _ = bContext.Upsert(Key(i), SpanByte.FromPinnedVariable(ref value), Empty.Default);
            }
            _ = bContext.CompletePending(true);
        }

        private static Status Read(TsavoriteKV<StructStoreFunctions, StructAllocator> store, int i)
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
                    status = outputs.Current.Status;
                }
            }
            return status;
        }

        /// <summary>Every bucket and overflow bucket of the live table: tentative entries, read-cache entries, overflow words with a lock bit.</summary>
        private static unsafe (long Tentative, long ReadCache, long Locked) TableCensus(TsavoriteKV<StructStoreFunctions, StructAllocator> store)
        {
            var version = store.resizeInfo.version;
            var table = store.state[version].tableAligned;
            long tentative = 0, readCache = 0, locked = 0;
            HashBucketEntry entry = default;
            for (long i = 0; i < store.state[version].size; i++)
            {
                var b = table + i;
                while (true)
                {
                    for (var e = 0; e < Constants.kOverflowBucketIndex; e++)
                    {
                        entry.word = b->bucket_entries[e];
                        if (entry.word == 0) continue;
                        if (entry.Tentative) tentative++;
                        if (entry.IsReadCache) readCache++;
                    }
                    var word = b->bucket_entries[Constants.kOverflowBucketIndex];
                    if ((word & ~LogAddress.kAddressBitMask) != 0) locked++;
                    var overflow = word & LogAddress.kAddressBitMask;
                    if (overflow == 0) break;
                    b = (HashBucket*)store.overflowBucketsAllocator.GetPhysicalAddress(overflow);
                }
            }
            return (tentative, readCache, locked);
        }

        /// <summary>Plants what recovery's passes clear: a tentative entry (a new key's tag, marked tentative), a lock bit on the overflow
        /// word of that bucket, and the victim's entry moved 3 bytes off its record. Returns the keys that share the victim's chain.</summary>
        private static unsafe HashSet<int> PlantRecoveryWork(TsavoriteKV<StructStoreFunctions, StructAllocator> store, int victim)
        {
            HashEntryInfo Entry(int i) => new(store.storeFunctions.GetKeyHashCode64(Key(i)));
            var sharing = new HashSet<int>();
            var hei = Entry(victim);
            ClassicAssert.IsTrue(store.FindTag(ref hei), "CONTROL: the victim has an entry");
            for (var i = 0; i < Keys; i++)
            {
                var other = Entry(i);
                if (store.FindTag(ref other) && other.bucketIndex == hei.bucketIndex && other.tag == hei.tag) _ = sharing.Add(i);
            }
            hei.entry.Set(hei.Address + 3, hei.tag);
            hei.bucket->bucket_entries[hei.slot] = hei.entry.word;

            HashEntryInfo pending = new(store.storeFunctions.GetKeyHashCode64(Key(-7)));
            store.FindOrCreateTag(ref pending, store.Log.BeginAddress);
            pending.entry.Tentative = true;
            pending.bucket->bucket_entries[pending.slot] = pending.entry.word;
            pending.bucket->bucket_entries[Constants.kOverflowBucketIndex] |= 1L << 60;
            return sharing;
        }

        private static string[] IndexLines(TsavoriteKV<StructStoreFunctions, StructAllocator> store, Guid token)
            => Encoding.UTF8.GetString(store.CheckpointManager.GetIndexCheckpointMetadata(token)).Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();

        [Test]
        [Category("TsavoriteKV")]
        public async Task A_clean_checkpoint_clears_what_recovery_would_and_its_recovery_takes_the_table_as_read()
        {
            DeleteDirectory(MethodTestDir);
            _ = Directory.CreateDirectory(MethodTestDir);
            Guid token;
            HashSet<int> sharing;
            var victim = Keys / 2;
            var writer = new CapturingLogger();
            {
                using var device = Devices.CreateLogDevice(Path.Join(MethodTestDir, "clean.log"));
                using var store = Open(device, clean: true, writer);
                Upsert(store, 0, Keys);
                store.Log.FlushAndEvict(wait: true);
                sharing = PlantRecoveryWork(store, victim);
                ClassicAssert.AreEqual((1L, 0L, 1L), TableCensus(store), "CONTROL: the live table holds the tentative entry and the lock bit");
                (var ok, token) = await store.TakeFullCheckpointAsync(CheckpointType.FoldOver);
                ClassicAssert.IsTrue(ok);
                var census = store.LastCleanIndexCheckpoint;
                ClassicAssert.AreEqual((true, 1L, 1L), (census.Clean, census.Tentative, census.Misaligned), "the copy cleared the tentative entry and the corrupt link");
                ClassicAssert.Greater(census.MaxAddress, 0L, "it names the highest address its entries hold");
                ClassicAssert.AreEqual((1L, 0L, 1L), TableCensus(store), "the live table is untouched: only the copy is cleaned");
                ClassicAssert.IsTrue(writer.Warnings.Any(w => w.Contains("off the 8-byte record alignment")), "the checkpoint names the corrupt link it cleared");
                ClassicAssert.AreEqual("clean-index-checkpoint", IndexLines(store, token)[9], "the mark rides in the commit metadata itself, after the nine lines it had");
            }

            using (var device = Devices.CreateLogDevice(Path.Join(MethodTestDir, "clean.log")))
            using (var store = Open(device, clean: true))
            {
                _ = await store.RecoverAsync(token, token);
                ClassicAssert.IsTrue(store.LastRecoveryTrustedCleanMark, "the mark is below the log's end, so the passes are skipped");
                ClassicAssert.AreEqual((0L, 0L, 0L), TableCensus(store),
                    "MUTANT (the copy keeps a tentative entry or a lock bit): the recovered table holds no tentative entry and no lock bit with no pass run over it");
                for (var i = 0; i < Keys; i++)
                {
                    var status = Read(store, i);
                    if (sharing.Contains(i)) ClassicAssert.IsTrue(status.NotFound, $"MUTANT (the copy keeps a misaligned link): key {i} shared the cleared chain ({status})");
                    else ClassicAssert.IsTrue(status.Found, $"key {i} reads ({status})");
                }
            }
            DeleteDirectory(MethodTestDir);
        }

        [Test]
        [Category("TsavoriteKV")]
        public async Task A_mark_that_names_an_address_past_the_logs_end_is_not_trusted_and_the_scrub_runs()
        {
            DeleteDirectory(MethodTestDir);
            _ = Directory.CreateDirectory(MethodTestDir);
            Guid logToken, indexToken;
            {
                using var device = Devices.CreateLogDevice(Path.Join(MethodTestDir, "behind.log"));
                using var store = Open(device, clean: true);
                Upsert(store, 0, Keys);
                (var logOk, logToken) = await store.TakeHybridLogCheckpointAsync(CheckpointType.FoldOver);
                ClassicAssert.IsTrue(logOk);
                var logFinal = long.Parse(Encoding.UTF8.GetString(store.CheckpointManager.GetLogCheckpointMetadata(logToken)).Split('\n').Select(l => l.Trim()).ToArray()[9]);
                Upsert(store, Keys, Keys);
                (var indexOk, indexToken) = await store.TakeIndexCheckpointAsync();
                ClassicAssert.IsTrue(indexOk);

                // The inconsistent pair of AnIndexEntryPastTheRecoveredLogIsClearedTests, with its mark kept whole: both checksums re-derived.
                var lines = IndexLines(store, indexToken);
                ClassicAssert.AreEqual("clean-index-checkpoint", lines[9], "CONTROL: the index checkpoint is marked");
                var (checksum, start, final, max) = (long.Parse(lines[1]), long.Parse(lines[7]), long.Parse(lines[8]), long.Parse(lines[10]));
                ClassicAssert.GreaterOrEqual(max, logFinal, "CONTROL: the mark names records past the log checkpoint");
                var rewritten = checksum ^ start ^ final ^ logFinal ^ logFinal;
                (lines[1], lines[7], lines[8], lines[11]) = (rewritten.ToString(), logFinal.ToString(), logFinal.ToString(), (long.Parse(lines[11]) ^ checksum ^ rewritten).ToString());
                store.CheckpointManager.CommitIndexCheckpoint(indexToken, Encoding.UTF8.GetBytes(string.Join(Environment.NewLine, lines) + Environment.NewLine));
            }

            using (var device = Devices.CreateLogDevice(Path.Join(MethodTestDir, "behind.log")))
            using (var store = Open(device, clean: true))
            {
                _ = await store.RecoverAsync(indexToken, logToken);
                ClassicAssert.IsFalse(store.LastRecoveryTrustedCleanMark, "MUTANT (the mark trusted blind): its address is at or past the log's end");
                ClassicAssert.AreEqual((long)Keys, store.RecoveryScrubbedEntries, "the scrub ran and cleared every entry only the index named");
                ClassicAssert.IsTrue(Read(store, Keys + Keys / 2).NotFound, "a record past the log checkpoint reads as absent");
            }

            Guid plainToken;
            {
                using var device = Devices.CreateLogDevice(Path.Join(MethodTestDir, "plain.log"));
                using var store = Open(device, clean: false);
                Upsert(store, 0, Keys);
                (var ok, plainToken) = await store.TakeFullCheckpointAsync(CheckpointType.FoldOver);
                ClassicAssert.IsTrue(ok);
                var plain = IndexLines(store, plainToken);
                CollectionAssert.DoesNotContain(plain, "clean-index-checkpoint", "a store that does not write clean checkpoints writes no mark after the nine lines it always did");
                ClassicAssert.IsTrue(long.TryParse(plain[8], out _), "its ninth line is still the final address");
            }
            using (var device = Devices.CreateLogDevice(Path.Join(MethodTestDir, "plain.log")))
            using (var reopened = Open(device, clean: false))
            {
                _ = await reopened.RecoverAsync(plainToken, plainToken);
                ClassicAssert.IsFalse(reopened.LastRecoveryTrustedCleanMark, "no mark, so the walk runs");
            }
            DeleteDirectory(MethodTestDir);
        }

        [Test]
        [Category("ReadCache")]
        public async Task A_read_cache_entry_is_written_as_the_log_address_beneath_it()
        {
            DeleteDirectory(MethodTestDir);
            _ = Directory.CreateDirectory(MethodTestDir);
            Guid token;
            {
                using var device = Devices.CreateLogDevice(Path.Join(MethodTestDir, "rc.log"));
                using var store = Open(device, clean: true, readCache: true);
                Upsert(store, 0, Keys);
                store.Log.FlushAndEvict(wait: true);
                for (var i = 0; i < Keys; i++) ClassicAssert.IsTrue(Read(store, i).Found);
                ClassicAssert.Greater(TableCensus(store).ReadCache, 0L, "CONTROL: reads of evicted records put read-cache entries in the table");
                (var ok, token) = await store.TakeFullCheckpointAsync(CheckpointType.FoldOver);
                ClassicAssert.IsTrue(ok);
                ClassicAssert.IsTrue(store.LastCleanIndexCheckpoint.Clean, "MUTANT (the read-cache skip dropped from the clean fix): a read-cache entry left in the copy refuses the mark");
            }
            using (var device = Devices.CreateLogDevice(Path.Join(MethodTestDir, "rc.log")))
            using (var store = Open(device, clean: true, readCache: true))
            {
                _ = await store.RecoverAsync(token, token);
                ClassicAssert.IsTrue(store.LastRecoveryTrustedCleanMark);
                ClassicAssert.AreEqual(0L, TableCensus(store).ReadCache, "MUTANT (the scan passes a read-cache entry as a log address): no recovered entry points into a cache this life does not have");
                for (var i = 0; i < Keys; i++) ClassicAssert.IsTrue(Read(store, i).Found, $"key {i} reads through the log address its entry was written as");
            }
            DeleteDirectory(MethodTestDir);
        }

        /// <summary>The reader before the mark, as of c89e7afa5: nine lines, version 1 and the XOR checksum. A rolled-back binary reads a
        /// marked checkpoint with exactly this, so it must accept it (rg #91885 ask 3).</summary>
        private static (long TableSize, long Final) PreMarkReader(byte[] metadata)
        {
            using var reader = new StreamReader(new MemoryStream(metadata));
            var version = int.Parse(reader.ReadLine());
            var checksum = long.Parse(reader.ReadLine());
            var bytes = Guid.Parse(reader.ReadLine()).ToByteArray();
            var tableSize = long.Parse(reader.ReadLine());
            var ht = ulong.Parse(reader.ReadLine());
            var ofb = ulong.Parse(reader.ReadLine());
            var buckets = int.Parse(reader.ReadLine());
            var start = long.Parse(reader.ReadLine());
            var final = long.Parse(reader.ReadLine());
            ClassicAssert.AreEqual(1, version, "the pre-mark reader refuses any other version");
            ClassicAssert.AreEqual(BitConverter.ToInt64(bytes, 0) ^ BitConverter.ToInt64(bytes, 8) ^ tableSize ^ (long)ht ^ (long)ofb ^ buckets ^ start ^ final, checksum,
                "the pre-mark reader refuses a checksum the nine lines do not give");
            return (tableSize, final);
        }

        [Test]
        [Category("TsavoriteKV")]
        public void A_marked_checkpoint_opens_under_the_reader_before_the_mark_and_a_torn_mark_is_no_mark()
        {
            var info = new IndexRecoveryInfo();
            info.Initialize(Guid.NewGuid(), 1L << 16);
            (info.num_ht_bytes, info.num_ofb_bytes, info.num_buckets, info.startLogicalAddress, info.finalLogicalAddress) = (4_194_304, 65_536, 1024, 64, 4096);
            (info.clean, info.cleanMaxAddress) = (true, 4000);
            var marked = info.ToByteArray();
            ClassicAssert.AreEqual((1L << 16, 4096L), PreMarkReader(marked), "a rolled-back binary opens a marked checkpoint and walks it");

            IndexRecoveryInfo Read(byte[] bytes)
            {
                var read = new IndexRecoveryInfo();
                using var reader = new StreamReader(new MemoryStream(bytes));
                read.Initialize(reader);
                return read;
            }
            ClassicAssert.AreEqual((true, 4000L), (Read(marked).clean, Read(marked).cleanMaxAddress), "this binary reads the mark back");

            var lines = Encoding.UTF8.GetString(marked).Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToArray();
            byte[] Bytes(IEnumerable<string> ls) => Encoding.UTF8.GetBytes(string.Join(Environment.NewLine, ls) + Environment.NewLine);
            ClassicAssert.IsFalse(Read(Bytes(lines.Take(9))).clean, "nine lines: no mark");
            ClassicAssert.IsFalse(Read(Bytes(lines.Take(11))).clean, "a mark torn before its checksum is no mark");
            ClassicAssert.IsFalse(Read(Bytes(lines.Take(10).Append("9999").Append(lines[11]))).clean, "a mark whose address does not match its checksum is no mark");
        }
    }
}
