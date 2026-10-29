// Copyright (c) Microsoft Corporation. Licensed under the MIT license.

using System;
using System.Collections.Generic;
using System.Threading;

namespace Tsavorite.core
{
    /// <summary>Two chains a halving would put in one bucket under one tag: bucket <see cref="Low"/> and its sibling
    /// <see cref="Low"/> + half hold entries with this tag naming different chains. Neither can be merged into the other blind.</summary>
    public readonly record struct IndexTagCollision(long Low, ushort Tag, long LowWord, long HighWord);

    public partial class TsavoriteKV<TStoreFunctions, TAllocator> : TsavoriteBase
        where TStoreFunctions : IStoreFunctions
        where TAllocator : IAllocator<TStoreFunctions>
    {
        /// <summary>
        /// One bounded step of a halving's pre-pass: for buckets i in [<paramref name="from"/>, <paramref name="from"/> +
        /// <paramref name="buckets"/>) of the lower half, names every tag that bucket i and bucket i + half both hold with different
        /// words (at most <paramref name="cap"/>; the step stops early when full, and <see cref="IndexScrubStep.Next"/> says where).
        /// Equal words are not a collision: a grow copied a below-head entry into both halves, and a merge keeps it once. Reads
        /// only; refused while a resize is in flight.
        /// </summary>
        public unsafe IndexScrubStep FindHalvingCollisions(long from, long buckets, List<IndexTagCollision> into, int cap)
        {
            var protect = !epoch.ThisInstanceProtected();
            if (protect) epoch.Resume();
            try
            {
                var phase = stateMachineDriver.SystemState.Phase;
                if (resizeInfo.status != ResizeOperationStatus.DONE || phase == Phase.PREPARE_GROW || phase == Phase.IN_PROGRESS_GROW)
                    return new(0, 0, from, false, true);
                var version = resizeInfo.version;
                var half = state[version].size / 2;
                var end = Math.Min(half, Math.Max(from, 0) + buckets);
                long found = 0;
                var bucket = Math.Max(from, 0);
                for (; bucket < end && into.Count < cap; bucket++)
                {
                    var high = state[version].tableAligned + bucket + half;
                    var low = state[version].tableAligned + bucket;
                    for (var b = high; b != null; b = NextOverflow(b))
                        for (var slot = 0; slot < Constants.kOverflowBucketIndex; slot++)
                        {
                            HashBucketEntry entry = default;
                            entry.word = b->bucket_entries[slot];
                            if (entry.word == 0 || entry.Tentative)
                                continue;
                            var other = WordOfTag(low, entry.Tag);
                            if (other != 0 && other != entry.word)
                            {
                                into.Add(new(bucket, entry.Tag, other, entry.word));
                                found++;
                            }
                        }
                }
                return new(found, 0, bucket, bucket >= half, false);
            }
            finally
            {
                if (protect) epoch.Suspend();
            }
        }

        /// <summary>
        /// Clears the entry of <paramref name="tag"/> in bucket <paramref name="bucket"/> (or its overflow) if it still holds
        /// <paramref name="word"/>: the caller has proven every record of that chain is held elsewhere (its cold tier), so a read of
        /// any of its keys that misses here is answered there. A CAS: if a writer moved the chain's head since the proof, the entry
        /// is left and false says so - the proof was of a chain that no longer is. Refused (false) while a resize is in flight.
        /// </summary>
        public unsafe bool DropEntryIfUnchanged(long bucket, ushort tag, long word)
        {
            var protect = !epoch.ThisInstanceProtected();
            if (protect) epoch.Resume();
            try
            {
                var phase = stateMachineDriver.SystemState.Phase;
                if (resizeInfo.status != ResizeOperationStatus.DONE || phase == Phase.PREPARE_GROW || phase == Phase.IN_PROGRESS_GROW)
                    return false;
                var version = resizeInfo.version;
                if (bucket < 0 || bucket >= state[version].size)
                    return false;
                for (var b = state[version].tableAligned + bucket; b != null; b = NextOverflow(b))
                    for (var slot = 0; slot < Constants.kOverflowBucketIndex; slot++)
                    {
                        HashBucketEntry entry = default;
                        entry.word = b->bucket_entries[slot];
                        if (entry.word == word && entry.Tag == tag && !entry.Tentative)
                            return Interlocked.CompareExchange(ref b->bucket_entries[slot], 0, word) == word;
                    }
                return false;
            }
            finally
            {
                if (protect) epoch.Suspend();
            }
        }

        private unsafe HashBucket* NextOverflow(HashBucket* b)
        {
            var overflow = b->bucket_entries[Constants.kOverflowBucketIndex] & LogAddress.kAddressBitMask;
            return overflow == 0 ? null : (HashBucket*)overflowBucketsAllocator.GetPhysicalAddress(overflow);
        }

        private unsafe long WordOfTag(HashBucket* b, ushort tag)
        {
            for (; b != null; b = NextOverflow(b))
                for (var slot = 0; slot < Constants.kOverflowBucketIndex; slot++)
                {
                    HashBucketEntry entry = default;
                    entry.word = b->bucket_entries[slot];
                    if (entry.word != 0 && !entry.Tentative && entry.Tag == tag)
                        return entry.word;
                }
            return 0;
        }
    }
}
