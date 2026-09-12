// Copyright (c) Microsoft Corporation. Licensed under the MIT license.

using System.Threading;

namespace Tsavorite.core
{
    /// <summary>
    /// HOW MUCH OF THIS STORE IS GONE, AS A NUMBER SOMEBODY CAN READ.
    /// <para>
    /// A pending read that completes without producing a record is treated as ABSENT by
    /// <c>ContinuePendingRead</c> rather than dereferenced. That is the ruled behaviour - a read of gone log returns
    /// absent, never an error - but absent-and-SILENT would hide the very data loss worth naming: a cell that boots
    /// clean and a cell that boots having dropped part of its state would look identical from outside.
    /// </para>
    /// <para>
    /// So every such read is counted here and the most recent address is kept. The COUNT is the useful half - its
    /// derivative answers the question a point value cannot: is the damage BOUNDED, or still growing? A store
    /// carrying old damage from a defect that has since been fixed shows a count that rises during a boot scan and
    /// then stops; a store still being damaged shows one that keeps climbing while the cell serves. Those need
    /// opposite responses, and nothing else distinguishes them.
    /// </para>
    /// <para>
    /// Deliberately a plain static: this is engine-level and must not depend on a logger, a meter, or a DI graph
    /// that the read path does not have and cannot afford. The host reads <see cref="Count"/> and publishes it as
    /// its own gauge; the engine only counts. Lock-free and allocation-free, because the site that increments it is
    /// a completion callback on the read path.
    /// </para>
    /// </summary>
    public static class UndeliveredPendingReads
    {
        private static long count;
        private static long lastAddress = -1;

        /// <summary>Records one pending read that completed without a record, at <paramref name="logicalAddress"/>.</summary>
        public static void Record(long logicalAddress)
        {
            _ = Interlocked.Increment(ref count);
            _ = Interlocked.Exchange(ref lastAddress, logicalAddress);
        }

        /// <summary>How many pending reads have completed without a record in this process.</summary>
        public static long Count => Interlocked.Read(ref count);

        /// <summary>The most recent such address, or -1 if there has been none.</summary>
        public static long LastAddress => Interlocked.Read(ref lastAddress);

        /// <summary>Resets both. For tests only - a process that resets this in production is erasing its own evidence.</summary>
        public static void ResetForTests()
        {
            _ = Interlocked.Exchange(ref count, 0);
            _ = Interlocked.Exchange(ref lastAddress, -1);
        }
    }
}
