// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace Tsavorite.core
{
    /// <summary>One bounded step of a read-only count: the overflow buckets the table's chains reach, the overflow buckets their live entries
    /// need packed, the live entries, and the entries below begin a compaction drops. Next and Refused mean as a scrub step's.</summary>
    public readonly record struct IndexOverflowStep(long Reached, long Needed, long Entries, long BelowBegin, long Next, bool Done, bool Refused);

    /// <summary>What one overflow compaction did: whether the table's chains were copied onto a fresh overflow allocator, or the refusal that
    /// left it as it was; overflow buckets before (the allocator's high water) and after; the entries carried; the entries below begin
    /// dropped; and how long every operation waited at the barrier.</summary>
    public readonly record struct IndexOverflowCompaction(bool Compacted, string Refusal, long OverflowBefore, long OverflowAfter, long Entries, long BelowBegin, double PausedMs);

    public partial class TsavoriteKV<TStoreFunctions, TAllocator> : TsavoriteBase
        where TStoreFunctions : IStoreFunctions
        where TAllocator : IAllocator<TStoreFunctions>
    {
        private IndexOverflowCompaction lastOverflowCompaction;

        /// <summary>
        /// One bounded step of a read-only walk over buckets [<paramref name="from"/>, <paramref name="from"/> + <paramref name="buckets"/>):
        /// the overflow buckets their chains reach, and those the live entries need once packed. A chain never gives an overflow bucket back
        /// (OverflowBucketCount is the allocator's high water: every slot a scrub or a delete emptied stays linked),
        /// so the gap between it and Needed is what <see cref="CompactOverflowAsync"/> frees. Refused while a resize is in flight.
        /// </summary>
        public unsafe IndexOverflowStep CountOverflowReached(long from, long buckets)
        {
            var protect = !epoch.ThisInstanceProtected();
            if (protect) epoch.Resume();
            try
            {
                var phase = stateMachineDriver.SystemState.Phase;
                if (resizeInfo.status != ResizeOperationStatus.DONE || phase == Phase.PREPARE_GROW || phase == Phase.IN_PROGRESS_GROW)
                    return new(0, 0, 0, 0, from, false, true);
                var version = resizeInfo.version;
                var size = state[version].size;
                var end = Math.Min(size, Math.Max(from, 0) + buckets);
                var begin = hlogBase.BeginAddress;
                long reached = 0, needed = 0, entries = 0, below = 0;
                for (var bucket = Math.Max(from, 0); bucket < end; bucket++)
                {
                    var first = state[version].tableAligned + bucket;
                    long live = 0;
                    for (var b = first; b != null; b = NextOverflow(b))
                    {
                        if (b != first) reached++;
                        for (var slot = 0; slot < Constants.kOverflowBucketIndex; slot++)
                        {
                            var word = b->bucket_entries[slot];
                            if (word == 0) continue;
                            if (Drops(word, begin)) below++;
                            else live++;
                        }
                    }
                    entries += live;
                    needed += Math.Max(0, (live - 1) / Constants.kOverflowBucketIndex);
                }
                return new(reached, needed, entries, below, end, end >= size, false);
            }
            finally
            {
                if (protect) epoch.Suspend();
            }
        }

        /// <summary>
        /// Copies every bucket's chain, its entries packed and empty slots dropped, into a table of the same size with an overflow allocator
        /// of its own; the old table and its overflow are given back. An entry below begin is dropped, as a scrub clears it: it names nothing
        /// a read reaches. Under the resize's full barrier, copied AT the barrier (every operation waits; <see cref="IndexOverflowCompaction.PausedMs"/>
        /// says how long). No live entry changes, so no read does. Take a checkpoint after, for persistence.
        /// </summary>
        public async Task<IndexOverflowCompaction> CompactOverflowAsync()
        {
            if (epoch.ThisInstanceProtected())
                throw new TsavoriteException("Cannot use CompactOverflow when using non-async sessions");
            if (LockedIndexBytes > 0)
                return new(false, "the table is locked resident (an offline rebuild's source)", OverflowBucketCount, OverflowBucketCount, 0, 0, 0);
            lastOverflowCompaction = default;
            var ran = await stateMachineDriver.RunAsync(new IndexResizeSM(new IndexResizeSMTask<TStoreFunctions, TAllocator>(this, compact: true))).ConfigureAwait(false);
            return ran ? lastOverflowCompaction : lastOverflowCompaction with { Compacted = false, Refusal = lastOverflowCompaction.Refusal ?? "the resize state machine did not run" };
        }

        /// <summary>The copy, at the IN_PROGRESS_GROW barrier: no operation is in flight, every session waits in PREPARE_GROW.</summary>
        internal unsafe void CompactOverflowAtBarrier()
        {
            var clock = Stopwatch.StartNew();
            int from = resizeInfo.version, to = 1 - from;
            var size = state[from].size;
            var before = OverflowBucketCount;
            var begin = hlogBase.BeginAddress;
            var fresh = new MallocFixedPageSize<HashBucket>();
            Initialize(to, size, sectorSize);
            long entries = 0, below = 0;
            for (long j = 0; j < size; j++)
            {
                var into = (long*)(state[to].tableAligned + j);
                var end = into + Constants.kOverflowBucketIndex;
                for (var b = state[from].tableAligned + j; b != null; b = NextOverflow(b))
                    for (var slot = 0; slot < Constants.kOverflowBucketIndex; slot++)
                    {
                        var word = b->bucket_entries[slot];
                        if (word == 0) continue;
                        if (Drops(word, begin)) { below++; continue; }
                        if (into == end)
                        {
                            var page = fresh.Allocate();
                            *into = page;
                            into = (long*)fresh.GetPhysicalAddress(page);
                            end = into + Constants.kOverflowBucketIndex;
                        }
                        *into++ = word;
                        entries++;
                    }
            }
            var old = overflowBucketsAllocator;
            overflowBucketsAllocator = fresh;
            resizeInfo.version = to;
            ReleaseIndexVersion(from);
            old.Dispose();
            lastOverflowCompaction = new(true, null, before, OverflowBucketCount, entries, below, clock.Elapsed.TotalMilliseconds);
        }

        // An entry the scrub clears (ScrubEntriesBelowBegin): a main-log word below begin, not read-cache, tentative or a reserved tag's.
        private static bool Drops(long word, long begin)
        {
            HashBucketEntry entry = default;
            entry.word = word;
            return !entry.IsReadCache && !entry.Tentative && entry.Address < begin && entry.Address != LogAddress.kTempInvalidAddress;
        }
    }
}
