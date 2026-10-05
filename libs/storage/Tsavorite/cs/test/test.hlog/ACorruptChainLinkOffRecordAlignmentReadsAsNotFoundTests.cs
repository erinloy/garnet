// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Garnet.test;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using Tsavorite.core;
using static Tsavorite.test.TestUtils;

namespace Tsavorite.test
{
    using StructAllocator = SpanByteAllocator<StoreFunctions<KeyStruct.Comparer, SpanByteRecordTriggers>>;
    using StructStoreFunctions = StoreFunctions<KeyStruct.Comparer, SpanByteRecordTriggers>;

    /// <summary>
    /// A CHAIN LINK OFF THE RECORD ALIGNMENT IS A CORRUPT POINTER: THE READ ENDS THERE, NOT FOUND, AND NEVER ISSUES IT.
    ///
    /// <para>Measured on webfrontend's store (2026-10-05): a key's chain reached log:3194. Every record starts on an 8-byte
    /// boundary, and the bytes there were the middle of another record's key text; read as a header they asked for 6,781,120
    /// bytes from a 1 MB page, so three reads made no progress and the read threw. Every start retried it (a kept
    /// dropped-event record) and logged the same Error.</para>
    ///
    /// <para>The device hands back record 0 marked invalid with its previous-address link moved 2 bytes off a record start,
    /// so the walk goes there next. The guard resolves the read as not found after that ONE device read. Without it the
    /// misaligned address is issued, so a second device read is the red.</para>
    /// </summary>
    [TestFixture]
    internal class ACorruptChainLinkOffRecordAlignmentReadsAsNotFoundTests : TestBase
    {
        private const int Records = 700;
        private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);

        /// <summary>A device that serves every read and, once armed, corrupts the record at <see cref="RecordAddress"/> in the
        /// first read that returns it, counting the armed reads.</summary>
        private sealed class CorruptingDevice : StorageDeviceBase
        {
            private readonly IDevice underlying;
            public volatile bool Armed;
            public long RecordAddress;
            public long CorruptLink;
            public int ArmedReads;
            public int Corrupted;

            public CorruptingDevice(IDevice underlying) : base(underlying.FileName, underlying.SectorSize, underlying.Capacity)
                => this.underlying = underlying;

            public override void Initialize(long segmentSize, LightEpoch epoch = null, bool omitSegmentIdFromFilename = false)
            {
                base.Initialize(segmentSize, epoch, omitSegmentIdFromFilename);
                underlying.Initialize(segmentSize, epoch, omitSegmentIdFromFilename);
            }

            public override void RemoveSegmentAsync(int segment, AsyncCallback callback, IAsyncResult result)
                => underlying.RemoveSegmentAsync(segment, callback, result);

            public override void WriteAsync(IntPtr sourceAddress, int segmentId, ulong destinationAddress, uint numBytesToWrite, DeviceIOCompletionCallback callback, object context)
                => underlying.WriteAsync(sourceAddress, segmentId, destinationAddress, numBytesToWrite, callback, context);

            public override void ReadAsync(int segmentId, ulong sourceAddress, IntPtr destinationAddress, uint readLength, DeviceIOCompletionCallback callback, object context)
            {
                if (!Armed)
                {
                    underlying.ReadAsync(segmentId, sourceAddress, destinationAddress, readLength, callback, context);
                    return;
                }
                _ = Interlocked.Increment(ref ArmedReads);
                var recordInSegment = RecordAddress - (segmentId * SegmentSize);
                var offset = recordInSegment - (long)sourceAddress;
                underlying.ReadAsync(segmentId, sourceAddress, destinationAddress, readLength, (errorCode, numBytes, ctx) =>
                {
                    if (errorCode == 0 && offset >= 0 && offset + RecordInfo.Size <= numBytes && Interlocked.CompareExchange(ref Corrupted, 1, 0) == 0)
                    {
                        var word = Marshal.ReadInt64(destinationAddress, (int)offset);
                        ref var info = ref Unsafe.As<long, RecordInfo>(ref word);
                        info.SetInvalid();
                        info.PreviousAddress = CorruptLink;
                        Marshal.WriteInt64(destinationAddress, (int)offset, word);
                    }
                    callback(errorCode, numBytes, ctx);
                }, context);
            }

