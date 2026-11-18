// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System.Threading;

namespace Tsavorite.core
{
    /// <summary>Fixes a copied run of an index checkpoint's buckets before it is written; <paramref name="live"/> is the same run in the table.</summary>
    internal unsafe delegate void FixCheckpointBuckets(HashBucket* copy, HashBucket* live, long buckets);

    /// <summary>What the last clean index checkpoint cleared and the highest log address any of its entries names.</summary>
    public readonly record struct CleanIndexCheckpointCensus(bool Clean, long Tentative, long Misaligned, long MaxAddress);

    public partial class TsavoriteBase
    {
        /// <summary>When set, an index checkpoint writes the table as recovery would leave it (no tentative entry, no bucket lock bit,
        /// no misaligned link, no read-cache address), and its metadata says so with the highest address it names. A recovery from such
        /// a checkpoint skips the passes over every bucket that make it so; a checkpoint without the mark is walked as before.</summary>
        public bool WritesCleanIndexCheckpoints { get; set; }

        /// <summary>The census of the last index checkpoint written with <see cref="WritesCleanIndexCheckpoints"/>.</summary>
        public CleanIndexCheckpointCensus LastCleanIndexCheckpoint { get; internal set; }

        /// <summary>Whether the last recovery took its index checkpoint's clean mark and skipped the tentative and scrub passes.</summary>
        public bool LastRecoveryTrustedCleanMark { get; internal set; }

        /// <summary>Copies an index checkpoint has issued and not yet written: at most this many, so a clean checkpoint holds a bounded
        /// buffer, never a second table.</summary>
        internal const int CheckpointCopiesInFlight = 4;

        long cleanMaxAddress, cleanTentative, cleanMisaligned;
        int cleanRefused;
        internal bool cleanArmed;   // as this checkpoint's copies were fixed: a switch flipped mid-checkpoint marks nothing it did not clean

        internal void BeginCleanCheckpoint(bool armed) => (cleanArmed, cleanMaxAddress, cleanTentative, cleanMisaligned, cleanRefused) = (armed, 0, 0, 0, 0);

        /// <summary>The census as the metadata write records it: clean only when every copied bucket was cleaned and none was refused.</summary>
        internal CleanIndexCheckpointCensus EndCleanCheckpoint()
            => LastCleanIndexCheckpoint = new(Volatile.Read(ref cleanRefused) == 0, Interlocked.Read(ref cleanTentative), Interlocked.Read(ref cleanMisaligned),
                Interlocked.Read(ref cleanMaxAddress));

        /// <summary>Recovery's two bucket passes, run on a copy before it is written: tentative entries and misaligned links are cleared,
        /// the overflow word keeps only its address. A read-cache entry left in the copy refuses the mark, since its chain is not in the file.</summary>
        internal unsafe void CleanCheckpointBuckets(HashBucket* copy, long buckets)
        {
            long max = 0, tentative = 0, misaligned = 0;
            var refused = false;
            HashBucketEntry entry = default;
            for (long i = 0; i < buckets; i++)
            {
                var b = copy + i;
                for (int e = 0; e < Constants.kOverflowBucketIndex; e++)
                {
                    entry.word = b->bucket_entries[e];
                    if (entry.word == 0) continue;
                    if (entry.Tentative) { b->bucket_entries[e] = 0; tentative++; continue; }
                    if (entry.IsReadCache) { refused = true; continue; }
                    var address = LogAddress.AbsoluteAddress(entry.Address);
                    if (address > LogAddress.kTempInvalidAddress && (address & (Constants.kRecordAlignment - 1)) != 0) { b->bucket_entries[e] = 0; misaligned++; continue; }
                    if (address > max) max = address;
                }
                b->bucket_entries[Constants.kOverflowBucketIndex] &= (long)LogAddress.kAddressBitMask;
            }
            if (refused) Volatile.Write(ref cleanRefused, 1);
            _ = Interlocked.Add(ref cleanTentative, tentative);
            _ = Interlocked.Add(ref cleanMisaligned, misaligned);
            for (var seen = Interlocked.Read(ref cleanMaxAddress); max > seen; seen = Interlocked.Read(ref cleanMaxAddress))
                if (Interlocked.CompareExchange(ref cleanMaxAddress, max, seen) == seen) break;
        }
    }

    public partial class TsavoriteKV<TStoreFunctions, TAllocator> : TsavoriteBase
        where TStoreFunctions : IStoreFunctions
        where TAllocator : IAllocator<TStoreFunctions>
    {
        /// <summary>The fix an index checkpoint applies to each copy: none (the table is written as it stands), the read-cache skip, or the
        /// clean pass after it.</summary>
        internal unsafe FixCheckpointBuckets CheckpointFix(bool clean)
            => clean ? FixClean : UseReadCache ? SkipReadCacheBuckets : null;

        unsafe void SkipReadCacheBuckets(HashBucket* copy, HashBucket* live, long buckets)
        {
            for (long i = 0; i < buckets; i++)
                SkipReadCacheBucket(copy + i, live + i);
        }

        unsafe void FixClean(HashBucket* copy, HashBucket* live, long buckets)
        {
            if (UseReadCache) SkipReadCacheBuckets(copy, live, buckets);
            CleanCheckpointBuckets(copy, buckets);
        }
    }
}
