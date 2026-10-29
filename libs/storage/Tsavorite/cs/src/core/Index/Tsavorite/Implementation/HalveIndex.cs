// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Tsavorite.core
{
    using static LogAddress;

    /// <summary>What one halving of the hash index did: whether the table halved, or the refusal that left it as it was; the bucket
    /// counts before and after; the entries carried; same-tag pairs that were one entry twice (a grow's copies) or one chain holding the
    /// other (kept once); same-tag pairs that are two chains (a collision: the halving is refused); overflow buckets before and after;
    /// and how long every operation waited at the barrier while the table was merged.</summary>
    public readonly record struct IndexHalving(bool Halved, string Refusal, long FromBuckets, long ToBuckets, long Entries, long Duplicates,
        long Suffixes, long Collisions, long OverflowBefore, long OverflowAfter, double PausedMs);

    public unsafe partial class TsavoriteBase
    {
        /// <summary>Gives back the table in <paramref name="version"/>'s slot: a halving's discarded or superseded table is not kept for a
        /// later grow to reuse (a grow re-allocates its slot anyway). A direct-VM table is retired as a grow retires one.</summary>
        internal void ReleaseIndexVersion(int version)
        {
            if (useNativeHashIndex)
            {
                var block = nativeIndex.Blocks[version];
                nativeIndex.Blocks[version] = default;
                RetireNativeIndexTable(block);
            }
            state[version] = default;
        }
    }

    public partial class TsavoriteKV<TStoreFunctions, TAllocator> : TsavoriteBase
        where TStoreFunctions : IStoreFunctions
        where TAllocator : IAllocator<TStoreFunctions>
    {
        /// <summary>Set at a resize's IN_PROGRESS_GROW barrier when it was a halving, merged (or refused) there: an operation in
        /// IN_PROGRESS_GROW has no chunk to split.</summary>
        internal bool indexHalvedAtBarrier;

        private IndexHalving lastHalving;

        /// <summary>
        /// Halve the hash index: bucket i and bucket i + N/2 become bucket i of a table half the size, with an overflow allocator of its
        /// own, and the old table and its overflow are given back. The mirror of <see cref="GrowIndexAsync"/>, under the same full
        /// barrier, but merged AT the barrier (every operation waits for the merge; <see cref="IndexHalving.PausedMs"/> says how long).
        /// A merge never puts two chains under one tag: a same-tag pair is kept once when both are one entry (a grow's copies) or one
        /// chain holds the other (walked through the in-memory log only); any other pair is a collision, and the halving is refused with
        /// the table untouched. Settle collisions first (<see cref="FindHalvingCollisions"/>, <see cref="DropEntryIfUnchanged"/>).
        /// Take a checkpoint after, for persistence.
        /// </summary>
        public async Task<IndexHalving> HalveIndexAsync()
        {
            if (epoch.ThisInstanceProtected())
                throw new TsavoriteException("Cannot use HalveIndex when using non-async sessions");
            var size = state[resizeInfo.version].size;
            if (size < 2)
                return new(false, "the table is one bucket", size, size, 0, 0, 0, 0, OverflowBucketCount, OverflowBucketCount, 0);
            if (LockedIndexBytes > 0)
                return new(false, "the table is locked resident (an offline rebuild's source)", size, size, 0, 0, 0, 0, OverflowBucketCount, OverflowBucketCount, 0);
            lastHalving = default;
            var ran = await stateMachineDriver.RunAsync(new IndexResizeSM(new IndexResizeSMTask<TStoreFunctions, TAllocator>(this, halve: true))).ConfigureAwait(false);
            return ran ? lastHalving : lastHalving with { Halved = false, Refusal = lastHalving.Refusal ?? "the resize state machine did not run" };
        }

        /// <summary>The merge, at the IN_PROGRESS_GROW barrier: no operation is in flight, every session waits in PREPARE_GROW.</summary>
        internal unsafe void HalveAtBarrier()
        {
            var clock = Stopwatch.StartNew();
            int from = resizeInfo.version, to = 1 - from;
            var oldSize = state[from].size;
            var half = oldSize / 2;
            var overflowBefore = OverflowBucketCount;
            var merged = new MallocFixedPageSize<HashBucket>();
            Initialize(to, half, sectorSize);
            long entries = 0, duplicates = 0, suffixes = 0, collisions = 0;
            string first = null;
            var partitions = (int)Math.Clamp(half >> 12, 1, Environment.ProcessorCount);
            _ = Parallel.For(0, partitions, () => new HalvingScratch(), (p, loop, scratch) =>
            {
                long start = half * p / partitions, end = half * (p + 1) / partitions;
                for (var j = start; j < end; j++)
                {
                    if (MergePair(j, half, from, to, merged, scratch) is { } named)
                        _ = Interlocked.CompareExchange(ref first, named, null);
                }
                return scratch;
            }, scratch =>
            {
                _ = Interlocked.Add(ref entries, scratch.Entries);
                _ = Interlocked.Add(ref duplicates, scratch.Duplicates);
                _ = Interlocked.Add(ref suffixes, scratch.Suffixes);
                _ = Interlocked.Add(ref collisions, scratch.Collisions);
            });
            if (collisions > 0)
            {
                ReleaseIndexVersion(to);
                merged.Dispose();
                lastHalving = new(false, $"{collisions:N0} same-tag pair(s) are two chains, first {first}; the table is as it was",
                    oldSize, oldSize, entries, duplicates, suffixes, collisions, overflowBefore, overflowBefore, clock.Elapsed.TotalMilliseconds);
                return;
            }
            var old = overflowBucketsAllocator;
            overflowBucketsAllocator = merged;
            resizeInfo.version = to;
            ReleaseIndexVersion(from);
            old.Dispose();
            lastHalving = new(true, null, oldSize, half, entries, duplicates, suffixes, 0, overflowBefore, OverflowBucketCount, clock.Elapsed.TotalMilliseconds);
        }

        private sealed class HalvingScratch
        {
            public readonly List<long> Words = new();
            public readonly int[] Stamp = new int[1 << 16], At = new int[1 << 16];
            public int Generation;
            public long Entries, Duplicates, Suffixes, Collisions;
        }

        // Merges old buckets j and j + half into new bucket j; returns a name for the first collision, else null.
        private unsafe string MergePair(long j, long half, int from, int to, MallocFixedPageSize<HashBucket> merged, HalvingScratch s)
        {
            var words = s.Words;
            words.Clear();
            if (++s.Generation == int.MaxValue) { Array.Clear(s.Stamp); s.Generation = 1; }
            string collision = null;
            for (var side = 0; side < 2; side++)
            {
                var b = state[from].tableAligned + j + (side * half);
                while (true)
                {
                    for (var slot = 0; slot < Constants.kOverflowBucketIndex; slot++)
                    {
                        HashBucketEntry entry = default;
                        entry.word = b->bucket_entries[slot];
                        if (entry.word == Constants.kInvalidEntry) continue;
                        if (s.Stamp[entry.Tag] != s.Generation)
                        {
                            s.Stamp[entry.Tag] = s.Generation;
                            s.At[entry.Tag] = words.Count;
                            words.Add(entry.word);
                            continue;
                        }
                        HashBucketEntry kept = default;
                        kept.word = words[s.At[entry.Tag]];
                        if (kept.word == entry.word) { s.Duplicates++; continue; }
                        if (!kept.IsReadCache && !entry.IsReadCache)
                        {
                            long high = Math.Max(kept.Address, entry.Address), low = Math.Min(kept.Address, entry.Address);
                            if (ChainHolds(high, low))
                            {
                                s.Suffixes++;
                                if (entry.Address == high) words[s.At[entry.Tag]] = entry.word;
                                continue;
                            }
                        }
                        s.Collisions++;
                        collision ??= $"bucket {j} tag {entry.Tag}: 0x{kept.word:X} and 0x{entry.word:X}";
                    }
                    var overflow = b->bucket_entries[Constants.kOverflowBucketIndex] & kAddressBitMask;
                    if (overflow == 0) break;
                    b = (HashBucket*)overflowBucketsAllocator.GetPhysicalAddress(overflow);
                }
            }
            s.Entries += words.Count;
            var into = (long*)(state[to].tableAligned + j);
            var end = into + Constants.kOverflowBucketIndex;
            foreach (var word in words)
            {
                if (into == end)
                {
                    var page = merged.Allocate();
                    *into = page;
                    into = (long*)merged.GetPhysicalAddress(page);
                    end = into + Constants.kOverflowBucketIndex;
                }
                *into++ = word;
            }
            return collision;
        }

        // Whether the chain from main-log address high passes through low, walking only records still in memory.
        private unsafe bool ChainHolds(long high, long low)
        {
            var head = hlogBase.HeadAddress;
            while (high > low && high >= head)
                high = LogRecord.GetInfo(hlogBase.GetPhysicalAddress(high)).PreviousAddress;
            return high == low;
        }
    }
}
