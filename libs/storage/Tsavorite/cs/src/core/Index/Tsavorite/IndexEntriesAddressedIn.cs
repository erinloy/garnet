// Copyright (c) Microsoft Corporation. Licensed under the MIT license.

using System;

namespace Tsavorite.core
{
    public partial class TsavoriteKV<TStoreFunctions, TAllocator> : TsavoriteBase
        where TStoreFunctions : IStoreFunctions
        where TAllocator : IAllocator<TStoreFunctions>
    {
        /// <summary>How many hash-index entries (main and overflow buckets, read-cache entries excluded) name a log address in
        /// [<paramref name="from"/>, <paramref name="to"/>): zero says no key's chain starts there. Walks the whole table; for an offline store.</summary>
        public long IndexEntriesAddressedIn(long from, long to)
        {
            if (to <= from) return 0;
            long count = 0, lowest = 0;
            IndexEntriesAddressedIn(new ReadOnlySpan<long>(in from), new ReadOnlySpan<long>(in to), new Span<long>(ref count), new Span<long>(ref lowest));
            return count;
        }

        /// <summary>One walk of the table for many ranges: <paramref name="counts"/>[i] is how many entries name an address in
        /// [<paramref name="froms"/>[i], <paramref name="tos"/>[i]) and <paramref name="lowest"/>[i] the lowest such address, or -1 for none.
        /// The ranges are ascending, non-empty and do not overlap; anything else is refused.</summary>
        public unsafe void IndexEntriesAddressedIn(ReadOnlySpan<long> froms, ReadOnlySpan<long> tos, Span<long> counts, Span<long> lowest)
        {
            var n = froms.Length;
            if (tos.Length != n || counts.Length != n || lowest.Length != n)
                throw new ArgumentException($"{n} range start(s) with {tos.Length} end(s), {counts.Length} count(s) and {lowest.Length} lowest address(es)");
            for (var i = 0; i < n; i++)
            {
                if (tos[i] <= froms[i] || (i > 0 && froms[i] < tos[i - 1]))
                    throw new ArgumentException($"range {i} [{froms[i]}, {tos[i]}) is empty or not above the one before it");
                counts[i] = 0;
                lowest[i] = -1;
            }
            if (n == 0) return;

            var version = resizeInfo.version;
            var tableSize = state[version].size;
            var table = state[version].tableAligned;
            long first = froms[0], last = tos[n - 1];
            for (long bucket = 0; bucket < tableSize; bucket++)
            {
                var b = *(table + bucket);
                while (true)
                {
                    for (var slot = 0; slot < Constants.kOverflowBucketIndex; slot++)
                    {
                        var entry = default(HashBucketEntry);
                        entry.word = b.bucket_entries[slot];
                        if (entry.word == 0 || entry.IsReadCache) continue;
                        var address = entry.Address;
                        if (address < first || address >= last) continue;
                        // The first range that ends above the address is the only one that can hold it.
                        int lo = 0, hi = n - 1;
                        while (lo < hi)
                        {
                            var mid = (lo + hi) >> 1;
                            if (tos[mid] > address) hi = mid; else lo = mid + 1;
                        }
                        if (address < froms[lo]) continue;
                        counts[lo]++;
                        if (lowest[lo] < 0 || address < lowest[lo]) lowest[lo] = address;
                    }
                    if ((b.bucket_entries[Constants.kOverflowBucketIndex] & LogAddress.kAddressBitMask) == 0) break;
                    b = *(HashBucket*)overflowBucketsAllocator.GetPhysicalAddress(b.bucket_entries[Constants.kOverflowBucketIndex] & LogAddress.kAddressBitMask);
                }
            }
        }
    }
}
