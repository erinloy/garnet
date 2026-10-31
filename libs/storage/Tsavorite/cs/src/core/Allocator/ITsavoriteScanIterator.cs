// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;

namespace Tsavorite.core
{
    /// <summary>
    /// Scan buffering mode when reading from disk
    /// </summary>
    public enum DiskScanBufferingMode
    {
        /// <summary>
        /// Buffer only current page being scanned
        /// </summary>
        SinglePageBuffering,

        /// <summary>
        /// Buffer current and next page in scan sequence
        /// </summary>
        DoublePageBuffering,

        /// <summary>
        /// Do not buffer - with this mode, you can only scan records already in main memory
        /// </summary>
        NoBuffering,

        /// <summary>
        /// Buffer the current page and read ahead as many pages as fit <see cref="ScanReadAhead.Bytes"/> (at least two): a device
        /// whose reads are slow but concurrent (a remote tier) is read with that many pages in flight instead of one.
        /// </summary>
        MultiPageBuffering
    }

    /// <summary>The read-ahead budget of <see cref="DiskScanBufferingMode.MultiPageBuffering"/>.</summary>
    public static class ScanReadAhead
    {
        /// <summary>Bytes of pages a multi-page scan holds in flight; frames = Bytes / page size, clamped to [2, 256].</summary>
        public static long Bytes = 32L << 20;

        internal static int Frames(int logPageSizeBits) => (int)Math.Clamp(Bytes >> logPageSizeBits, 2, 256);

        /// <summary>Bytes of frame slots one log's scans hold at once, the most its pool ever makes; a scan past it takes what is left
        /// (two at least, a double-buffered scan) or waits for two. The pinned heap never compacts, so slots made past a bound and
        /// dropped stay committed as holes.</summary>
        public static long BudgetBytes = 64L << 20;

        /// <summary>How long a scan past the budget waits for two frames before it takes them past it, counted: a scan opened in
        /// another's sink would otherwise wait on itself.</summary>
        public static TimeSpan BudgetWait = TimeSpan.FromSeconds(10);
    }

    /// <summary>A log's conditional scans by how each record's liveness was decided: pushed with no IO (the index entry, or an in-memory
    /// chain, said it was the newest); pushed after IO (a chain below memory had to be read); superseded (a newer record of its key was
    /// found). IoIssued counts the IOs scans began; ChainRereads counts the further reads those IOs made down a chain on the device,
    /// past records of other keys sharing the tag, or re-reading a record that arrived short. PushedUnproven counts records a scan asked to
    /// push unproven handed out where deciding would have needed a read: live, or superseded by a newer record the caller also receives.</summary>
    public readonly record struct ScanLivenessCounts(long PushedNoIo, long PushedAfterIo, long Superseded, long IoIssued, long ChainRereads, long PushedUnproven = 0);

    /// <summary>A log's scan frames against its budget: what its scans may hold at once, hold now, the slots kept for the next scan,
    /// the slots ever made, and how often a scan was trimmed to what was left, waited for it, or went past it.</summary>
    public readonly record struct ScanFrameCounts(int Budget, int Held, int Kept, long Made, long Trimmed, long Waited, long PastBudget);

    /// <summary>
    /// Scan buffering mode for in-memory records, e.g. for copying and holding a record for Pull iterators
    /// </summary>
    public enum InMemoryScanBufferingMode
    {
        /// <summary>
        /// Buffer the current record being scanned. Automatic for Pull iteration.
        /// </summary>
        CurrentRecordBuffering,

        /// <summary>
        /// Do not buffer - with this mode, Push iteration will hold the epoch during each record's push to the client
        /// </summary>
        NoBuffering
    }

    /// <summary>
    /// Scan iterator interface for Tsavorite log
    /// </summary>
    public interface ITsavoriteScanIterator : ISourceLogRecord, IDisposable
    {
        /// <summary>
        /// Get next record
        /// </summary>
        /// <returns>True if record found, false if end of scan</returns>
        bool GetNext();

        /// <summary>
        /// Current address
        /// </summary>
        long CurrentAddress { get; }

        /// <summary>
        /// Next address
        /// </summary>
        long NextAddress { get; }

        /// <summary>
        /// The starting address of the scan
        /// </summary>
        long BeginAddress { get; }

        /// <summary>
        /// The ending address of the scan
        /// </summary>
        long EndAddress { get; }
    }
}