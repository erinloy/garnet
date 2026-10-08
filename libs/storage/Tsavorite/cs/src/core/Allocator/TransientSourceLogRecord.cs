// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.Runtime.CompilerServices;

namespace Tsavorite.core
{
    /// <summary>
    /// A record image its caller built and still owns, handed to the engine as a source record. Its bytes do not outlive the call, so it
    /// answers IsPinned = false and a pending operation takes its own copy of the key and of the record: the scan iterators' contract.
    /// </summary>
    internal unsafe struct TransientSourceLogRecord : ISourceLogRecord
    {
        /// <summary>Over the image; it owns no buffer, so a pending operation deep-copies it.</summary>
        internal DiskLogRecord record;

        internal TransientSourceLogRecord(DiskLogRecord record) => this.record = record;

        /// <inheritdoc/>
        public readonly bool IsPinned => false;
        /// <inheritdoc/>
        public readonly bool IsEmpty => false;
        /// <inheritdoc/>
        public readonly ReadOnlySpan<byte> KeyBytes => record.Key;
        /// <inheritdoc/>
        public readonly bool HasNamespace => record.HasNamespace;
        /// <inheritdoc/>
        public readonly ReadOnlySpan<byte> NamespaceBytes => record.NamespaceBytes;

        /// <inheritdoc/>
        public readonly long PhysicalAddress => record.PhysicalAddress;
        /// <inheritdoc/>
        public ref RecordInfo InfoRef => ref record.InfoRef;
        /// <inheritdoc/>
        public readonly RecordInfo Info => record.Info;
        /// <inheritdoc/>
        public readonly RecordDataHeader DataHeader => record.DataHeader;
        /// <inheritdoc/>
        public readonly byte RecordType => record.RecordType;
        /// <inheritdoc/>
        public readonly ReadOnlySpan<byte> Namespace => record.Namespace;
        /// <inheritdoc/>
        public readonly ObjectIdMap ObjectIdMap => record.ObjectIdMap;
        /// <inheritdoc/>
        public readonly bool IsSet => record.IsSet;
        /// <inheritdoc/>
        public readonly ReadOnlySpan<byte> Key => record.Key;
        /// <inheritdoc/>
        public readonly bool IsPinnedKey => record.IsPinnedKey;
        /// <inheritdoc/>
        public readonly byte* PinnedKeyPointer => record.PinnedKeyPointer;
        /// <inheritdoc/>
        public OverflowByteArray KeyOverflow { get => record.KeyOverflow; set => record.KeyOverflow = value; }
        /// <inheritdoc/>
        public readonly Span<byte> ValueSpan => record.ValueSpan;
        /// <inheritdoc/>
        public readonly IHeapObject ValueObject => record.ValueObject;
        /// <inheritdoc/>
        public readonly bool IsPinnedValue => record.IsPinnedValue;
        /// <inheritdoc/>
        public readonly byte* PinnedValuePointer => record.PinnedValuePointer;
        /// <inheritdoc/>
        public OverflowByteArray ValueOverflow { get => record.ValueOverflow; set => record.ValueOverflow = value; }
        /// <inheritdoc/>
        public readonly SpanByteAndMemory ValueSpanByteAndMemory => record.ValueSpanByteAndMemory;
        /// <inheritdoc/>
        public readonly long ETag => record.ETag;
        /// <inheritdoc/>
        public readonly long Expiration => record.Expiration;
        /// <inheritdoc/>
        public readonly void ClearValueIfHeap() { }
        /// <inheritdoc/>
        public readonly bool IsMemoryLogRecord => false;
        /// <inheritdoc/>
        public readonly ref LogRecord AsMemoryLogRecordRef() => throw new TsavoriteException("TransientSourceLogRecord cannot be returned as MemoryLogRecord");
        /// <inheritdoc/>
        public readonly bool IsDiskLogRecord => true;
        /// <inheritdoc/>
        public readonly ref DiskLogRecord AsDiskLogRecordRef() => ref Unsafe.AsRef(in record);
        /// <inheritdoc/>
        public readonly RecordFieldInfo GetRecordFieldInfo() => record.GetRecordFieldInfo();
        /// <inheritdoc/>
        public readonly int AllocatedSize => record.AllocatedSize;
        /// <inheritdoc/>
        public readonly int ActualSize => record.ActualSize;
        /// <inheritdoc/>
        public readonly long CalculateHeapMemorySize() => record.CalculateHeapMemorySize();
    }
}
