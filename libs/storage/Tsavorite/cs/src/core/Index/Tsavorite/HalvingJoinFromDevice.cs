// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System.Collections.Generic;
using System.Threading;

namespace Tsavorite.core
{
    using static LogAddress;

    public partial class TsavoriteKV<TStoreFunctions, TAllocator> : TsavoriteBase
        where TStoreFunctions : IStoreFunctions
        where TAllocator : IAllocator<TStoreFunctions>
    {
        /// <summary>
        /// <see cref="JoinHalvingPair"/> for a pair whose chains reach the device: each side's chain is walked from the word the pre-scan observed down
        /// to BeginAddress, its records below the head read from the device one at a time with no epoch held, and the newest record of each key that
        /// hashes to the side (tombstones too) is kept as a copy. The side with fewer keys is then copied to the tail onto the other side's head and its
        /// entry swapped to the copies by CAS on the observed word, exactly as the in-memory join does. A record below an unchanged head never changes,
        /// so a walk taken at that word is exact; a lost CAS leaves the copies invalid (Raced). A side headed by read-cache copies drops them first.
        /// </summary>
        /// <summary>A test seam: runs between a device join's walk and its copy, with no epoch held.</summary>
        public System.Action HalvingJoinBeforeCopyForTest { get; set; }

        public unsafe HalvingJoin JoinHalvingPairFromDevice(IndexTagCollision pair)
        {
            long half, mask;
            var protect = !epoch.ThisInstanceProtected();
            if (!protect)
                return new(HalvingJoinOutcome.Refused, 0, 0);   // a device wait under the caller's epoch would hold every other thread's reclaim
            epoch.Resume();
            try
            {
                if (resizeInfo.status != ResizeOperationStatus.DONE || stateMachineDriver.SystemState.Phase != Phase.REST)
                    return new(HalvingJoinOutcome.Refused, 0, 0);
                var version = resizeInfo.version;
                half = state[version].size / 2;
                if (pair.Low < 0 || pair.Low >= half)
                    return new(HalvingJoinOutcome.Gone, 0, 0);
                mask = state[version].size_mask;
                if (EntryHolding(state[version].tableAligned + pair.Low, pair.LowWord) == null
                    || EntryHolding(state[version].tableAligned + pair.Low + half, pair.HighWord) == null)
                    return new(HalvingJoinOutcome.Gone, 0, 0);
                if (!TrySplicePair(state[version].tableAligned + pair.Low, state[version].tableAligned + pair.Low + half, ref pair))
                    return new(HalvingJoinOutcome.Raced, 0, 0);
            }
            finally
            {
                epoch.Suspend();
            }

            List<(long Address, DiskLogRecord Record)> lowKeys = new(), highKeys = new();
            try
            {
                var lowWhy = NewestOfEachKeyFromDevice(pair.LowWord, pair.Low, mask, pair.Tag, lowKeys);
                var highWhy = NewestOfEachKeyFromDevice(pair.HighWord, pair.Low + half, mask, pair.Tag, highKeys);
                if (lowWhy != HalvingJoinOutcome.Joined && highWhy != HalvingJoinOutcome.Joined)
                    return new(lowWhy == HalvingJoinOutcome.OnDisk ? highWhy : lowWhy, 0, 0);
                var copyLow = lowWhy == HalvingJoinOutcome.Joined && (highWhy != HalvingJoinOutcome.Joined || lowKeys.Count <= highKeys.Count);
                HalvingJoinBeforeCopyForTest?.Invoke();
                return CopyOnto(pair, half, copyLow ? pair.Low : pair.Low + half, copyLow ? pair.LowWord : pair.HighWord,
                    copyLow ? lowKeys : highKeys, copyLow ? pair.HighWord : pair.LowWord);
            }
            finally
            {
                foreach (var (_, record) in lowKeys) record.Dispose();
                foreach (var (_, record) in highKeys) record.Dispose();
            }
        }

        // The newest record of each key of the chain at word that hashes to bucket under tag, newest first, each a copy this call owns: records at or
        // above the head under the epoch, records below it from the device with no epoch held. Mutable when the chain reaches the mutable region.
        private unsafe HalvingJoinOutcome NewestOfEachKeyFromDevice(long word, long bucket, long mask, ushort tag, List<(long, DiskLogRecord)> into)
        {
            HashBucketEntry entry = default;
            entry.word = word;
            var address = entry.Address;
            AsyncIOContextCompletionEvent read = null;
            try
            {
                while (address >= hlogBase.BeginAddress && address > kTempInvalidAddress)
                {
                    DiskLogRecord record;
                    epoch.Resume();
                    try
                    {
                        if (address >= hlogBase.SafeReadOnlyAddress)
                            return HalvingJoinOutcome.Mutable;
                        record = address >= hlogBase.HeadAddress
                            ? DiskLogRecord.CopyFrom(hlog.CreateLogRecord(address, hlogBase.GetPhysicalAddress(address)), hlogBase.bufferPool, hlogBase.transientObjectIdMap)
                            : default;
                    }
                    finally
                    {
                        epoch.Suspend();
                    }
                    if (!record.IsSet)
                    {
                        read ??= new AsyncIOContextCompletionEvent();
                        read.PrepareAtAddress(address);
                        hlogBase.AsyncGetFromDisk(address, IStreamBuffer.DefaultInitialIORecordSize, read.request);
                        read.Wait();
                        if (read.exception is not null)
                            return HalvingJoinOutcome.Refused;
                        if (read.request.logicalAddress < hlogBase.BeginAddress || !read.request.diskLogRecord.IsSet)
                            break;   // the device walked past every invalid record to below begin
                        address = read.request.logicalAddress;   // an invalid record is passed over to its previous one by the read itself
                        record = read.request.diskLogRecord;
                        read.request.diskLogRecord = default;   // ours now: disposed below or with the list
                        read.request.DisposeRecord();
                    }

                    var keep = false;
                    try
                    {
                        if (!record.Info.Invalid)
                        {
                            var hash = storeFunctions.GetKeyHashCode64(record);
                            if ((hash & mask) == bucket && HashBucketEntry.GetTag(hash) == tag && !HoldsKey(into, record))
                            {
                                if (into.Count == HalvingJoinKeysCap)
                                    return HalvingJoinOutcome.Refused;
                                into.Add((address, record));
                                keep = true;
                            }
                        }
                        address = record.Info.PreviousAddress;
                    }
                    finally
                    {
                        if (!keep) record.Dispose();
                    }
                }
                return HalvingJoinOutcome.Joined;
            }
            finally
            {
                read?.Dispose();
            }
        }

        private bool HoldsKey(List<(long, DiskLogRecord Record)> newer, DiskLogRecord record)
        {
            foreach (var (_, held) in newer)
                if (storeFunctions.KeysEqual(record, held))
                    return true;
            return false;
        }

        // The join's write half over owned copies: oldest first onto the other side's head, then the side's entry swapped from the word it was walked at.
        private unsafe HalvingJoin CopyOnto(IndexTagCollision pair, long half, long side, long word, List<(long Address, DiskLogRecord Record)> keys, long onto)
        {
            epoch.Resume();
            try
            {
                if (resizeInfo.status != ResizeOperationStatus.DONE || stateMachineDriver.SystemState.Phase != Phase.REST || state[resizeInfo.version].size / 2 != half)
                    return new(HalvingJoinOutcome.Raced, 0, 0);
                var bucket = state[resizeInfo.version].tableAligned + side;
                HashBucketEntry other = default;
                other.word = onto;
                var previous = other.Address;
                var count = keys.Count;
                var made = new long[count];
                long bytes = 0;
                for (var k = count - 1; k >= 0; k--)
                {
                    var source = keys[k].Record;
                    var sizeInfo = new RecordSizeInfo() { FieldInfo = source.GetRecordFieldInfo() };
                    if (source.Info.Tombstone)   // a tombstone is copied as a delete writes one: an in-place delete left its value's bytes, and a copy never writes them again
                        sizeInfo.FieldInfo = new() { KeySize = sizeInfo.FieldInfo.KeySize, ExtendedNamespaceSize = sizeInfo.FieldInfo.ExtendedNamespaceSize, RecordType = sizeInfo.FieldInfo.RecordType };
                    hlog.PopulateRecordSizeInfo(ref sizeInfo);
                    if (!hlogBase.TryAllocateRetryNow(sizeInfo.AllocatedInlineRecordSize, out var address))
                    {
                        if (address > 0) LogRecord.GetInfoRef(hlogBase.GetPhysicalAddress(address)).SetInvalid();
                        return Abandon(made, k + 1, count, HalvingJoinOutcome.Raced);
                    }
                    var copy = WriteNewRecordInfo(source, hlogBase, address, hlogBase.GetPhysicalAddress(address), in sizeInfo, inNewVersion: false, previousAddress: previous);
                    if (!source.Info.Tombstone) _ = copy.TryCopyFrom(in source, in sizeInfo);
                    if (source.Info.Tombstone)
                        copy.InfoRef.SetTombstone();
                    made[k] = address;
                    bytes += copy.AllocatedSize;
                    previous = address;
                }

                HashBucketEntry joined = default;
                joined.Tag = pair.Tag;
                joined.Address = previous;
                var slot = EntryHolding(bucket, word);
                if (stateMachineDriver.SystemState.Phase != Phase.REST || slot == null || Interlocked.CompareExchange(ref *slot, joined.word, word) != word)
                    return Abandon(made, 0, count, HalvingJoinOutcome.Raced);
                for (var k = count - 1; k >= 0; k--)
                {
                    var copy = hlog.CreateLogRecord(made[k], hlogBase.GetPhysicalAddress(made[k]));
                    hlogBase.logSizeTracker?.UpdateSize(in copy, add: true);
                    if (storeFunctions.CallPostCopyToTail)
                    {
                        var source = keys[k].Record;
                        storeFunctions.PostCopyToTail(in source, keys[k].Address, ref copy, made[k]);
                    }
                    copy.InfoRef.UnsealAndValidate();
                }
                OneWord(state[resizeInfo.version].tableAligned + (side == pair.Low ? pair.Low + half : pair.Low), onto, joined.word);
                return new(HalvingJoinOutcome.Joined, count, bytes);
            }
            finally
            {
                epoch.Suspend();
            }
        }
    }
}
