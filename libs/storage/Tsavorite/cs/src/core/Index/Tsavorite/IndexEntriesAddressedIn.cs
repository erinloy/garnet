// Copyright (c) Microsoft Corporation. Licensed under the MIT license.

namespace Tsavorite.core
{
    public partial class TsavoriteKV<TStoreFunctions, TAllocator> : TsavoriteBase
        where TStoreFunctions : IStoreFunctions
        where TAllocator : IAllocator<TStoreFunctions>
    {
        /// <summary>How many hash-index entries (main and overflow buckets, read-cache entries excluded) name a log address in
        /// [<paramref name="from"/>, <paramref name="to"/>): zero says no key's chain starts there. Walks the whole table; for an offline store.</summary>
        public unsafe long IndexEntriesAddressedIn(long from, long to)
        {
            var version = resizeInfo.version;
            var tableSize = state[version].size;
            var table = state[version].tableAligned;
            long found = 0;
            for (long bucket = 0; bucket < tableSize; bucket++)
            {
                var b = *(table + bucket);
                while (true)
                {
                    for (var slot = 0; slot < Constants.kOverflowBucketIndex; slot++)
                    {
                        var entry = default(HashBucketEntry);
                        entry.word = b.bucket_entries[slot];
                        if (entry.word != 0 && !entry.IsReadCache && entry.Address >= from && entry.Address < to) found++;
                    }
                    if ((b.bucket_entries[Constants.kOverflowBucketIndex] & LogAddress.kAddressBitMask) == 0) break;
                    b = *(HashBucket*)overflowBucketsAllocator.GetPhysicalAddress(b.bucket_entries[Constants.kOverflowBucketIndex] & LogAddress.kAddressBitMask);
                }
            }
            return found;
        }
    }
}
