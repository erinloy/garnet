// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System.Runtime.CompilerServices;

namespace Tsavorite.core
{
    public unsafe partial class TsavoriteKV<TStoreFunctions, TAllocator> : TsavoriteBase
        where TStoreFunctions : IStoreFunctions
        where TAllocator : IAllocator<TStoreFunctions>
    {
        long readCacheInvalidations;

        /// <summary>
        /// Ziltch ADR-0147 s.3.4: a farm node told that another node changed <paramref name="key"/> drops its read-cache copy, so its next read
        /// asks the shared runs. Marks the key's read-cache record Invalid (readers skip it, eviction frees it); the main log is untouched.
        /// True when a copy was there. Never pending.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal bool InvalidateReadCacheBytes<TKey, TInput, TOutput, TContext, TSessionFunctionsWrapper>(TSessionFunctionsWrapper sessionFunctions, TKey key)
            where TKey : IKey
#if NET9_0_OR_GREATER
                , allows ref struct
#endif
            where TSessionFunctionsWrapper : ISessionFunctionsWrapper<TInput, TOutput, TContext, TStoreFunctions, TAllocator>
        {
            if (!UseReadCache)
                return false;
            OperationState<TInput, TOutput, TContext> operationState = default;
            OperationStackContext<TStoreFunctions, TAllocator> stackCtx = new(storeFunctions.GetKeyHashCode64(key));
            while (true)
            {
                if (!FindTagAndTryEphemeralSLock<TInput, TOutput, TContext, TSessionFunctionsWrapper>(sessionFunctions, ref stackCtx, out var status))
                {
                    if (status == OperationStatus.NOTFOUND)
                        return false;   // no tag: nothing of the key is cached
                    if (HandleImmediateRetryStatus(status, sessionFunctions, ref operationState))
                        continue;
                    return false;
                }
                try
                {
                    if (!stackCtx.hei.IsReadCache || !FindInReadCache(key, ref stackCtx, minAddress: LogAddress.kInvalidAddress, alwaysFindLatestLA: false))
                        return false;
                    LogRecord.GetInfoRef(stackCtx.recSrc.PhysicalAddress).SetInvalidAtomic();
                    _ = System.Threading.Interlocked.Increment(ref readCacheInvalidations);
                    return true;
                }
                finally
                {
                    EphemeralSUnlock<TInput, TOutput, TContext, TSessionFunctionsWrapper>(sessionFunctions, ref stackCtx);
                }
            }
        }

        /// <summary>Read-cache copies dropped by a farm notice (BasicContext.InvalidateReadCache).</summary>
        public long ReadCacheInvalidations => System.Threading.Interlocked.Read(ref readCacheInvalidations);
    }
}
