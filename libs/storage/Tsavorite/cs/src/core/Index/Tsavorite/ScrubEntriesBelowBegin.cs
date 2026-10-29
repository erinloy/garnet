// Copyright (c) Microsoft Corporation. Licensed under the MIT license.

using System;
using System.Threading;

namespace Tsavorite.core
{
    /// <summary>One bounded step of a live index scrub: entries it cleared, CASes it lost to a writer (skipped, never waited on),
    /// the bucket the next step starts at, whether the table is done, and whether the step was refused (an index resize in flight).</summary>
    public readonly record struct IndexScrubStep(long Cleared, long Lost, long Next, bool Done, bool Refused);

    public partial class TsavoriteKV<TStoreFunctions, TAllocator> : TsavoriteBase
        where TStoreFunctions : IStoreFunctions
        where TAllocator : IAllocator<TStoreFunctions>
    {
        /// <summary>
        /// LIVE INDEX GC, one bounded step: clears every main-log hash entry below the log's BeginAddress in buckets
        /// [<paramref name="from"/>, <paramref name="from"/> + <paramref name="buckets"/>) and their overflow buckets. Such an entry
        /// names no record a read can reach (FindRecord answers not-found below begin), so clearing it changes no read; an insert
        /// that passes one already clears it lazily (FindTagOrFreeInternal) - this is that same CAS, done eagerly, so the table's
        /// live entries follow the log's begin. A CAS that loses to a writer skips the entry. The caller holds the cursor
        /// (<see cref="IndexScrubStep.Next"/>), so a kill resumes at the last step it kept. Epoch-protected for the step, so an index
        /// resize cannot swap the table under it; refused while one is in flight. Read-cache and tentative entries are left alone.
        /// </summary>
        public unsafe IndexScrubStep ScrubEntriesBelowBegin(long from, long buckets)
        {
            var protect = !epoch.ThisInstanceProtected();
            if (protect) epoch.Resume();
            try
            {
                var phase = stateMachineDriver.SystemState.Phase;
                if (resizeInfo.status != ResizeOperationStatus.DONE || phase == Phase.PREPARE_GROW || phase == Phase.IN_PROGRESS_GROW)
                    return new(0, 0, from, false, true);
                var version = resizeInfo.version;
                var tableSize = state[version].size;
                var begin = hlogBase.BeginAddress;
                var end = Math.Min(tableSize, Math.Max(from, 0) + buckets);
                long cleared = 0, lost = 0;
                for (var bucket = Math.Max(from, 0); bucket < end; bucket++)
                {
                    var b = state[version].tableAligned + bucket;
                    while (true)
                    {
                        for (var slot = 0; slot < Constants.kOverflowBucketIndex; slot++)
                        {
                            HashBucketEntry entry = default;
                            entry.word = b->bucket_entries[slot];
                            if (entry.word == 0 || entry.IsReadCache || entry.Tentative || entry.Address >= begin || entry.Address == LogAddress.kTempInvalidAddress)
                                continue;
                            if (Interlocked.CompareExchange(ref b->bucket_entries[slot], 0, entry.word) == entry.word) cleared++;
                            else lost++;
                        }
                        var overflow = b->bucket_entries[Constants.kOverflowBucketIndex] & LogAddress.kAddressBitMask;
                        if (overflow == 0) break;
                        b = (HashBucket*)overflowBucketsAllocator.GetPhysicalAddress(overflow);
                    }
                }
                return new(cleared, lost, end, end >= tableSize, false);
            }
            finally
            {
                if (protect) epoch.Suspend();
            }
        }
    }
}
