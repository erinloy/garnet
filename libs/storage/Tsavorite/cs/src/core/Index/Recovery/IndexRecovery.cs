// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Tsavorite.core
{
    /// <summary/>
    public partial class TsavoriteBase
    {
        internal ICheckpointManager checkpointManager;
        internal bool disposeCheckpointManager;

        /// <summary>
        /// CheckpointManager
        /// </summary>
        public ICheckpointManager CheckpointManager => checkpointManager;

        // Derived class exposed API
        internal async ValueTask RecoverFuzzyIndexAsync(IndexCheckpointInfo info, CancellationToken cancellationToken)
        {
            var phase = System.Diagnostics.Stopwatch.StartNew();
            ulong alignedIndexSize = InitializeMainIndexRecovery(ref info, isAsync: true);
            var mainIssueMs = phase.ElapsedMilliseconds;
            await recoveryCountdown.WaitAsync(cancellationToken).ConfigureAwait(false);
            var mainReadMs = phase.ElapsedMilliseconds;
            await overflowBucketsAllocator.RecoverAsync(info.main_ht_device, alignedIndexSize, info.info.num_buckets, info.info.num_ofb_bytes, cancellationToken).ConfigureAwait(false);
            var overflowMs = phase.ElapsedMilliseconds;
            FinalizeMainIndexRecovery(info);
            LastIndexRecoveryParts = new(mainIssueMs, mainReadMs - mainIssueMs, overflowMs - mainReadMs, phase.ElapsedMilliseconds - overflowMs);
        }

        private ulong InitializeMainIndexRecovery(ref IndexCheckpointInfo info, bool isAsync)
        {
            var token = info.info.token;
            var ht_version = resizeInfo.version;

            // Create devices to read from using Async API
            info.main_ht_device = checkpointManager.GetIndexDevice(token);
            var sectorSize = info.main_ht_device.SectorSize;

            if (state[ht_version].size != info.info.table_size)
            {
                // This will allocate over any existing table for this version and initialize the new table size
                Initialize(info.info.table_size, (int)sectorSize);
            }

            BeginMainIndexRecovery(ht_version, info.main_ht_device, info.info.num_ht_bytes, isAsync);

            var alignedIndexSize = (info.info.num_ht_bytes + (sectorSize - 1)) & ~((ulong)sectorSize - 1);
            return alignedIndexSize;
        }

        private void FinalizeMainIndexRecovery(IndexCheckpointInfo info)
        {
            // close index checkpoint files appropriately
            info.main_ht_device.Dispose();

            // Delete all tentative entries!
            DeleteTentativeEntries();
        }

        // Test-only
        internal async ValueTask RecoverFuzzyIndexAsync(int ht_version, IDevice device, ulong num_ht_bytes, IDevice ofbdevice, int num_buckets, ulong num_ofb_bytes, CancellationToken cancellationToken)
        {
            BeginMainIndexRecovery(ht_version, device, num_ht_bytes, isAsync: true);
            await recoveryCountdown.WaitAsync(cancellationToken).ConfigureAwait(false);
            var sectorSize = device.SectorSize;
            var alignedIndexSize = (num_ht_bytes + (sectorSize - 1)) & ~((ulong)sectorSize - 1);
            await overflowBucketsAllocator.RecoverAsync(ofbdevice, alignedIndexSize, num_buckets, num_ofb_bytes, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Main Index Recovery Functions
        /// </summary>
        private protected CountdownWrapper recoveryCountdown;

        private unsafe void BeginMainIndexRecovery(
                                int version,
                                IDevice device,
                                ulong num_bytes,
                                bool isAsync = false)
        {
            long totalSize = state[version].size * sizeof(HashBucket);

            int numChunks = 1;
            if (totalSize > uint.MaxValue)
            {
                numChunks = (int)Math.Ceiling((double)totalSize / (long)uint.MaxValue);
                numChunks = (int)Math.Pow(2, Math.Ceiling(Math.Log(numChunks, 2)));
            }

            uint chunkSize = (uint)(totalSize / numChunks);
            recoveryCountdown = new CountdownWrapper(numChunks, isAsync);
            HashBucket* start = state[version].tableAligned;

            ulong numBytesRead = 0;
            for (int index = 0; index < numChunks; index++)
            {
                IntPtr chunkStartBucket = (IntPtr)(((byte*)start) + (index * chunkSize));
                HashIndexPageAsyncReadResult result = default;
                result.chunkIndex = index;
                device.ReadAsync(numBytesRead, chunkStartBucket, chunkSize, AsyncPageReadCallback, result);
                numBytesRead += chunkSize;
            }
            Debug.Assert(numBytesRead == num_bytes);
        }

        private unsafe void AsyncPageReadCallback(uint errorCode, uint numBytes, object overlap)
        {
            if (errorCode != 0)
                logger?.LogError($"{nameof(AsyncPageReadCallback)} error: {{errorCode}}", errorCode);
            recoveryCountdown.Decrement();
        }

        /// <summary>Where the last recovery's time went: the index read (and scrub), the log replay from the index's start to the log
        /// checkpoint's end (and how many log bytes that was), and the post-recovery flush. Zero until a recovery has run.</summary>
        public RecoveryPhases LastRecoveryPhases { get; internal set; }

        /// <summary>The last fuzzy index read split: allocating and issuing the main table, its read, the overflow buckets, and the tentative-entry pass.</summary>
        public IndexRecoveryParts LastIndexRecoveryParts { get; internal set; }

        /// <summary>The last recovery's scrub pass over every bucket, in milliseconds.</summary>
        public long LastScrubMs { get; internal set; }

        internal long recoveryScrubbedEntries;

        /// <summary>Hash entries the last recovery cleared at or past its log's final address: each was the head of a key's chain, so
        /// nonzero means older records of those keys are left in the log with no index entry (a mismatched checkpoint pair).</summary>
        public long RecoveryScrubbedEntries => Interlocked.Read(ref recoveryScrubbedEntries);

        internal long recoveryMisalignedEntries;

        /// <summary>Hash entries the last recovery cleared because their address is off the record alignment: no record starts there, so
        /// each was a corrupt link and every read of its tag's chain ended at it as not found (mesh broker 10-15..17: log:6627540995).</summary>
        public long RecoveryMisalignedEntries => Interlocked.Read(ref recoveryMisalignedEntries);

        /// <summary>The first misaligned entries the last recovery cleared, by bucket, tag and the address they named: where to look for
        /// the records their chains held.</summary>
        public System.Collections.Generic.IReadOnlyList<(long Bucket, ushort Tag, long Address)> LastMisalignedEntries { get; internal set; } = [];
        private const int MisalignedNamed = 16;

        /// <summary>
        /// DEFENCE IN DEPTH AT RECOVERY: clears every main-log hash entry whose address is at or beyond
        /// <paramref name="logFinalAddress"/>, the recovered log's final address, and returns how many it cleared. No such entry
        /// can name a record the recovered log holds - recovery replays the log only up to that address - so an entry past it
        /// is a pointer into bytes this store will never have: a point read of its key would issue a device read past what
        /// the log contains. Recovery pairs an index checkpoint only with a log checkpoint at or past it, so a consistent pair
        /// has none; an index snapshot that disagrees with its log (a mismatched or partially restored pair) is exactly what
        /// this catches. Read-cache entries are not log addresses and are left alone. One in-memory pass over the buckets
        /// recovery has just loaded - the same shape as <see cref="DeleteTentativeEntries"/>.
        /// </summary>
        internal unsafe long ScrubEntriesAtOrBeyond(long logFinalAddress)
        {
            long scrubbed = 0, misaligned = 0;
            var named = new System.Collections.Concurrent.ConcurrentQueue<(long Bucket, ushort Tag, long Address)>();
            int version = resizeInfo.version;
            var table_size_ = state[version].size;
            var table = (nint)state[version].tableAligned;
            var parts = BucketPartitions(table_size_);
            Parallel.For(0, parts, part =>
            {
                long partScrubbed = 0, partMisaligned = 0;
                HashBucketEntry entry = default;
                for (long bucket = table_size_ * part / parts, end = table_size_ * (part + 1) / parts; bucket < end; bucket++)
                {
                HashBucket* b = (HashBucket*)table + bucket;
                while (true)
                {
                    for (int bucket_entry = 0; bucket_entry < Constants.kOverflowBucketIndex; bucket_entry++)
                    {
                        entry.word = b->bucket_entries[bucket_entry];
                        if (entry.word == 0 || entry.IsReadCache)
                            continue;
                        var address = LogAddress.AbsoluteAddress(entry.Address);
                        if (address != LogAddress.kInvalidAddress && address >= logFinalAddress)
                        {
                            b->bucket_entries[bucket_entry] = 0;
                            partScrubbed++;
                        }
                        else if ((address & (Constants.kRecordAlignment - 1)) != 0)
                        {
                            if (named.Count < MisalignedNamed) named.Enqueue((bucket, entry.Tag, address));
                            b->bucket_entries[bucket_entry] = 0;
                            partMisaligned++;
                        }
                    }
                    var overflow = b->bucket_entries[Constants.kOverflowBucketIndex] & (long)LogAddress.kAddressBitMask;
                    if (overflow == 0) break;
                    b = (HashBucket*)overflowBucketsAllocator.GetPhysicalAddress(overflow);
                }
                }
                _ = Interlocked.Add(ref scrubbed, partScrubbed);
                _ = Interlocked.Add(ref misaligned, partMisaligned);
            });
            recoveryMisalignedEntries = misaligned;
            LastMisalignedEntries = [.. System.Linq.Enumerable.OrderBy(named, n => n.Bucket)];
            return scrubbed;
        }

        // Each bucket's chain (the bucket and its overflow buckets) is its own, so a pass over the table runs by bucket ranges in
        // parallel: on the mesh broker's 33.5M buckets + 29.5M overflow, each serial pass took 10 s of a 29 s open.
        private static int BucketPartitions(long tableSize) => (int)Math.Clamp(tableSize >> 16, 1, Environment.ProcessorCount);

        internal unsafe void DeleteTentativeEntries()
        {
            int version = resizeInfo.version;
            var table_size_ = state[version].size;
            var table = (nint)state[version].tableAligned;
            var parts = BucketPartitions(table_size_);
            Parallel.For(0, parts, part =>
            {
            HashBucketEntry entry = default;
            for (long bucket = table_size_ * part / parts, end = table_size_ * (part + 1) / parts; bucket < end; bucket++)
            {
                HashBucket* b = (HashBucket*)table + bucket;
                while (true)
                {
                    for (int bucket_entry = 0; bucket_entry < Constants.kOverflowBucketIndex; bucket_entry++)
                    {
                        entry.word = b->bucket_entries[bucket_entry];
                        if (entry.Tentative)
                            b->bucket_entries[bucket_entry] = 0;
                    }
                    // Reset any ephemeral bucket level locks
                    b->bucket_entries[Constants.kOverflowBucketIndex] &= (long)LogAddress.kAddressBitMask;
                    if (b->bucket_entries[Constants.kOverflowBucketIndex] == 0) break;
                    b = (HashBucket*)overflowBucketsAllocator.GetPhysicalAddress(b->bucket_entries[Constants.kOverflowBucketIndex]);
                }
            }
            });
        }
    }
    /// <summary>The time a recovery spent by phase; see <see cref="TsavoriteBase.LastRecoveryPhases"/>.</summary>
    public readonly record struct RecoveryPhases(long IndexMs, long ReplayMs, long ReplayBytes, long PostMs);

    /// <summary>The fuzzy index read by part; see <see cref="TsavoriteBase.LastIndexRecoveryParts"/>.</summary>
    public readonly record struct IndexRecoveryParts(long AllocateAndIssueMs, long MainReadMs, long OverflowMs, long TentativePassMs);
}
