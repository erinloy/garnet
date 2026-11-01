// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System.Threading;

namespace Tsavorite.core
{
    using static LogAddress;

    public partial class TsavoriteKV<TStoreFunctions, TAllocator> : TsavoriteBase
        where TStoreFunctions : IStoreFunctions
        where TAllocator : IAllocator<TStoreFunctions>
    {
        long halvingReadCacheSplices;

        /// <summary>Same-tag pair sides whose read-cache prefix a halving join dropped, so the join could read their main-log chain.</summary>
        public long HalvingReadCacheSplices => Interlocked.Read(ref halvingReadCacheSplices);

        /// <summary>
        /// The word an entry holds once its read-cache prefix is dropped: the same tag, at its chain's first main-log address. False when a record of the
        /// prefix lies below the read cache's head: its page's close is in flight, and the close unlinks it (the next ask finds the entry moved).
        /// The caller holds the epoch, so a read-cache address at or above the head stays readable while it is walked.
        /// </summary>
        private bool TryMainLogWord(long word, out long main)
        {
            HashBucketEntry entry = default;
            entry.word = word;
            main = word;
            if (!entry.IsReadCache)
                return true;
            if (entry.Tentative)
                return false;
            var address = entry.Address;
            while (IsReadCache(address))
            {
                if (AbsoluteAddress(address) < readcacheBase.HeadAddress)
                    return false;
                address = LogRecord.GetInfo(readcacheBase.GetPhysicalAddress(address)).PreviousAddress;
            }
            HashBucketEntry kept = default;
            kept.Tag = entry.Tag;
            kept.Address = address;
            main = kept.word;
            return true;
        }

        /// <summary>
        /// Drops a pair side's read-cache prefix the way a writer's detach does (CASRecordIntoChain): the entry swaps by CAS from the word the pre-scan
        /// observed to the same tag at the chain's first main-log address. The dropped copies stay on their page until ReadCacheEvict closes it, and a
        /// reader that already holds the old word walks them down to that same address. False when the entry moved (a read cached a key, a writer
        /// wrote one) or a prefix record's close is in flight: the next ask names the pair again if it stands.
        /// </summary>
        private unsafe bool TrySpliceReadCachePrefix(HashBucket* bucket, long word, out long main)
        {
            if (!TryMainLogWord(word, out main))
                return false;
            if (main == word)
                return true;
            var slot = EntryHolding(bucket, word);
            if (slot == null || Interlocked.CompareExchange(ref *slot, main, word) != word)
                return false;
            _ = Interlocked.Increment(ref halvingReadCacheSplices);
            return true;
        }

        // Both sides of a pair, spliced; the pair as its main-log words. Under the caller's epoch.
        private unsafe bool TrySplicePair(HashBucket* low, HashBucket* high, ref IndexTagCollision pair)
        {
            if (!TrySpliceReadCachePrefix(low, pair.LowWord, out var lowWord) || !TrySpliceReadCachePrefix(high, pair.HighWord, out var highWord))
                return false;
            pair = pair with { LowWord = lowWord, HighWord = highWord };
            return true;
        }
    }
}
