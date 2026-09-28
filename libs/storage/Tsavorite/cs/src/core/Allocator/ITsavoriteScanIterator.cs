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
    }

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