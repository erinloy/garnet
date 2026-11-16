// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

namespace Tsavorite.core
{
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
