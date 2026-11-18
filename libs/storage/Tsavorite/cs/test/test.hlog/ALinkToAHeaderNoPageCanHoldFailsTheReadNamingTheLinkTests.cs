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
    /// A LINK TO A HEADER NO PAGE CAN HOLD FAILS THE READ AT ONCE, AND NAMES THE LINK.
    ///
    /// <para>Measured on prod webfrontend (2026-11-16): the log-budget compaction pass failed every time on one chain link,
    /// log:137977485728. That address is 8-aligned, so the alignment guard passes it, but it is 80 bytes inside a real 216-byte
    /// record. Read as a header, its bytes ask for 14,418,320 bytes starting 612,768 bytes into a 1 MiB page. No record spans a page,
    /// so the page-end clamp re-read the same 435,808 bytes until the no-progress limit threw. That throw failed the whole pass
    /// and named no link.</para>
    ///
    /// <para>The shape here is the same, on a real link. Key 0 is written twice: R1 is the log's first record and R2 a later one,
    /// and R2's previous-address link is R1. The device hands back R2 marked invalid, so the read follows R2's link to R1. It hands
    /// back R1 with a value length that runs past its page, while R1's key still matches. The guard fails the read on R1's first
    /// read with <see cref="TsavoriteRecordLostException"/>, with Address = R1 and LinkedFrom = R2. Without the guard, R1 is re-read
    /// at the impossible length, so a third device read, or a failure that does not name the link, is the red.</para>
    /// </summary>
    [TestFixture]
    internal class ALinkToAHeaderNoPageCanHoldFailsTheReadNamingTheLinkTests : TestBase
    {
        private const int Records = 700;
        private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);

        // RecordDataHeader: ValueLength is bits 24-47 of its word (24 bits), read only while ValueIsInline (bit 1) is set
        private const int ValueLengthShift = 24;
        private const ulong ValueLengthMask = (1UL << 24) - 1;
        private const ulong ValueIsInlineMask = 1UL << 1;

        /// <summary>A device that serves every read. Once armed, it marks the record at <see cref="InvalidAddress"/> invalid and
        /// gives the record at <see cref="OversizedAddress"/> the largest value length, in every read that returns them, the way a
        /// corrupt link reads the same wrong bytes every time. It counts the armed reads.</summary>
        private sealed class CorruptingDevice : StorageDeviceBase
        {
            private readonly IDevice underlying;
            public volatile bool Armed;
            public long InvalidAddress;
            public long OversizedAddress;
            public int ArmedReads;
            public int Invalidated;
            public int Oversized;

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
                var invalidAt = InvalidAddress - (segmentId * SegmentSize) - (long)sourceAddress;
                var oversizedAt = OversizedAddress - (segmentId * SegmentSize) - (long)sourceAddress;
                underlying.ReadAsync(segmentId, sourceAddress, destinationAddress, readLength, (errorCode, numBytes, ctx) =>
                {
                    if (errorCode == 0 && invalidAt >= 0 && invalidAt + RecordInfo.Size <= numBytes)
                    {
                        var word = Marshal.ReadInt64(destinationAddress, (int)invalidAt);
                        ref var info = ref Unsafe.As<long, RecordInfo>(ref word);
                        info.SetInvalid();
                        Marshal.WriteInt64(destinationAddress, (int)invalidAt, word);
                        _ = Interlocked.Increment(ref Invalidated);
                    }
                    if (errorCode == 0 && oversizedAt >= 0 && oversizedAt + RecordInfo.Size + RecordDataHeader.Size <= numBytes)
                    {
                        var at = (int)oversizedAt + RecordInfo.Size;
                        var header = (ulong)Marshal.ReadInt64(destinationAddress, at);
                        if ((header & ValueIsInlineMask) != 0)
                        {
                            header = (header & ~(ValueLengthMask << ValueLengthShift)) | (ValueLengthMask << ValueLengthShift);
                            Marshal.WriteInt64(destinationAddress, at, (long)header);
                            _ = Interlocked.Increment(ref Oversized);
                        }
                    }
                    callback(errorCode, numBytes, ctx);
                }, context);
            }

            public override void Dispose() => underlying.Dispose();
        }

        /// <summary>The shared <see cref="Functions"/> asserts Found in its completion callback; this read fails instead.</summary>
        private sealed class QuietReadFunctions : Functions
        {
            public override void ReadCompletionCallback(ref DiskLogRecord diskLogRecord, ref InputStruct input, ref OutputStruct output, Empty ctx, Status status, RecordMetadata recordMetadata) { }
        }

        private static TsavoriteRecordLostException LostIn(Exception ex)
        {
            for (var e = ex; e is not null; e = e.InnerException)
            {
                if (e is TsavoriteRecordLostException lost)
                    return lost;
                if (e is AggregateException agg && agg.InnerExceptions.Count == 1)
                    return LostIn(agg.InnerExceptions[0]);
            }
            return null;
        }

        [Test]
        [Category("TsavoriteKV")]
        public void A_link_to_a_header_no_page_can_hold_fails_the_read_at_once_naming_the_link()
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
            var session = store.NewSession<KeyStruct, InputStruct, OutputStruct, Empty, QuietReadFunctions>(new QuietReadFunctions());
            try
            {
                var bContext = session.BasicContext;
                void Write(int k, int v)
                {
                    var key = new KeyStruct { kfield1 = k, kfield2 = k + 1 };
                    var value = new ValueStruct { vfield1 = v, vfield2 = v + 1 };
                    _ = bContext.Upsert(key, SpanByte.FromPinnedVariable(ref value), Empty.Default);
                }

                for (var i = 0; i < Records; i++)
                    Write(i, i);
                Write(0, -1);                           // R2: key 0 again, its previous-address link R1
                for (var i = Records; i < 3 * Records; i++)
                    Write(i, i);
                _ = bContext.CompletePending(true);

                var first = new KeyStruct { kfield1 = 0, kfield2 = 1 };
                InputStruct input = default;
                OutputStruct output = default;

                // the control: an unarmed read of key 0 goes to the device and finds R2, which names R2's address
                var status = bContext.Read(first, ref input, ref output, Empty.Default);
                ClassicAssert.IsTrue(status.IsPending, "the control: R2 must have left memory, or no device read is under test");
                long r2 = 0;
                _ = bContext.CompletePendingWithOutputs(out var unarmed, wait: true);
                using (unarmed)
                {
                    while (unarmed.Next())
                    {
                        ClassicAssert.IsTrue(unarmed.Current.Status.Found, "the control: key 0 reads as found before the device is armed");
                        r2 = unarmed.Current.RecordMetadata.Address;
                    }
                }
                var r1 = store.Log.BeginAddress;
                ClassicAssert.Greater(r2, r1, "the control: R2 is the later copy of key 0");
                ClassicAssert.Less(r2, store.Log.HeadAddress, "the control: R2 is on disk");

                device.InvalidAddress = r2;
                device.OversizedAddress = r1;
                device.Armed = true;

                status = bContext.Read(first, ref input, ref output, Empty.Default);
                ClassicAssert.IsTrue(status.IsPending, "the control: the armed read goes to the device");

                Exception thrown = null;
                var completion = Task.Factory.StartNew(() =>
                {
                    try
                    {
                        _ = bContext.CompletePendingWithOutputs(out var outputs, wait: true);
                        using (outputs)
                        {
                            while (outputs.Next()) { }
                        }
                    }
                    catch (Exception ex) { thrown = ex; }
                }, TaskCreationOptions.LongRunning);
                ClassicAssert.IsTrue(completion.Wait(Deadline), $"CompletePending did not return within {Deadline.TotalSeconds:F0} s after {device.ArmedReads} device read(s)");

                ClassicAssert.GreaterOrEqual(device.Invalidated, 1, "the control: the device handed back R2 marked invalid");
                ClassicAssert.GreaterOrEqual(device.Oversized, 1, "the control: the device handed back R1 with a header past its page");
                var lost = LostIn(thrown);
                ClassicAssert.IsNotNull(lost, $"the read fails as a lost record, never as not found or a bare engine error: {thrown?.ToString() ?? "(no exception: answered)"}");
                StringAssert.Contains("no record spans a page", lost.Message, "it fails on the header's length, not on the no-progress limit");
                ClassicAssert.AreEqual(r1, lost.Address, "it names the address the link led to");
                ClassicAssert.AreEqual(r2, lost.LinkedFrom, "it names the record whose link led there");
                ClassicAssert.AreEqual(2, device.ArmedReads, "at once: R2, then R1's first read, and the impossible length is never issued");
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

        /// <summary>
        /// THE SAME LOST LINK IN A LOOKUP COMPACTION'S LIVENESS WALK CARRIES THE RECORD, AND NAMES THE CARRY.
        ///
        /// <para>Key 0 is written at R1, the log's first record, and again at R2, above the compaction's end. The walk for R1 asks whether
        /// a newer key 0 lies above it, and it reaches R2, whose header runs past its page. Failing there would fail the pass on every
        /// run, as on prod; dropping R1 would lose the key. So R1 is carried to the tail, and the hook is called once with the key, R1,
        /// R2, and the index as the link (0). The cost is named, not hidden: the carried copy is now key 0's newest record, so a read
        /// answers R1's value. That is why every carry is a row (echo #90162). Mutant: without the catch, the pass throws.</para>
        /// </summary>
        [Test]
        [Category("TsavoriteKV"), Category("Compaction")]
        public void A_lookup_compaction_carries_a_record_past_a_lost_link_above_it_and_names_the_carry()
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
            var session = store.NewSession<KeyStruct, InputStruct, OutputStruct, Empty, QuietReadFunctions>(new QuietReadFunctions());
            try
            {
                var bContext = session.BasicContext;
                void Write(int k, int v)
                {
                    var key = new KeyStruct { kfield1 = k, kfield2 = k + 1 };
                    var value = new ValueStruct { vfield1 = v, vfield2 = v + 1 };
                    _ = bContext.Upsert(key, SpanByte.FromPinnedVariable(ref value), Empty.Default);
                }
                (Status status, OutputStruct output, long address) Read(int k, bool onDisk = true)
                {
                    var key = new KeyStruct { kfield1 = k, kfield2 = k + 1 };
                    InputStruct input = default;
                    OutputStruct output = default;
                    ReadOptions readOptions = default;
                    var status = bContext.Read(key, ref input, ref output, ref readOptions, out var recordMetadata, Empty.Default);
                    if (!onDisk)
                        return (status, output, recordMetadata.Address);
                    ClassicAssert.IsTrue(status.IsPending, $"the control: key {k} has left memory");
                    _ = bContext.CompletePendingWithOutputs(out var outputs, wait: true);
                    (status, output) = GetSinglePendingResult(outputs, out var metadata);
                    return (status, output, metadata.Address);
                }

                Write(0, 7);                            // R1
                for (var i = 1; i < Records; i++)
                    Write(i, i);
                Write(0, 8);                            // R2, above the compaction's end
                for (var i = Records; i < 3 * Records; i++)
                    Write(i, i);
                _ = bContext.CompletePending(true);

                var r1 = store.Log.BeginAddress;
                var (found, newest, r2) = Read(0);
                ClassicAssert.IsTrue(found.Found && newest.value.vfield1 == 8, "the control: key 0 reads R2's value before the device is armed");
                var until = Read(Records / 2).address;
                ClassicAssert.Less(r1, until, "the control: R1 is in the compacted range");
                ClassicAssert.Less(until, r2, "the control: R2 is above the compaction's end, so the walk for R1 must read it");
                ClassicAssert.Less(r2, store.Log.HeadAddress, "the control: R2 is on disk");

                var rows = new System.Collections.Generic.List<(long key, long source, long address, long linkedFrom)>();
                store.OnCompactionCarriedPastLostLink = (key, source, lost) =>
                    rows.Add((MemoryMarshal.Read<KeyStruct>(key).kfield1, source, lost.Address, lost.LinkedFrom));
                device.InvalidAddress = long.MinValue / 2;  // nothing is marked invalid in this arm
                device.OversizedAddress = r2;
                device.Armed = true;

                Exception thrown = null;
                long reached = 0;
                var completion = Task.Factory.StartNew(() =>
                {
                    try { reached = session.Compact(until, CompactionType.Lookup); }
                    catch (Exception ex) { thrown = ex; }
                }, TaskCreationOptions.LongRunning);
                ClassicAssert.IsTrue(completion.Wait(Deadline), $"the compaction did not return within {Deadline.TotalSeconds:F0} s after {device.ArmedReads} device read(s)");
                device.Armed = false;

                ClassicAssert.GreaterOrEqual(device.Oversized, 1, "the control: the walk read R2 with a header past its page");
                ClassicAssert.IsNull(thrown, $"a lost link above a compacted record carries it; it never fails the pass: {thrown}");
                ClassicAssert.AreEqual(store.Log.BeginAddress, reached, "the pass completed and moved BeginAddress");
                ClassicAssert.AreEqual(1, rows.Count, "one carry, one row");
                ClassicAssert.AreEqual((0L, r1, r2, 0L), rows[0], "the row names the key, the carried record, the lost address, and its link (0: the hash index)");
                ClassicAssert.AreEqual(1, store.CompactionCarriedPastLostLinks, "the carry is counted");

                var (after, carried, _) = Read(0, onDisk: false);   // the carry is at the tail, in memory
                ClassicAssert.IsTrue(after.Found, "carried, never dropped: key 0 is still found");
                ClassicAssert.AreEqual(7, carried.value.vfield1, "the carried R1 is now key 0's newest record, which is the cost the row names");
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

        /// <summary>
        /// A TOMBSTONE BEHIND THE LOST LINK: THE CARRY REVIVES A DELETED KEY, AND THE ROW NAMES IT (echo #90162).
        ///
        /// <para>Key 0 is written at R1 (in the compacted range), deleted at T above the compaction's end, and written again at R2 above T,
        /// whose header runs past its page. The walk for R1 reaches R2 first and loses the chain there, so it never sees T: R1 is carried,
        /// and key 0, which T deleted, reads R1's value again. Carry is still the default, since a drop cannot be undone, but the row
        /// (key, R1, R2, the index) is what lets the cell reconcile the key once the span is judged.</para>
        /// </summary>
        [Test]
        [Category("TsavoriteKV"), Category("Compaction")]
        public void A_tombstone_behind_a_lost_link_is_revived_by_the_carry_and_the_row_names_the_key()
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
            var session = store.NewSession<KeyStruct, InputStruct, OutputStruct, Empty, QuietReadFunctions>(new QuietReadFunctions());
            try
            {
                var bContext = session.BasicContext;
                void Write(int k, int v)
                {
                    var key = new KeyStruct { kfield1 = k, kfield2 = k + 1 };
                    var value = new ValueStruct { vfield1 = v, vfield2 = v + 1 };
                    _ = bContext.Upsert(key, SpanByte.FromPinnedVariable(ref value), Empty.Default);
                }
                (Status status, OutputStruct output, long address) Read(int k, bool onDisk = true)
                {
                    var key = new KeyStruct { kfield1 = k, kfield2 = k + 1 };
                    InputStruct input = default;
                    OutputStruct output = default;
                    ReadOptions readOptions = default;
                    var status = bContext.Read(key, ref input, ref output, ref readOptions, out var recordMetadata, Empty.Default);
                    if (!onDisk || !status.IsPending)
                        return (status, output, recordMetadata.Address);
                    _ = bContext.CompletePendingWithOutputs(out var outputs, wait: true);
                    (status, output) = GetSinglePendingResult(outputs, out var metadata);
                    return (status, output, metadata.Address);
                }

                Write(0, 7);                            // R1, in the compacted range
                for (var i = 1; i < Records; i++)
                    Write(i, i);
                var deleted = new KeyStruct { kfield1 = 0, kfield2 = 1 };
                _ = bContext.Delete(deleted, Empty.Default);   // T, above the compaction's end
                for (var i = Records; i < 2 * Records; i++)
                    Write(i, i);
                Write(0, 8);                            // R2, above T: the walk meets it first
                for (var i = 2 * Records; i < 3 * Records; i++)
                    Write(i, i);
                _ = bContext.CompletePending(true);

                var r1 = store.Log.BeginAddress;
                var (found, newest, r2) = Read(0);
                ClassicAssert.IsTrue(found.Found && newest.value.vfield1 == 8, "the control: key 0 reads R2's value before the device is armed");
                var until = Read(Records / 2).address;
                ClassicAssert.Less(r1, until, "the control: R1 is in the compacted range");
                ClassicAssert.Less(until, r2, "the control: R2 (and T below it) is above the compaction's end");
                ClassicAssert.Less(r2, store.Log.HeadAddress, "the control: R2 is on disk");

                var rows = new System.Collections.Generic.List<(long key, long source, long address, long linkedFrom)>();
                store.OnCompactionCarriedPastLostLink = (key, source, lost) =>
                    rows.Add((MemoryMarshal.Read<KeyStruct>(key).kfield1, source, lost.Address, lost.LinkedFrom));
                device.InvalidAddress = long.MinValue / 2;
                device.OversizedAddress = r2;
                device.Armed = true;

                Exception thrown = null;
                var completion = Task.Factory.StartNew(() =>
                {
                    try { _ = session.Compact(until, CompactionType.Lookup); }
                    catch (Exception ex) { thrown = ex; }
                }, TaskCreationOptions.LongRunning);
                ClassicAssert.IsTrue(completion.Wait(Deadline), $"the compaction did not return within {Deadline.TotalSeconds:F0} s after {device.ArmedReads} device read(s)");
                device.Armed = false;

                ClassicAssert.IsNull(thrown, $"a lost link above a compacted record carries it: {thrown}");
                ClassicAssert.AreEqual(1, rows.Count, "one carry, one row");
                ClassicAssert.AreEqual((0L, r1, r2, 0L), rows[0], "the row names the deleted key, the carried record, the lost address and its link");

                var (after, revived, _) = Read(0, onDisk: false);
                ClassicAssert.IsTrue(after.Found, "the carry revived key 0, which T deleted: the cost the row names");
                ClassicAssert.AreEqual(7, revived.value.vfield1, "the revived value is R1's");
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
