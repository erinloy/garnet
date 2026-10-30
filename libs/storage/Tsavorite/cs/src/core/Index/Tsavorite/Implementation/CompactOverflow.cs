// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
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

    /// <summary>What one step of the in-place compaction did: whether it ran, or why not; the bucket it stopped before and whether that is
    /// the table's end; the overflow buckets it freed; the chain buckets it left at or past the line; the live entries it packed; the
    /// entries below begin it dropped; and how long every operation waited at its barrier.</summary>
    public readonly record struct IndexOverflowPack(bool Ran, string Refusal, long Next, bool Done, long Freed, long PastLine, long Entries, long BelowBegin, double PausedMs);

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

        private IndexOverflowPack lastOverflowPack;

        /// <summary>
        /// One bounded step of the in-place compaction, at the resize's full barrier, from bucket <paramref name="from"/> until its chains
        /// have walked <paramref name="visits"/> buckets (a chain is finished once begun): each chain's live entries are packed onto as few overflow buckets as they
        /// need, taken first from those below <paramref name="line"/> (the chain's own, then the allocator's free ones), and the rest are
        /// zeroed and freed; a freed bucket at or past the line is not reused, for <see cref="TrimOverflowAsync"/> gives it back. Needs no
        /// memory beyond the table's own, so a table no barrier could copy whole (the mesh's 2^25 buckets) compacts in steps. No live entry
        /// changes, so no read does. Refused while a resize is in flight. Take a checkpoint after, for persistence.
        /// </summary>
        public async Task<IndexOverflowPack> PackOverflowStepAsync(long from, long visits, long line)
        {
            if (epoch.ThisInstanceProtected())
                throw new TsavoriteException("Cannot use PackOverflowStep when using non-async sessions");
            if (LockedIndexBytes > 0)
                return new(false, "the table is locked resident (an offline rebuild's source)", from, false, 0, 0, 0, 0, 0);
            lastOverflowPack = new(false, "the resize state machine did not run", from, false, 0, 0, 0, 0, 0);
            _ = await stateMachineDriver.RunAsync(new IndexResizeSM(new IndexResizeSMTask<TStoreFunctions, TAllocator>(this,
                atBarrier: () => PackOverflowAtBarrier(from, visits, line)))).ConfigureAwait(false);
            return lastOverflowPack;
        }

        /// <summary>
        /// The in-place compaction's last barrier: every chain bucket still at or past <paramref name="line"/> (a step left it, or an
        /// operation linked it since) moves to a bucket below it that no chain reaches, and the allocator gives back every page past the line
        /// (the free list becomes every unreached bucket below it, so a bucket a pack dropped past an earlier line is not lost). Refused,
        /// with the table as it was, when the free buckets below the line are too few or the line is past the allocator's high water.
        /// </summary>
        public async Task<IndexOverflowCompaction> TrimOverflowAsync(long line)
        {
            if (epoch.ThisInstanceProtected())
                throw new TsavoriteException("Cannot use TrimOverflow when using non-async sessions");
            if (LockedIndexBytes > 0)
                return new(false, "the table is locked resident (an offline rebuild's source)", OverflowBucketCount, OverflowBucketCount, 0, 0, 0);
            lastOverflowCompaction = new(false, "the resize state machine did not run", OverflowBucketCount, OverflowBucketCount, 0, 0, 0);
            _ = await stateMachineDriver.RunAsync(new IndexResizeSM(new IndexResizeSMTask<TStoreFunctions, TAllocator>(this,
                atBarrier: () => TrimOverflowAtBarrier(line)))).ConfigureAwait(false);
            return lastOverflowCompaction;
        }

        /// <summary>
        /// The in-place compaction's last barrier with no walk (a table too large to walk at one barrier): trusts the caller's pass. The
        /// caller read <paramref name="highWater"/> (<see cref="OverflowHighWater"/>) before the pass's first step, and every step of the
        /// pass left no chain bucket past <paramref name="line"/>. Each step drains the free list of buckets past the line, so after the
        /// first one an operation links only a bucket below the line, or one past the high water. Refused, nothing trimmed, when the high
        /// water moved, so the caller packs again.
        /// </summary>
        public async Task<IndexOverflowCompaction> TrimOverflowAfterCleanPassAsync(long line, long highWater)
        {
            if (epoch.ThisInstanceProtected())
                throw new TsavoriteException("Cannot use TrimOverflow when using non-async sessions");
            lastOverflowCompaction = new(false, "the resize state machine did not run", OverflowBucketCount, OverflowBucketCount, 0, 0, 0);
            _ = await stateMachineDriver.RunAsync(new IndexResizeSM(new IndexResizeSMTask<TStoreFunctions, TAllocator>(this,
                atBarrier: () =>
                {
                    var clock = Stopwatch.StartNew();
                    var before = OverflowBucketCount;
                    if (before != highWater || line > before || !overflowBucketsAllocator.CanTrimTo(line))
                    {
                        lastOverflowCompaction = new(false, $"the high water is {before:N0}, not the pass's {highWater:N0}, or the line {line:N0} is past it, or a checkpoint is writing it", before, before, 0, 0, 0);
                        return;
                    }
                    foreach (var a in overflowBucketsAllocator.TakeFree(line)) overflowBucketsAllocator.Free(a);
                    overflowBucketsAllocator.TrimTo(line);
                    lastOverflowCompaction = new(true, null, before, OverflowBucketCount, 0, 0, clock.Elapsed.TotalMilliseconds);
                }))).ConfigureAwait(false);
            return lastOverflowCompaction;
        }

        /// <summary>The overflow allocator's high water: past it no bucket was ever handed out.</summary>
        public long OverflowHighWater => OverflowBucketCount;

        internal unsafe void PackOverflowAtBarrier(long from, long visits, long line)
        {
            var clock = Stopwatch.StartNew();
            var table = state[resizeInfo.version];
            var begin = hlogBase.BeginAddress;
            var pool = new Stack<long>(overflowBucketsAllocator.TakeFree(line));
            var words = new List<long>();
            var chain = new List<long>();
            var targets = new List<long>();
            long freed = 0, past = 0, entries = 0, below = 0, walked = 0, j = Math.Max(from, 0);
            for (; j < table.size && walked < visits; j++)
            {
                var first = table.tableAligned + j;
                words.Clear();
                chain.Clear();
                for (var b = first; ; )
                {
                    for (var slot = 0; slot < Constants.kOverflowBucketIndex; slot++)
                    {
                        var word = b->bucket_entries[slot];
                        if (word == 0) continue;
                        if (Drops(word, begin)) below++;
                        else words.Add(word);
                    }
                    walked++;
                    var next = b->bucket_entries[Constants.kOverflowBucketIndex] & LogAddress.kAddressBitMask;
                    if (next == 0) break;
                    chain.Add(next);
                    b = (HashBucket*)overflowBucketsAllocator.GetPhysicalAddress(next);
                }
                entries += words.Count;
                var need = (int)Math.Max(0, (words.Count - 1) / Constants.kOverflowBucketIndex);
                targets.Clear();
                foreach (var a in chain) if (targets.Count < need && a < line) targets.Add(a);
                while (targets.Count < need && pool.Count > 0) targets.Add(pool.Pop());
                foreach (var a in chain) if (targets.Count < need && a >= line) targets.Add(a);
                var w = 0;
                var into = first;
                for (var t = 0; ; t++)
                {
                    for (var slot = 0; slot < Constants.kOverflowBucketIndex; slot++)
                        into->bucket_entries[slot] = w < words.Count ? words[w++] : 0;
                    var link = t < targets.Count ? targets[t] : 0;
                    ref var overflow = ref into->bucket_entries[Constants.kOverflowBucketIndex];
                    overflow = (into == first ? overflow & ~LogAddress.kAddressBitMask : 0) | link;   // the first bucket's latch bits stay
                    if (link == 0) break;
                    if (link >= line) past++;
                    into = (HashBucket*)overflowBucketsAllocator.GetPhysicalAddress(link);
                }
                foreach (var a in chain)
                {
                    if (targets.Contains(a)) continue;
                    Zero(a);
                    freed++;
                    if (a < line) pool.Push(a);
                }
            }
            foreach (var a in pool) overflowBucketsAllocator.Free(a);
            lastOverflowPack = new(true, null, j, j >= table.size, freed, past, entries, below, clock.Elapsed.TotalMilliseconds);
        }

        internal unsafe void TrimOverflowAtBarrier(long line)
        {
            var clock = Stopwatch.StartNew();
            var before = OverflowBucketCount;
            if (line > before || !overflowBucketsAllocator.CanTrimTo(line))
            {
                lastOverflowCompaction = new(false, $"the line {line:N0} is not between the first block and the allocator's high water {before:N0}, or a checkpoint is writing it", before, before, 0, 0, 0);
                return;
            }
            var table = state[resizeInfo.version];
            var partitions = (int)Math.Clamp(table.size >> 12, 1, Environment.ProcessorCount);
            // the free buckets below the line are those no chain reaches: the free list alone misses what a pack dropped past an earlier line
            _ = overflowBucketsAllocator.TakeFree(line);
            var reached = new long[(line + 63) >> 6];
            _ = Parallel.For(0, partitions, p =>
            {
                long start = table.size * p / partitions, stop = table.size * (p + 1) / partitions;
                for (var j = start; j < stop; j++)
                    for (var b = table.tableAligned + j; ; )
                    {
                        var next = b->bucket_entries[Constants.kOverflowBucketIndex] & LogAddress.kAddressBitMask;
                        if (next == 0) break;
                        if (next < line) _ = Interlocked.Or(ref reached[next >> 6], 1L << (int)(next & 63));
                        b = (HashBucket*)overflowBucketsAllocator.GetPhysicalAddress(next);
                    }
            });
            var pool = new System.Collections.Concurrent.ConcurrentStack<long>();
            for (long a = overflowBucketsAllocator.FirstAllocation; a < line; a++)
                if ((reached[a >> 6] & (1L << (int)(a & 63))) == 0) pool.Push(a);
            long moved = 0, unplaced = 0;
            _ = Parallel.For(0, partitions, p =>
            {
                long start = table.size * p / partitions, stop = table.size * (p + 1) / partitions, m = 0, s = 0;
                for (var j = start; j < stop; j++)
                    for (var b = table.tableAligned + j; ; )
                    {
                        ref var overflow = ref b->bucket_entries[Constants.kOverflowBucketIndex];
                        var next = overflow & LogAddress.kAddressBitMask;
                        if (next == 0) break;
                        if (next >= line)
                        {
                            if (!pool.TryPop(out var to)) { s++; break; }
                            Buffer.MemoryCopy((void*)overflowBucketsAllocator.GetPhysicalAddress(next), (void*)overflowBucketsAllocator.GetPhysicalAddress(to), sizeof(HashBucket), sizeof(HashBucket));
                            overflow = (overflow & ~LogAddress.kAddressBitMask) | to;
                            next = to;
                            m++;
                        }
                        b = (HashBucket*)overflowBucketsAllocator.GetPhysicalAddress(next);
                    }
                _ = Interlocked.Add(ref moved, m);
                _ = Interlocked.Add(ref unplaced, s);
            });
            foreach (var a in pool) overflowBucketsAllocator.Free(a);
            if (unplaced > 0)
            {
                // the moved buckets stand (each is a whole copy, linked once), the rest stay past the line: nothing is trimmed
                lastOverflowCompaction = new(false, $"{unplaced:N0} chain bucket(s) past the line found no free bucket below it; {moved:N0} moved, nothing trimmed", before, before, moved, 0, clock.Elapsed.TotalMilliseconds);
                return;
            }
            overflowBucketsAllocator.TrimTo(line);
            lastOverflowCompaction = new(true, null, before, OverflowBucketCount, moved, 0, clock.Elapsed.TotalMilliseconds);
        }

        private unsafe void Zero(long logical)
        {
            var b = (HashBucket*)overflowBucketsAllocator.GetPhysicalAddress(logical);
            for (var slot = 0; slot < Constants.kEntriesPerBucket; slot++) b->bucket_entries[slot] = 0;
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
