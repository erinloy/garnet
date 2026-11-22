// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System.Runtime.CompilerServices;

namespace Tsavorite.core
{
    public unsafe partial class TsavoriteKV<TStoreFunctions, TAllocator> : TsavoriteBase
        where TStoreFunctions : IStoreFunctions
        where TAllocator : IAllocator<TStoreFunctions>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal Status InternalContainsKeyInMemory<TKey, TInput, TOutput, TContext, TSessionFunctionsWrapper>(
            TKey key, TSessionFunctionsWrapper sessionFunctions, out long logicalAddress, long fromAddress = -1)
            where TKey : IKey
#if NET9_0_OR_GREATER
                , allows ref struct
#endif
            where TSessionFunctionsWrapper : ISessionFunctionsWrapper<TInput, TOutput, TContext, TStoreFunctions, TAllocator>
        {
            OperationStackContext<TStoreFunctions, TAllocator> stackCtx = new(storeFunctions.GetKeyHashCode64(key));

            if (sessionFunctions.Ctx.phase == Phase.IN_PROGRESS_GROW)
                SplitBuckets(stackCtx.hei.hash);

            if (FindTag(ref stackCtx.hei))
            {
                stackCtx.SetRecordSourceToHashEntry(hlogBase);

                // Gate on hei.IsReadCache (same cost as UseReadCache): skips the call when this chain has no RC prefix, and
                // lets SkipReadCache's UseReadCache assert flag an RC-record-while-!UseReadCache anomaly.
                if (stackCtx.hei.IsReadCache)
                    SkipReadCache(ref stackCtx, out _);

                if (fromAddress < hlogBase.HeadAddress)
                    fromAddress = hlogBase.HeadAddress;

                if (TraceBackForKeyMatch(key, ref stackCtx.recSrc, fromAddress) && !stackCtx.recSrc.GetInfo().Tombstone)
                {
                    logicalAddress = stackCtx.recSrc.LogicalAddress;
                    return new(StatusCode.Found);
                }
                logicalAddress = 0;
                return new(StatusCode.NotFound);
            }

            // no tag found
            logicalAddress = 0;
            return new(StatusCode.NotFound);
        }

        /// <summary>
        /// Where a read of <paramref name="key"/> would first leave memory, answered from memory alone (no device read is issued).
        /// </summary>
        /// <param name="logicalAddress">Found: the record's address in memory. NotFound: the address its chain goes on at below the
        /// head, the first device read a point read would issue; 0 when no device read would be (no tag, a chain that ends in memory or
        /// below the begin address, a tombstone in memory). A record held only in the read cache answers as below memory.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal Status InternalChainAddressBelowMemory<TKey, TInput, TOutput, TContext, TSessionFunctionsWrapper>(
            TKey key, TSessionFunctionsWrapper sessionFunctions, out long logicalAddress)
            where TKey : IKey
#if NET9_0_OR_GREATER
                , allows ref struct
#endif
            where TSessionFunctionsWrapper : ISessionFunctionsWrapper<TInput, TOutput, TContext, TStoreFunctions, TAllocator>
        {
            OperationStackContext<TStoreFunctions, TAllocator> stackCtx = new(storeFunctions.GetKeyHashCode64(key));

            if (sessionFunctions.Ctx.phase == Phase.IN_PROGRESS_GROW)
                SplitBuckets(stackCtx.hei.hash);

            logicalAddress = 0;
            if (!FindTag(ref stackCtx.hei))
                return new(StatusCode.NotFound);

            stackCtx.SetRecordSourceToHashEntry(hlogBase);
            if (stackCtx.hei.IsReadCache)
                SkipReadCache(ref stackCtx, out _);

            if (TraceBackForKeyMatch(key, ref stackCtx.recSrc, hlogBase.HeadAddress))
            {
                if (stackCtx.recSrc.GetInfo().Tombstone)
                    return new(StatusCode.NotFound);
                logicalAddress = stackCtx.recSrc.LogicalAddress;
                return new(StatusCode.Found);
            }

            // The trace stopped at the first address under the head: the record a pending read would fetch first.
            if (stackCtx.recSrc.LogicalAddress >= hlogBase.BeginAddress)
                logicalAddress = stackCtx.recSrc.LogicalAddress;
            return new(StatusCode.NotFound);
        }
    }
}