// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System.Threading;
using Microsoft.Extensions.Logging;

namespace Tsavorite.core
{
    using static LogAddress;

    /// <summary>The operation whose head CAS met an emptied slot.</summary>
    internal enum ZeroCasSite { Upsert, Delete, Rmw, CopyToTail, ReadCacheCopy }

    public unsafe partial class TsavoriteKV<TStoreFunctions, TAllocator> : TsavoriteBase
        where TStoreFunctions : IStoreFunctions
        where TAllocator : IAllocator<TStoreFunctions>
    {
        /// <summary>
        /// Lost CASes in one operation after which its retry refreshes the epoch: an insert holds its bucket's latch, so what moves the
        /// entry under it takes none, and an eviction in flight finishes on a refresh. One router upsert lost ~36M in a row (Ziltch, 10-20).
        /// </summary>
        internal const int CasLossesBeforeRefresh = 8;

        private long casLostEvicted, casLostPromoted, casLostOther, casPastBound;
        private readonly long[] zeroCasRefused = new long[(int)ZeroCasSite.ReadCacheCopy + 1];

        /// <summary>Test seam: runs after a lock helper found the key's tag and before it reads the slot's current word.</summary>
        internal System.Action<HashEntryInfo> tagFoundBeforeReadForTest;

        /// <summary>Test seam: runs in a copy to the tail after its record is written and before its head CAS.</summary>
        internal System.Action<HashEntryInfo> copyBeforeCasForTest;

        private void NoteZeroCasRefused(ZeroCasSite site) => _ = Interlocked.Increment(ref zeroCasRefused[(int)site]);

        /// <summary>
        /// Head CASes refused because the found tag's slot had been emptied under the operation (see <see cref="HashEntryInfo"/>.TryCAS), by
        /// operation: main-log inserts retry and find the tag again; a read-cache copy is abandoned.
        /// </summary>
        public (long Upsert, long Delete, long Rmw, long CopyToTail, long ReadCacheCopy) ZeroCasRefusals
            => (Interlocked.Read(ref zeroCasRefused[0]), Interlocked.Read(ref zeroCasRefused[1]), Interlocked.Read(ref zeroCasRefused[2]),
                Interlocked.Read(ref zeroCasRefused[3]), Interlocked.Read(ref zeroCasRefused[4]));
        private static int warnedPromoted;

        /// <summary>
        /// This store's lost insert CASes by what moved the hash entry: <c>Evicted</c> (the head moved down its own chain, a read-cache
        /// unlink), <c>Promoted</c> (a read-cache record was put at the head under the latch, a defect), <c>Other</c> (anything else); and
        /// operations that lost <see cref="CasLossesBeforeRefresh"/> in a row and refreshed the epoch.
        /// </summary>
        public (long Evicted, long Promoted, long Other, long PastBound) LostCases
            => (Interlocked.Read(ref casLostEvicted), Interlocked.Read(ref casLostPromoted), Interlocked.Read(ref casLostOther), Interlocked.Read(ref casPastBound));

        private OperationStatus RetryAfterLostCas<TInput, TOutput, TContext>(ref OperationState<TInput, TOutput, TContext> operationState,
                ref OperationStackContext<TStoreFunctions, TAllocator> stackCtx)
        {
            var (lost, now) = (stackCtx.hei.Address, stackCtx.hei.CurrentAddress);
            if (IsReadCache(lost) && (IsReadCache(now) ? AbsoluteAddress(now) < AbsoluteAddress(lost) : now == stackCtx.recSrc.LatestLogicalAddress))
                _ = Interlocked.Increment(ref casLostEvicted);
            else if (IsReadCache(now))
            {
                _ = Interlocked.Increment(ref casLostPromoted);
                if (Interlocked.Exchange(ref warnedPromoted, 1) == 0)
                    logger?.LogWarning("A read-cache record took the head of hash {Hash:X16} under an insert's latch: the insert read {Lost:X}, the entry holds {Now:X}.",
                        stackCtx.hei.hash, lost, now);
            }
            else
                _ = Interlocked.Increment(ref casLostOther);

            if (++operationState.casLosses < CasLossesBeforeRefresh)
                return OperationStatus.RETRY_NOW;   // CAS failure does not require epoch refresh
            operationState.casLosses = 0;
            _ = Interlocked.Increment(ref casPastBound);
            return OperationStatus.RETRY_LATER;
        }
    }
}
