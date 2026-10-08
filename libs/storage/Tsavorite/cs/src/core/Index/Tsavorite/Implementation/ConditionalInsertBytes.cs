// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.Runtime.CompilerServices;

namespace Tsavorite.core
{
    public unsafe partial class TsavoriteKV<TStoreFunctions, TAllocator> : TsavoriteBase
        where TStoreFunctions : IStoreFunctions
        where TAllocator : IAllocator<TStoreFunctions>
    {
        /// <summary>
        /// F2's copy from a cold store, from bytes: append (key, value) or the key's tombstone at the tail only if this log holds no record
        /// for the key at or above <paramref name="sinceAddress"/>. The bound is INCLUSIVE: a caller passes the TailAddress it read before it
        /// looked elsewhere, and the next record is written exactly there.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal Status ConditionalInsertBytes<TKey, TInput, TOutput, TContext, TSessionFunctionsWrapper>(TSessionFunctionsWrapper sessionFunctions,
                TKey key, ReadOnlySpan<byte> value, bool tombstone, long sinceAddress)
            where TKey : IKey
#if NET9_0_OR_GREATER
                , allows ref struct
#endif
            where TSessionFunctionsWrapper : ISessionFunctionsWrapper<TInput, TOutput, TContext, TStoreFunctions, TAllocator>
        {
            var keyLength = key.KeyBytes.Length;
            if (keyLength == 0 || keyLength > LogSettings.MaxInlineKeySizeLimit)
                throw new TsavoriteException($"ConditionalInsert: key length {keyLength} is outside 1..{LogSettings.MaxInlineKeySizeLimit}");
            if (value.Length > LogSettings.MaxInlineValueSizeLimit)
                throw new TsavoriteException($"ConditionalInsert: value length {value.Length} exceeds {LogSettings.MaxInlineValueSizeLimit}");
            if (sinceAddress < 0)
                throw new TsavoriteException($"ConditionalInsert: sinceAddress {sinceAddress} is not an address");

            // The image is a SOURCE: all inline whatever this store's allocator. The copy sizes its destination again from the image.
            var sizeInfo = new RecordSizeInfo
            {
                FieldInfo = new() { KeySize = keyLength, ValueSize = value.Length, ExtendedNamespaceSize = RecordNamespace.GetExtendedNamespaceSize(in key) }
            };
            sizeInfo.SetKeyIsInline();
            sizeInfo.MaxInlineValueSize = int.MaxValue;
            sizeInfo.SetValueIsInline();
            sizeInfo.CalculateSizes(keyLength, value.Length);

            var buffer = hlogBase.bufferPool.Get(sizeInfo.AllocatedInlineRecordSize);
            try
            {
                var image = new LogRecord((long)buffer.GetValidPointer());
                image.InfoRef = RecordInfo.InitialValid;
                image.InitializeRecord(key, in sizeInfo);
                if (!image.TrySetValueSpanAndPrepareOptionals(value, in sizeInfo))
                    throw new TsavoriteException("ConditionalInsert: the record image did not take its value");
                if (tombstone)
                    image.InfoRef.SetTombstone();

                // The record form's bound is exclusive (it is the source's own address); one below an aligned address names no record.
                var source = new TransientSourceLogRecord(DiskLogRecord.CreateFromTransientLogRecord(in image));
                return CompactionConditionalCopyToTail<TInput, TOutput, TContext, TSessionFunctionsWrapper, TransientSourceLogRecord>(
                        sessionFunctions, in source, currentAddress: sinceAddress > 0 ? sinceAddress - 1 : 0, minAddress: sinceAddress, createTag: true);
            }
            finally
            {
                buffer.Return();   // always this call's: a pending operation holds its own copy of the record and of the key
            }
        }
    }
}
