// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Tsavorite.core
{
#pragma warning disable IDE0065 // Misplaced using directive
    using static LogAddress;

    // Partial file for readcache functions
    public unsafe partial class TsavoriteKV<TStoreFunctions, TAllocator> : TsavoriteBase
        where TStoreFunctions : IStoreFunctions
        where TAllocator : IAllocator<TStoreFunctions>
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal bool FindInReadCache<TKey>(TKey key, ref OperationStackContext<TStoreFunctions, TAllocator> stackCtx, long minAddress = kInvalidAddress, bool alwaysFindLatestLA = true)
            where TKey : IKey
#if NET9_0_OR_GREATER
                , allows ref struct
#endif
        {
            Debug.Assert(UseReadCache, "Should not call FindInReadCache if !UseReadCache");

            // minAddress, if present, comes from the pre-pendingIO entry.Address; there may have been no readcache entries then.
            minAddress = IsReadCache(minAddress) ? AbsoluteAddress(minAddress) : readcacheBase.HeadAddress;

        RestartChain:

            // 'recSrc' has already been initialized to the address in 'hei'.
            if (!stackCtx.hei.IsReadCache)
                return false;

            // This is also part of the initialization process for stackCtx.recSrc for each API/InternalXxx call.
            stackCtx.recSrc.LogicalAddress = kInvalidAddress;
            stackCtx.recSrc.PhysicalAddress = 0;

            while (true)
            {
                if (ReadCacheNeedToWaitForEviction(ref stackCtx))
                    goto RestartChain;

                // LatestLogicalAddress is the "leading" pointer and will end up as the highest logical address in the main log for this tag chain.
                // curReadCache* is the read-cache record we are currently examining; we look ahead from it to the next record.
                var curReadCacheLogicalAddress = stackCtx.recSrc.LatestLogicalAddress;
                var curReadCachePhysicalAddress = readcacheBase.GetPhysicalAddress(curReadCacheLogicalAddress);

                // Use a non-ref local, because we don't need to update.
                var recordInfo = LogRecord.GetInfo(curReadCachePhysicalAddress);

                // When traversing the readcache, we skip Invalid (Closed) records. We don't have Sealed records in the readcache because they cause
                // the operation to be retried, so we'd never get past them. Return true if we find a Valid read cache entry matching the key.
                if (!recordInfo.Invalid && stackCtx.recSrc.LatestLogicalAddress >= minAddress && !stackCtx.recSrc.HasReadCacheSrc)
                {
                    // TODO: Can we avoid always creating the log record here?
                    var logRecord = readcache.CreateLogRecord(curReadCacheLogicalAddress);
                    if (storeFunctions.KeysEqual(key, logRecord))
                    {
                        // Keep these at the current readcache location; they'll be the caller's source record.
                        stackCtx.recSrc.LogicalAddress = curReadCacheLogicalAddress;
                        stackCtx.recSrc.PhysicalAddress = curReadCachePhysicalAddress;
                        stackCtx.recSrc.SetAllocator(readcacheBase);
                        stackCtx.recSrc.SetHasReadCacheSrc();

                        // Read() does not need to continue past the found record; updaters continue the walk to find
                        // latestLogicalAddress (the main-log address below the read-cache prefix that the detach CAS targets).
                        if (!alwaysFindLatestLA)
                            return true;
                    }
                }

                // The current readcache record passed ReadCacheNeedToWaitForEviction above (it is >= readcache.HeadAddress),
                // so eviction cannot have unlinked/stamped it here: eviction runs as a deferred, epoch-gated drain-list action
                // (OnPagesClosedWorker) and HeadAddress is published before it is queued, so a walker either waits at the
                // boundary above or holds an epoch that gates the unlink. Its PreviousAddress is therefore always a live
                // address (a lower readcache record or the main-log boundary), or kInvalidAddress when ConditionalInsertReadCache cached a key
                // with no main-log record. SkipReadCache relies on this same invariant without a check.
                Debug.Assert(recordInfo.PreviousAddress != kTempInvalidAddress,
                    "readcache record's PreviousAddress was kTempInvalid; eviction must not be observable here under epoch");

                // Update the leading LatestLogicalAddress to recordInfo.PreviousAddress, and if that is a main log record, break out.
                stackCtx.recSrc.LatestLogicalAddress = recordInfo.PreviousAddress;
                if (!IsReadCache(stackCtx.recSrc.LatestLogicalAddress))
                    goto InMainLog;
            }

        InMainLog:
            if (stackCtx.recSrc.HasReadCacheSrc)
                return true;

            // We did not find the record in the readcache, so set these to the start of the main log entries, and the caller will call TracebackForKeyMatch
            Debug.Assert(ReferenceEquals(stackCtx.recSrc.AllocatorBase, hlogBase), "Expected recSrc.AllocatorBase == hlogBase");
            Debug.Assert(stackCtx.recSrc.LatestLogicalAddress != kTempInvalidAddress, "Must have a main-log address (or none) after readcache");
            stackCtx.recSrc.LogicalAddress = stackCtx.recSrc.LatestLogicalAddress;
            stackCtx.recSrc.PhysicalAddress = 0; // do *not* call hlog.GetPhysicalAddress(); LogicalAddress may be below hlog.HeadAddress. Let the caller decide when to do this.
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        bool ReadCacheNeedToWaitForEviction(ref OperationStackContext<TStoreFunctions, TAllocator> stackCtx)
        {
            Debug.Assert(stackCtx.hei.IsReadCache, "Can only call ReadCacheNeedToWaitForEviction when hei.IsReadCache is true");
            Debug.Assert(IsReadCache(stackCtx.recSrc.LatestLogicalAddress), "Can only call ReadCacheNeedToWaitForEviction when stackCtx.recSrc.LatestLogicalAddress is readcache");
            var logicalAddress = AbsoluteAddress(stackCtx.recSrc.LatestLogicalAddress);
            if (logicalAddress < readcacheBase.HeadAddress)
            {
                // A link read after this walk saw its record's close complete is one the eviction missed: waiting cannot unlink it, and the
                // retry loop that followed it here would never end (the Ziltch broker's readers spun in it on 10-30). Count it and throw.
                if (logicalAddress < stackCtx.readCacheClosedSeen)
                    ThrowReadCacheChainLost(logicalAddress, ReadCacheChainLostException.ReadWalker);

                SpinWaitUntilRecordIsClosed(logicalAddress, readcacheBase);

                // The close of logicalAddress has completed, and a close unlinks before it moves ClosedUntilAddress. Every link read from here on
                // that is below this value is dangling.
                stackCtx.readCacheClosedSeen = Volatile.Read(ref readcacheBase.ClosedUntilAddress);

                // Restore to hlog; we may have set readcache into Log and continued the loop, had to restart, and the matching readcache record was evicted.
                stackCtx.UpdateRecordSourceToCurrentHashEntry(hlogBase);
                return true;
            }
            return false;
        }

        // Skip over all readcache records in this key's chain, advancing stackCtx.recSrc to the first non-readcache record we encounter.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void SkipReadCache(ref OperationStackContext<TStoreFunctions, TAllocator> stackCtx, out bool didRefresh)
        {
            Debug.Assert(UseReadCache, "Should not call SkipReadCache if !UseReadCache");
            didRefresh = false;

        RestartChain:
            // 'recSrc' has already been initialized to the address in 'hei'.
            if (!stackCtx.hei.IsReadCache)
                return;

            // This is FindInReadCache without the key comparison or untilAddress.
            stackCtx.recSrc.LogicalAddress = kInvalidAddress;
            stackCtx.recSrc.PhysicalAddress = 0;

            while (true)
            {
                if (ReadCacheNeedToWaitForEviction(ref stackCtx))
                {
                    didRefresh = true;
                    goto RestartChain;
                }

                // curReadCachePhysicalAddress is the read-cache record we are currently examining; we look ahead from it to the next record.
                // GetPhysicalAddress strips the read-cache bit internally (via GetPageOfAddress -> AbsoluteAddress), so pass the raw address.
                var curReadCachePhysicalAddress = readcacheBase.GetPhysicalAddress(stackCtx.recSrc.LatestLogicalAddress);

                var recordInfo = LogRecord.GetInfo(curReadCachePhysicalAddress);
                if (!IsReadCache(recordInfo.PreviousAddress))
                {
                    stackCtx.recSrc.LatestLogicalAddress = recordInfo.PreviousAddress;
                    stackCtx.recSrc.LogicalAddress = stackCtx.recSrc.LatestLogicalAddress;
                    stackCtx.recSrc.PhysicalAddress = 0;
                    return;
                }
                stackCtx.recSrc.LatestLogicalAddress = recordInfo.PreviousAddress;
            }
        }

        // Skip over all readcache records in all key chains in this bucket, updating the bucket to point to the first main log record.
        // Called during checkpointing; we create a copy of the hash table page, eliminate read cache pointers from this copy, then write this copy to disk.
        // 'live' is the bucket 'copy' was taken from. The caller holds the epoch.
        //
        // 🩸 THE COPY CAN NAME A CLOSED READ-CACHE PAGE. A head shift bumps its close on the epoch, and a close bumped before this thread
        // protected does not wait for it: the copy is taken before that close unlinks the copy's read-cache records and walked after their
        // page is cleared or refilled. The walk then read PreviousAddress 0 (the key's entry kept its tag and lost its whole chain, silently)
        // or another record's bytes (an address past the log, or off the record alignment, which recovery cleared). Measured on the Ziltch
        // broker's 10-26 index checkpoint: 1,914 entries past the log's final address and 344 misaligned, every key behind them absent.
        // So a read-cache address below HeadAddress is never followed: the walk lets that close finish and starts again from the live slot,
        // which the close has unlinked. An address at or above HeadAddress is safe to follow: its close can only be bumped after we protected.
        internal void SkipReadCacheBucket(HashBucket* copy, HashBucket* live)
        {
            for (var index = 0; index < Constants.kOverflowBucketIndex; index++)
            {
                var entry = (HashBucketEntry*)&copy->bucket_entries[index];
                if (0 == entry->word)
                    continue;

                if (!entry->IsReadCache) continue;

                var copiedTag = entry->Tag;
                var closedSeen = 0L;
                while (true)
                {
                    var logicalAddress = entry->Address;
                    while (IsReadCache(logicalAddress) && AbsoluteAddress(logicalAddress) >= readcacheBase.HeadAddress)
                        logicalAddress = LogRecord.GetInfo(readcacheBase.GetPhysicalAddress(logicalAddress)).PreviousAddress;
                    if (!IsReadCache(logicalAddress))
                    {
                        entry->Address = logicalAddress;
                        break;
                    }

                    // As in ReadCacheNeedToWaitForEviction: a link read from the live slot after this walk saw its close complete is dangling,
                    // and the rewalk would spin on it forever with the checkpoint held open.
                    if (AbsoluteAddress(logicalAddress) < closedSeen)
                        ThrowReadCacheChainLost(AbsoluteAddress(logicalAddress), ReadCacheChainLostException.CheckpointWalker);

                    _ = Interlocked.Increment(ref checkpointClosedReadCacheRewalks);
                    SpinWaitUntilRecordIsClosed(AbsoluteAddress(logicalAddress), readcacheBase);
                    closedSeen = Volatile.Read(ref readcacheBase.ClosedUntilAddress);
                    // The live slot is this key's only while it carries the copied tag. A close that unlinked a cached absence leaves address 0,
                    // below begin, and another key's FindOrCreateTag may reclaim the slot: then this key has no record, and the copied slot is
                    // emptied rather than given the other key's word, which replay restores (frontier, rg 10-29).
                    HashBucketEntry now = default;
                    now.word = Volatile.Read(ref live->bucket_entries[index]);
                    if (now.word != 0 && now.Tag != copiedTag)
                    {
                        _ = Interlocked.Increment(ref checkpointRewalkTagMoved);
                        entry->word = 0;
                        break;
                    }
                    entry->word = now.word;
                    if (0 == entry->word || !entry->IsReadCache)
                        break;
                }
            }
        }

        long checkpointClosedReadCacheRewalks, checkpointRewalkTagMoved, readCacheMissedEvictions;

        /// <summary>Read-cache links met below ClosedUntilAddress after their close completed: records the eviction left linked. Each one
        /// threw <see cref="ReadCacheChainLostException"/>.</summary>
        public long ReadCacheMissedEvictions => Interlocked.Read(ref readCacheMissedEvictions);

        /// <summary>Called on every missed eviction, before the throw, with the running count and the walker
        /// (<see cref="ReadCacheChainLostException.ReadWalker"/> or <see cref="ReadCacheChainLostException.CheckpointWalker"/>).
        /// It runs on the walking thread, under the epoch and possibly a bucket lock: it must not block or touch the store.</summary>
        public Action<long, string> ReadCacheChainLost;

        [MethodImpl(MethodImplOptions.NoInlining)]
        [DoesNotReturn]
        void ThrowReadCacheChainLost(long absoluteAddress, string walker)
        {
            var count = Interlocked.Increment(ref readCacheMissedEvictions);
            try
            {
                ReadCacheChainLost?.Invoke(count, walker);
            }
            catch
            {
                // The observer's fault must not replace the named exception.
            }
            throw new ReadCacheChainLostException(absoluteAddress, Volatile.Read(ref readcacheBase.ClosedUntilAddress), walker, count);
        }

        /// <summary>Of those rewalks, the slots another key had reclaimed by then: the copied key had no record left.</summary>
        public long CheckpointRewalkTagMoved => Interlocked.Read(ref checkpointRewalkTagMoved);

        /// <summary>Index-checkpoint copies whose read-cache chain reached a closed page and were walked again from the live bucket.</summary>
        public long CheckpointClosedReadCacheRewalks => Interlocked.Read(ref checkpointClosedReadCacheRewalks);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void ReadCacheAbandonRecord(long physicalAddress)
        {
            // TODO: We currently don't save readcache allocations for retry, but we could
            ref var ri = ref LogRecord.GetInfoRef(physicalAddress);
            ri.SetInvalid();
            ri.PreviousAddress = kTempInvalidAddress;     // Necessary for ReadCacheEvict, but cannot be kInvalidAddress or we have recordInfo.IsNull
        }

        // One bit per 8-byte slot of every read-cache page: set where a record was allocated, cleared when its page has been evicted. The
        // evictor walks these bits instead of striding by each record's stated size, so a header that overstates its size cannot hide the
        // record after it, which stayed linked into a closed page (the dangling links of 10-30 and 11-02; their cause is still unproven).
        long[] readCacheStarts;
        int readCacheStartWordsPerPage;
        long readCacheStrideOvershoots, readCacheUnwrittenStarts;

        /// <summary>Records the evictor found where the previous record's stated size said there was none: each one a record the old
        /// stride walk would have left linked into a closed page.</summary>
        public long ReadCacheStrideOvershoots => Interlocked.Read(ref readCacheStrideOvershoots);

        /// <summary>Allocated read-cache starts whose RecordInfo was never written (an allocation abandoned before its header).</summary>
        public long ReadCacheUnwrittenStarts => Interlocked.Read(ref readCacheUnwrittenStarts);

        void InitializeReadCacheStarts()
        {
            readCacheStartWordsPerPage = Math.Max(1, readcacheBase.PageSize >> 9);
            readCacheStarts = new long[(long)readcacheBase.BufferSize * readCacheStartWordsPerPage];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void NoteReadCacheStart(long logicalAddress)
        {
            var slot = readcacheBase.GetOffsetOnPage(logicalAddress) >> 3;
            var word = (long)readcacheBase.GetPageIndexForAddress(logicalAddress) * readCacheStartWordsPerPage + (slot >> 6);
            _ = Interlocked.Or(ref readCacheStarts[word], 1L << (int)(slot & 63));
        }

        internal void ReadCacheEvict(long rcLogicalAddress, long rcToLogicalAddress)
        {
            // Every record allocated in [rcLogicalAddress, rcToLogicalAddress) is named by its start bit; each is unlinked from its key's
            // chain. The range is below SafeHeadAddress, so every allocation in it, and the bit it set, happened before this epoch drained.
            var pageSize = readcacheBase.PageSize;
            var pageBits = readcacheBase.LogPageSizeBits;
            for (var page = readcacheBase.GetPage(rcLogicalAddress); (page << pageBits) < rcToLogicalAddress; page++)
            {
                var pageStart = page << pageBits;
                var from = Math.Max(rcLogicalAddress, pageStart);
                var to = Math.Min(rcToLogicalAddress, pageStart + pageSize);
                var firstWord = (long)readcacheBase.GetPageIndexForPage(page) * readCacheStartWordsPerPage;
                var slotLo = (from - pageStart) >> 3;
                var slotHi = (to - pageStart + 7) >> 3;
                var strideNext = 0L;
                for (var w = slotLo >> 6; w <= (slotHi - 1) >> 6; w++)
                {
                    var bits = (ulong)Volatile.Read(ref readCacheStarts[firstWord + w]);
                    if (w == slotLo >> 6)
                        bits &= ulong.MaxValue << (int)(slotLo & 63);
                    if (w == (slotHi - 1) >> 6 && (slotHi & 63) != 0)
                        bits &= (1UL << (int)(slotHi & 63)) - 1;
                    while (bits != 0)
                    {
                        var bit = BitOperations.TrailingZeroCount(bits);
                        bits &= bits - 1;
                        ReadCacheEvictOne(pageStart + ((w << 6) + bit << 3), rcToLogicalAddress, ref strideNext);
                    }
                }

                // The page's records are all unlinked: its bits go before the page is freed, so the page that next takes its buffer slot starts clean.
                if (to == pageStart + pageSize)
                    Array.Clear(readCacheStarts, (int)firstWord, readCacheStartWordsPerPage);
            }
        }

        void ReadCacheEvictOne(long rcLogicalAddress, long rcToLogicalAddress, ref long strideNext)
        {
            var logRecord = new LogRecord(readcacheBase.GetPhysicalAddress(rcLogicalAddress));
            var rcRecordInfo = logRecord.Info;
            if (rcRecordInfo.IsNull)
            {
                _ = Interlocked.Increment(ref readCacheUnwrittenStarts);
                return;
            }
            if (strideNext > rcLogicalAddress)
                _ = Interlocked.Increment(ref readCacheStrideOvershoots);
            strideNext = rcLogicalAddress + logRecord.AllocatedSize;

            // A CAS failure or abandoned allocation (kTempInvalidAddress) is in no chain. A record whose PreviousAddress is kInvalidAddress IS:
            // ConditionalInsertReadCache caches the answer for a key with no main-log record (Ziltch router, 10-23).
            if (rcRecordInfo.PreviousAddress == kTempInvalidAddress)
                return;

            HashEntryInfo hei = new(storeFunctions.GetKeyHashCode64(logRecord));
            if (FindTag(ref hei))
                ReadCacheEvictChain(rcToLogicalAddress, ref hei);
        }

        private void ReadCacheEvictChain(long rcToLogicalAddress, ref HashEntryInfo hei)
        {
            // Traverse the chain of readcache entries for this key, looking "ahead" to .PreviousAddress to see if it is less than readcache.HeadAddress.
            // nextPhysicalAddress remains Constants.kInvalidAddress if hei.Address is < HeadAddress; othrwise, it is the lowest-address readcache record
            // remaining following this eviction, and its .PreviousAddress is updated to each lower record in turn until we hit a non-readcache record.
            var nextPhysicalAddress = kInvalidAddress;
            HashBucketEntry entry = new() { word = hei.entry.word };
            while (entry.IsReadCache)
            {
                var logRecord = new LogRecord(readcacheBase.GetPhysicalAddress(entry.Address));
                ref var recordInfo = ref logRecord.InfoRef;

#if DEBUG
                // Due to collisions, we can compare the hash code *mask* (i.e. the hash bucket index), not the key
                var mask = state[resizeInfo.version].size_mask;
                var rc_mask = hei.hash & mask;
                var pa_mask = storeFunctions.GetKeyHashCode64(logRecord) & mask;
                Debug.Assert(rc_mask == pa_mask, "The keyHash mask of the hash-chain ReadCache entry does not match the one obtained from the initial readcache address");
#endif

                // If the record's address is above the eviction range, leave it there and track nextPhysicalAddress.
                if (AbsoluteAddress(entry.Address) >= rcToLogicalAddress)
                {
                    Debug.Assert(!IsReadCache(recordInfo.PreviousAddress) || entry.Address > recordInfo.PreviousAddress, "Invalid ordering in readcache chain");

                    nextPhysicalAddress = logRecord.physicalAddress;
                    entry.word = recordInfo.PreviousAddress;
                    continue;
                }

                // The record is being evicted. If we have a higher readcache record that is not being evicted, unlink 'entry.Address' by setting
                // (nextPhysicalAddress).PreviousAddress to (entry.Address).PreviousAddress.
                if (nextPhysicalAddress != kInvalidAddress)
                {
                    ref var nextri = ref LogRecord.GetInfoRef(nextPhysicalAddress);
                    if (nextri.TryUpdateAddress(entry.Address, recordInfo.PreviousAddress))
                    {
                        recordInfo.PreviousAddress = kTempInvalidAddress;     // The record is no longer in the chain
                        entry.word = nextri.PreviousAddress;
                    }
                    else
                        Debug.Assert(entry.word == nextri.PreviousAddress, "We should be about to retry nextri.PreviousAddress");
                    continue;
                }

                // We are evicting the record whose address is in the hash bucket; unlink 'la' by setting the hash bucket to point to (la).PreviousAddress.
                if (hei.TryCAS(recordInfo.PreviousAddress))
                    recordInfo.PreviousAddress = kTempInvalidAddress;     // The record is no longer in the chain
                else
                    hei.SetToCurrent();
                entry.word = hei.entry.word;
            }
        }
    }
}