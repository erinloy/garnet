// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System.Runtime.CompilerServices;
using System.Threading;

namespace Tsavorite.core
{
    public unsafe partial class TsavoriteKV<TStoreFunctions, TAllocator> : TsavoriteBase
        where TStoreFunctions : IStoreFunctions
        where TAllocator : IAllocator<TStoreFunctions>
    {
        /// <summary>
        /// The read cache's second chance (ReadCacheSettings.SecondChanceFraction): a hit below the read cache's SafeReadOnlyAddress, its
        /// oldest fraction, is copied to the read cache's tail and the old copy is marked Invalid, so a record read again before its page
        /// closes stays in memory. Before this, the fraction only set the read-cache allocator's mutable line and no hit looked at it, so
        /// the read cache was a FIFO: a record read every second aged out with the cold ones and its next read went to the device
        /// (Ziltch broker 10-25: 10,106 of 12,709 same-address re-reads came back after 256-512 MiB of other reads, past a 256 MiB cache).
        /// Best effort like every read-cache insert: the head CAS is the only publication, and a lost CAS leaves the old copy serving.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        void PromoteReadCacheHit<TInput, TOutput, TContext, TSessionFunctionsWrapper>(in LogRecord source, TSessionFunctionsWrapper sessionFunctions,
                ref OperationState<TInput, TOutput, TContext> operationState, ref OperationStackContext<TStoreFunctions, TAllocator> stackCtx)
            where TSessionFunctionsWrapper : ISessionFunctionsWrapper<TInput, TOutput, TContext, TStoreFunctions, TAllocator>
        {
            var oldCopy = stackCtx.recSrc.PhysicalAddress;
            if (!TryCopyToReadCache(in source, sessionFunctions, ref operationState, ref stackCtx))
            {
                _ = Interlocked.Increment(ref readCachePromotionsLost);
                return;
            }
            // Still in the chain until its page closes; eviction unlinks Invalid records as it does valid ones, and readers skip it.
            // Not when clearing Valid would zero the RecordInfo: a copy cached for a key with no log record ends its chain at
            // kInvalidAddress (0), and ReadCacheEvict reads a zero RecordInfo as a page's padding (8 bytes long), so the copy was
            // never unlinked and every reader of the bucket spun on its closed page (Ziltch broker 10-26 15:20Z, 41 threads). It
            // stays Valid behind the new copy, which every reader of its key meets first; a write detaches the whole prefix.
            // By CAS, never a plain read-modify-write: the old copy sits in the cache's oldest fraction, and the evictor closing the page below
            // it unlinks that page's records by CAS on this same word (TryUpdateAddress). A plain write put the old PreviousAddress back, the
            // evicted record had already been marked out of every chain, and the closed page stayed linked (Ziltch broker 11-02 07:18Z).
            ref var oldInfo = ref LogRecord.GetInfoRef(oldCopy);
            while (true)
            {
                var seen = oldInfo;
                var invalidated = seen;
                invalidated.SetInvalid();
                if (invalidated.IsNull)
                    break;
                promotionBeforeInvalidateForTest?.Invoke();
                if (oldInfo.TryInvalidateFrom(seen))
                    break;
                _ = Interlocked.Increment(ref readCachePromotionInvalidateRetries);
            }
            _ = Interlocked.Increment(ref readCachePromotions);
        }

        long readCachePromotions, readCachePromotionsLost, readCachePromotionInvalidateRetries, readCacheCopyValidateRetries;

        /// <summary>Test seam: runs in a read-cache copy after its head CAS, between reading its RecordInfo and validating it.</summary>
        internal System.Action copyBeforeValidateForTest;

        /// <summary>Published read-cache copies whose validation met a word another writer had changed, and re-read it.</summary>
        public long ReadCacheCopyValidateRetries => Interlocked.Read(ref readCacheCopyValidateRetries);

        /// <summary>Test seam: runs in a promotion between reading the old copy's RecordInfo and clearing its Valid bit, where the evictor races.</summary>
        internal System.Action promotionBeforeInvalidateForTest;

        /// <summary>Promotions whose Invalid mark of the old copy met a word another writer had changed, and re-read it.</summary>
        public long ReadCachePromotionInvalidateRetries => Interlocked.Read(ref readCachePromotionInvalidateRetries);

        /// <summary>Read-cache hits copied to the read cache's tail by the second chance, and those whose copy lost its head CAS or
        /// allocation and kept serving from the old copy.</summary>
        public (long Promoted, long Lost) ReadCachePromotions
            => (Interlocked.Read(ref readCachePromotions), Interlocked.Read(ref readCachePromotionsLost));
    }
}
