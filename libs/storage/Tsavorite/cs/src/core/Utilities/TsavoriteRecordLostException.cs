// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;

namespace Tsavorite.core
{
    /// <summary>A lookup compaction's liveness walk lost its chain above the record it was compacting, so it carried that record to
    /// the tail. It could not tell whether a newer record of the key lay behind the lost link, so the carry is named here and
    /// never silent. <paramref name="key"/> is valid only for the call.</summary>
    /// <param name="key">The carried record's key.</param>
    /// <param name="sourceAddress">The address of the record the compaction carried.</param>
    /// <param name="lost">The lost link: <see cref="TsavoriteRecordLostException.Address"/> and <see cref="TsavoriteRecordLostException.LinkedFrom"/>.</param>
    public delegate void CompactionCarriedPastLostLink(ReadOnlySpan<byte> key, long sourceAddress, TsavoriteRecordLostException lost);

    /// <summary>A pending read found no record where it looked: a header no page can hold, or a device that keeps answering the same short
    /// bytes with no error. The record is lost, not absent, so the read fails by this type and a caller tells it from a device fault.</summary>
    public sealed class TsavoriteRecordLostException(string message, long address, long linkedFrom) : TsavoriteException(message)
    {
        /// <summary>The logical address the read followed.</summary>
        public long Address { get; } = address;

        /// <summary>The record whose previous-address link led to <see cref="Address"/>, or kInvalidAddress (0) when the hash index did.</summary>
        public long LinkedFrom { get; } = linkedFrom;
    }
}
