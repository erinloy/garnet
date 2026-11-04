// Copyright (c) Microsoft Corporation. Licensed under the MIT license.

using System.Collections.Generic;
using System.Threading;

namespace Tsavorite.core
{
    /// <summary>What <c>JoinHalvingPair</c> did with one same-tag pair.</summary>
    public enum HalvingJoinOutcome
    {
        /// <summary>One side's newest record of each of its keys was copied to the tail, chained onto the other side's head, and its entry swapped to the
        /// copies; the other side's entry too, while it still holds the head the copies were chained onto.</summary>
        Joined,
        /// <summary>A writer moved the copied side's entry between the walk and the swap: the copies stay invalid, and the next ask joins again.</summary>
        Raced,
        /// <summary>Neither side can be copied from memory: a record of each lies below HeadAddress. No IO is done.</summary>
        OnDisk,
        /// <summary>No side that could be copied has all its records below SafeReadOnlyAddress: a copy could miss an in-place update. The next ask joins again.</summary>
        Mutable,
        /// <summary>A side's entry is no longer the word the pre-scan named: the next scan names the pair again if it stands.</summary>
        Gone,
        /// <summary>A resize or a checkpoint is in flight, or a side holds more keys than a join copies.</summary>
        Refused,
    }

    /// <summary>One join: its outcome, the records it copied and their allocated bytes (both 0 unless joined).</summary>
    public readonly record struct HalvingJoin(HalvingJoinOutcome Outcome, int Copied, long Bytes);

    public partial class TsavoriteKV<TStoreFunctions, TAllocator> : TsavoriteBase
        where TStoreFunctions : IStoreFunctions
        where TAllocator : IAllocator<TStoreFunctions>
    {
        /// <summary>The most keys one side of a pair may hold for a join to copy it.</summary>
        public const int HalvingJoinKeysCap = 1024;

        /// <summary>
        /// Joins a same-tag pair the pre-scan named, so a halving can merge it: of the side whose chain is wholly in memory and immutable (the
        /// smaller, when both are), the newest record of each key that hashes to that side's bucket (tombstones too) is copied to the tail; the
        /// copies chain onto the other side's head, and the side's entry is swapped to them by CAS. The merged entry then walks both sides'
        /// keys, and the barrier's suffix check proves it. A lost CAS leaves the copies invalid, never a wrong chain. Reads no device.
        /// </summary>
        public unsafe HalvingJoin JoinHalvingPair(IndexTagCollision pair)
        {
            var protect = !epoch.ThisInstanceProtected();
            if (protect) epoch.Resume();
            try
            {
                if (resizeInfo.status != ResizeOperationStatus.DONE || stateMachineDriver.SystemState.Phase != Phase.REST)
                    return new(HalvingJoinOutcome.Refused, 0, 0);
                var version = resizeInfo.version;
                var half = state[version].size / 2;
                if (pair.Low < 0 || pair.Low >= half)
                    return new(HalvingJoinOutcome.Gone, 0, 0);
                var mask = state[version].size_mask;
                var low = state[version].tableAligned + pair.Low;
                var high = state[version].tableAligned + pair.Low + half;
                if (EntryHolding(low, pair.LowWord) == null || EntryHolding(high, pair.HighWord) == null)
                    return new(HalvingJoinOutcome.Gone, 0, 0);
                if (!TrySplicePair(low, high, ref pair))   // a side headed by read-cache copies joins its main-log chain
                    return new(HalvingJoinOutcome.Raced, 0, 0);

                List<long> lowKeys = new(), highKeys = new();
                var lowWhy = NewestOfEachKey(pair.LowWord, pair.Low, mask, pair.Tag, lowKeys);
                var highWhy = NewestOfEachKey(pair.HighWord, pair.Low + half, mask, pair.Tag, highKeys);
                var lowOk = lowWhy == HalvingJoinOutcome.Joined && !IsReadCacheWord(pair.HighWord);
                var highOk = highWhy == HalvingJoinOutcome.Joined && !IsReadCacheWord(pair.LowWord);
                if (!lowOk && !highOk)
                    return new(lowWhy == HalvingJoinOutcome.Joined ? highWhy == HalvingJoinOutcome.Joined ? HalvingJoinOutcome.OnDisk : highWhy
                        : lowWhy == HalvingJoinOutcome.OnDisk && highWhy != HalvingJoinOutcome.Joined ? highWhy : lowWhy, 0, 0);
                var copyLow = lowOk && (!highOk || lowKeys.Count <= highKeys.Count);
                var bucket = copyLow ? low : high;
                var (word, keys, onto) = copyLow ? (pair.LowWord, lowKeys, pair.HighWord) : (pair.HighWord, highKeys, pair.LowWord);

                HashBucketEntry other = default;
                other.word = onto;
                var previous = other.Address;
                var count = keys.Count;
                var made = new long[count];
                long bytes = 0;
                for (var k = count - 1; k >= 0; k--)   // oldest first: the newest copy is the chain's head
                {
                    var source = hlog.CreateLogRecord(keys[k], hlogBase.GetPhysicalAddress(keys[k]));
                    var sizeInfo = new RecordSizeInfo() { FieldInfo = source.GetRecordFieldInfo() };
                    if (source.Info.Tombstone)   // a tombstone is copied as a delete writes one: an in-place delete left its value's bytes, and a copy never writes them again
                        sizeInfo.FieldInfo = new() { KeySize = sizeInfo.FieldInfo.KeySize, ExtendedNamespaceSize = sizeInfo.FieldInfo.ExtendedNamespaceSize, RecordType = sizeInfo.FieldInfo.RecordType };
                    hlog.PopulateRecordSizeInfo(ref sizeInfo);
                    if (!hlogBase.TryAllocateRetryNow(sizeInfo.AllocatedInlineRecordSize, out var address) || keys[^1] < hlogBase.HeadAddress)
                    {
                        if (address > 0) LogRecord.GetInfoRef(hlogBase.GetPhysicalAddress(address)).SetInvalid();   // skipped on a scan
                        return Abandon(made, k + 1, count, keys[^1] < hlogBase.HeadAddress ? HalvingJoinOutcome.OnDisk : HalvingJoinOutcome.Raced);
                    }
                    source = hlog.CreateLogRecord(keys[k], hlogBase.GetPhysicalAddress(keys[k]));   // the allocation may have refreshed the epoch
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
                        var source = hlog.CreateLogRecord(keys[k], hlogBase.GetPhysicalAddress(keys[k]));
                        storeFunctions.PostCopyToTail(in source, keys[k], ref copy, made[k]);
                    }
                    copy.InfoRef.UnsealAndValidate();
                }
                OneWord(copyLow ? high : low, onto, joined.word);
                return new(HalvingJoinOutcome.Joined, count, bytes);
            }
            finally
            {
                if (protect) epoch.Suspend();
            }
        }

