// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

namespace Tsavorite.core
{
    /// <summary>
    /// ARE THE 16 BYTES AT AN ADDRESS A RECORD'S HEADER? One definition, shared by the scan iterator's guard (fyre, #90159) and the
    /// pending-read walk (rg), so the two cannot disagree about what a record looks like.
    ///
    /// <para>Each test names one property every written record has. A link into another record's bytes fails at least one of them in
    /// practice: prod webfrontend 11-16, log:137977485728, was 80 bytes into a 216-byte record and asked for 14,418,320 bytes from a
    /// 1 MiB page. Tsavorite cannot write a record larger than a page (TryAllocate throws "Entry does not fit on page").</para>
    ///
    /// <para>An INVALID record whose length is sane IS a header (a failed CAS, a split filler): a scan skips it, it is not corruption.
    /// So the valid bit is not tested here.</para>
    /// </summary>
    public static class RecordHeaderSanity
    {
        // RecordInfo: bits 0-47 previous address (bit 47 = read cache), 48 tombstone, 49 valid, 50 in-new-version, 51 modified,
        // 52 sealed, 53-63 unused (RecordInfo.cs kUnused10..kUnused0).
        private const ulong InfoUnusedMask = ~((1UL << 53) - 1);
        private const long AddressMask = (1L << 48) - 1;
        private const long ReadCacheBit = 1L << 47;

        // RecordDataHeader: bit 1 value inline, bit 2 value object, bit 5 Unused1 (RecordDataHeader.cs).
        private const ulong ValueIsInlineBit = 1UL << 1;
        private const ulong ValueIsObjectBit = 1UL << 2;
        private const ulong DataUnusedBit = 1UL << 5;

        /// <summary>True if the words read at <paramref name="logicalAddress"/> can be a record's header; otherwise false, with the
        /// first property that failed in <paramref name="reason"/>.</summary>
        /// <param name="recordInfoWord">The RecordInfo word (the record's first 8 bytes).</param>
        /// <param name="dataHeaderWord">The RecordDataHeader word (the next 8 bytes).</param>
        /// <param name="logicalAddress">The address the words were read from.</param>
        /// <param name="offsetOnPage">The address's offset on its page.</param>
        /// <param name="pageSize">The log's page size in bytes.</param>
        /// <param name="reason">Why it is not a header; null when it is.</param>
        public static bool IsRecordHeader(long recordInfoWord, ulong dataHeaderWord, long logicalAddress, int offsetOnPage, int pageSize, out string reason)
        {
            if (((ulong)recordInfoWord & InfoUnusedMask) != 0)
            {
                reason = $"record-info reserved bits 53-63 are set (0x{(ulong)recordInfoWord:X16})";
                return false;
            }
            var previous = recordInfoWord & AddressMask;
            if ((previous & ReadCacheBit) != 0)
            {
                reason = $"previous address 0x{previous:X} is in the read cache; a log record never links there";
                return false;
            }
            if (previous >= logicalAddress)
            {
                reason = $"previous address {previous} is not below the record's own {logicalAddress}; a link never points forward";
                return false;
            }
            if ((dataHeaderWord & DataUnusedBit) != 0)
            {
                reason = $"data-header unused bit 5 is set (0x{dataHeaderWord:X16})";
                return false;
            }
            if ((dataHeaderWord & ValueIsInlineBit) != 0 && (dataHeaderWord & ValueIsObjectBit) != 0)
            {
                reason = $"value is both inline and an object (0x{dataHeaderWord:X16})";
                return false;
            }
            var length = new RecordDataHeader { word = dataHeaderWord }.GetRecordLength();
            if (length <= 0 || (long)offsetOnPage + length > pageSize)
            {
                reason = $"length {length} at offset {offsetOnPage} runs past a {pageSize}-byte page";
                return false;
            }
            reason = null;
            return true;
        }
    }
}