            public override void Dispose() => underlying.Dispose();
        }

        /// <summary>The shared <see cref="Functions"/> asserts Found in its completion callback; this read's answer is NotFound.</summary>
        private sealed class NotFoundReadFunctions : Functions
        {
            public override void ReadCompletionCallback(ref DiskLogRecord diskLogRecord, ref InputStruct input, ref OutputStruct output, Empty ctx, Status status, RecordMetadata recordMetadata) { }
        }

        [Test]
        [Category("TsavoriteKV")]
        public void A_link_off_the_record_alignment_ends_the_read_as_not_found_with_no_read_of_it()
        {
            DeleteDirectory(MethodTestDir);
            var device = new CorruptingDevice(Devices.CreateLogDevice(Path.Join(MethodTestDir, "corrupting.log"), deleteOnClose: true));
            var store = new TsavoriteKV<StructStoreFunctions, StructAllocator>(
                new()
                {
                    IndexSize = 1L << 26,
                    LogDevice = device,
                    LogMemorySize = 1L << 15,
                    PageSize = MinKvLogPageSize,
                }, StoreFunctions.Create(KeyStruct.Comparer.Instance, SpanByteRecordTriggers.Instance),
                (allocatorSettings, storeFunctions) => new(allocatorSettings, storeFunctions));
            var session = store.NewSession<KeyStruct, InputStruct, OutputStruct, Empty, NotFoundReadFunctions>(new NotFoundReadFunctions());
            try
            {
                var bContext = session.BasicContext;
                for (var i = 0; i < Records; i++)
                {
                    var key = new KeyStruct { kfield1 = i, kfield2 = i + 1 };
                    var value = new ValueStruct { vfield1 = i, vfield2 = i + 1 };
                    _ = bContext.Upsert(key, SpanByte.FromPinnedVariable(ref value), Empty.Default);
                }
                _ = bContext.CompletePending(true);

                // Record 0 is the log's first record; its corrupt link is in range (above BeginAddress, below the tail) and 2 bytes off.
                device.RecordAddress = store.Log.BeginAddress;
                device.CorruptLink = store.Log.BeginAddress + 2;
                ClassicAssert.Less(device.CorruptLink, store.Log.HeadAddress, "the control: the corrupt link is on disk, so only the guard keeps it from being issued");
                device.Armed = true;

                InputStruct input = default;
                OutputStruct output = default;
                var first = new KeyStruct { kfield1 = 0, kfield2 = 1 };
                var status = bContext.Read(first, ref input, ref output, Empty.Default);
                ClassicAssert.IsTrue(status.IsPending, "the control: record 0 must have left memory, or no device read is under test");

                Exception thrown = null;
                var completed = false;
                var completion = Task.Factory.StartNew(() =>
                {
                    try
                    {
                        _ = bContext.CompletePendingWithOutputs(out var outputs, wait: true);
                        using (outputs)
                        {
                            while (outputs.Next())
                                completed = outputs.Current.Status.NotFound;
                        }
                    }
                    catch (Exception ex) { thrown = ex; }
                }, TaskCreationOptions.LongRunning);
                ClassicAssert.IsTrue(completion.Wait(Deadline), $"CompletePending did not return within {Deadline.TotalSeconds:F0} s after {device.ArmedReads} device read(s)");

                ClassicAssert.AreEqual(1, device.Corrupted, "the control: the device corrupted record 0 in the read that returned it");
                ClassicAssert.IsNull(thrown, $"a corrupt link is a chain that ends, not a failed read: {thrown}");
                ClassicAssert.IsTrue(completed, "the read resolves as not found");
                ClassicAssert.AreEqual(1, device.ArmedReads, "the misaligned link is never issued: exactly one device read, the record that held it");
            }
            finally
            {
                device.Armed = false;
                session.Dispose();
                store.Dispose();
                device.Dispose();
                DeleteDirectory(MethodTestDir);
            }
        }
    }
}