        // Both sides of a joined pair hold one word, so a write to either keeps the other a suffix of it, and copies that leave memory never part the
        // pair again (mesh 11-01: the pairs left rose 1,651 -> 2,449 in 4 min while ~620 joined a pass). A writer that moved the other side keeps its write.
        private unsafe void OneWord(HashBucket* other, long onto, long joined)
        {
            var slot = EntryHolding(other, onto);
            if (slot != null)
                _ = Interlocked.CompareExchange(ref *slot, joined, onto);
        }

        // The copies made so far stay sealed and invalid: no chain reaches them and a scan skips them.
        private HalvingJoin Abandon(long[] made, int from, int to, HalvingJoinOutcome why)
        {
            for (var k = from; k < to; k++)
                if (made[k] > 0)
                    LogRecord.GetInfoRef(hlogBase.GetPhysicalAddress(made[k])).SetInvalid();
            return new(why, 0, 0);
        }

        private static bool IsReadCacheWord(long word)
        {
            HashBucketEntry entry = default;
            entry.word = word;
            return entry.IsReadCache;
        }

        // The slot of bucket b's chain that holds word, or null.
        private unsafe long* EntryHolding(HashBucket* b, long word)
        {
            for (; b != null; b = NextOverflow(b))
                for (var slot = 0; slot < Constants.kOverflowBucketIndex; slot++)
                    if (b->bucket_entries[slot] == word)
                        return &b->bucket_entries[slot];
            return null;
        }

        // The addresses of the newest record of each key of the chain at word that hashes to bucket under tag, newest first, walked in memory
        // down to BeginAddress: Joined when the whole chain is in memory and immutable, else why it is not.
        private HalvingJoinOutcome NewestOfEachKey(long word, long bucket, long mask, ushort tag, List<long> into)
        {
            HashBucketEntry entry = default;
            entry.word = word;
            if (entry.IsReadCache)
                return HalvingJoinOutcome.OnDisk;
            var (begin, head, immutable) = (hlogBase.BeginAddress, hlogBase.HeadAddress, hlogBase.SafeReadOnlyAddress);
            for (var address = entry.Address; address >= begin && address > LogAddress.kTempInvalidAddress;)
            {
                if (address < head)
                    return HalvingJoinOutcome.OnDisk;
                if (address >= immutable)
                    return HalvingJoinOutcome.Mutable;
                var record = hlog.CreateLogRecord(address, hlogBase.GetPhysicalAddress(address));
                var info = record.Info;
                if (!info.Invalid)
                {
                    var hash = storeFunctions.GetKeyHashCode64(record);
                    if ((hash & mask) == bucket && HashBucketEntry.GetTag(hash) == tag && !HoldsKey(into, record))
                    {
                        if (into.Count == HalvingJoinKeysCap)
                            return HalvingJoinOutcome.Refused;
                        into.Add(address);
                    }
                }
                address = info.PreviousAddress;
            }
            return HalvingJoinOutcome.Joined;
        }

        private bool HoldsKey(List<long> newer, LogRecord record)
        {
            foreach (var address in newer)
                if (storeFunctions.KeysEqual(record, hlog.CreateLogRecord(address, hlogBase.GetPhysicalAddress(address))))
                    return true;
            return false;
        }
    }
}
