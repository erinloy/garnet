// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.Runtime.CompilerServices;

namespace Tsavorite.core
{
    using static LogAddress;

    public unsafe partial class TsavoriteKV<TStoreFunctions, TAllocator> : TsavoriteBase
        where TStoreFunctions : IStoreFunctions
        where TAllocator : IAllocator<TStoreFunctions>
    {
        /// <summary>
        /// F2's read of a cold store (section 7: "If we find a disk-resident record in either log, we first try to insert it to the read-cache"):
        /// head-insert (key, value), or the key's tombstone, into this store's READ CACHE only if the read cache holds no record of the key and
        /// this log holds none at or above <paramref name="sinceAddress"/>, the TailAddress the caller read before its own read of this log missed.
        /// Best effort, never pending: false when the read cache is off, when the check would need IO inside the bound, or when a writer moved
        /// the key's hash entry between the check and the insert. A later write of the key invalidates the copy as it does any read-cache record.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal bool ConditionalInsertReadCacheBytes<TKey, TInput, TOutput, TContext, TSessionFunctionsWrapper>(TSessionFunctionsWrapper sessionFunctions,
                TKey key, ReadOnlySpan<byte> value, bool tombstone, long sinceAddress)
            where TKey : IKey
#if NET9_0_OR_GREATER
                , allows ref struct
#endif
            where TSessionFunctionsWrapper : ISessionFunctionsWrapper<TInput, TOutput, TContext, TStoreFunctions, TAllocator>
        {
            if (!UseReadCache)
                return false;
            var buffer = ConditionalInsertImage(key, value, tombstone, sinceAddress, out _);
            try
            {
                var image = new LogRecord((long)buffer.GetValidPointer());
                var source = new TransientSourceLogRecord(DiskLogRecord.CreateFromTransientLogRecord(in image));

                // The search is exclusive of initialLatestLogicalAddress: one below the bound makes it inclusive. No initial entry: the whole read cache.
                OperationState<TInput, TOutput, TContext> operationState = default;
                operationState.initialEntryAddress = kInvalidAddress;
                operationState.initialLatestLogicalAddress = sinceAddress > 0 ? sinceAddress - 1 : kInvalidAddress;
                OperationStackContext<TStoreFunctions, TAllocator> stackCtx = new(storeFunctions.GetKeyHashCode64(key));
                while (true)
                {
                    if (!FindTagAndTryEphemeralSLock<TInput, TOutput, TContext, TSessionFunctionsWrapper>(sessionFunctions, ref stackCtx, out var status))
                    {
                        if (status == OperationStatus.NOTFOUND)
                        {
                            // A key the read cache can hold needs a tag to hang from, as a pending read's promotion has.
                            if (sessionFunctions.Ctx.phase == Phase.IN_PROGRESS_GROW)
                                SplitBuckets(stackCtx.hei.hash);
                            FindOrCreateTag(ref stackCtx.hei, hlogBase.BeginAddress);
                            continue;
                        }
                        if (HandleImmediateRetryStatus(status, sessionFunctions, ref operationState))
                            continue;
                        return false;
                    }

                    try
                    {
                        if (TryFindRecordInMemory(key, ref stackCtx, ref operationState))
                        {
                            _ = System.Threading.Interlocked.Increment(ref readCacheInsertsHeld);
                            return false;   // a read-cache copy, or a log record inside the bound: newer than the caller's answer, or the same
                        }
                        if (stackCtx.recSrc.LogicalAddress >= sinceAddress && stackCtx.recSrc.LogicalAddress >= hlogBase.BeginAddress)
                        {
                            _ = System.Threading.Interlocked.Increment(ref readCacheInsertsBelowMemory);
                            return false;   // the chain goes on below memory inside the bound: unknown without IO, so nothing is cached
                        }
                        if (TryCopyToReadCache(in source, sessionFunctions, ref operationState, ref stackCtx))
                        {
                            _ = System.Threading.Interlocked.Increment(ref readCacheInsertsLanded);
                            return true;
                        }
                        _ = System.Threading.Interlocked.Increment(ref readCacheInsertsLost);
                        return false;
                    }
                    finally
                    {
                        stackCtx.HandleNewRecordOnException(this);
                        EphemeralSUnlock<TInput, TOutput, TContext, TSessionFunctionsWrapper>(sessionFunctions, ref stackCtx);
                    }
                }
            }
            finally
            {
                buffer.Return();
            }
        }

        long readCacheInsertsLanded, readCacheInsertsHeld, readCacheInsertsBelowMemory, readCacheInsertsLost, readCacheInsertsUnderEpoch;

        internal bool RefuseReadCacheInsertUnderEpoch()
        {
            _ = System.Threading.Interlocked.Increment(ref readCacheInsertsUnderEpoch);
            return false;
        }

        /// <summary>Conditional read-cache inserts, by what they did: landed; held (the key already had a read-cache copy or a record inside the
        /// bound); below-memory (the check would have needed IO); lost (a writer moved the hash entry, or the read cache could not allocate);
        /// under-epoch (refused: the calling thread already held this store's epoch, inside one of its operations).</summary>
        public (long Landed, long Held, long BelowMemory, long Lost, long UnderEpoch) ConditionalReadCacheInserts =>
            (System.Threading.Interlocked.Read(ref readCacheInsertsLanded), System.Threading.Interlocked.Read(ref readCacheInsertsHeld),
             System.Threading.Interlocked.Read(ref readCacheInsertsBelowMemory), System.Threading.Interlocked.Read(ref readCacheInsertsLost),
             System.Threading.Interlocked.Read(ref readCacheInsertsUnderEpoch));
    }
}
