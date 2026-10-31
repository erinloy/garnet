// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
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
    /// The mesh store refused to open at 10-31 13:37Z: "Unable to set last valid segment to 438, last available segment on disk
    /// is 121". hlog.log.121 had outlived its truncation (its delete was refused while 122..138 went), and the device's directory
    /// walk took the run from the lowest file, so the log looked like one segment. A refused delete is now held and retried at
    /// every later truncation, and recovery takes the later run of files when the checkpoint's begin and tail both lie in it.
    /// </summary>
    [TestFixture]
    internal class ALogsStaleSegmentFilesAreHeldAndPassedOverTests : TestBase
    {
        private const int Records = 20_000;
        private const int PageBits = 12;
        private const int SegmentBits = 14;

        private TsavoriteKV<StructStoreFunctions, StructAllocator> store;
        private ClientSession<KeyStruct, InputStruct, OutputStruct, Empty, Functions, StructStoreFunctions, StructAllocator> session;
        private IDevice log;
        private string logPath, checkpointDir;

        [SetUp]
        public void Setup()
        {
            DeleteDirectory(MethodTestDir, wait: true);
            logPath = Path.Join(MethodTestDir, "hlog.log");
            checkpointDir = Path.Join(MethodTestDir, "checkpoints");
            Open();
            var bContext = session.BasicContext;
            for (var i = 0; i < Records; i++)
            {
                var key = new KeyStruct { kfield1 = i, kfield2 = i };
                var value = new ValueStruct { vfield1 = i, vfield2 = i };
                _ = bContext.Upsert(key, SpanByte.FromPinnedVariable(ref value), Empty.Default);
            }
            store.Log.FlushAndEvict(wait: true);
        }

        [TearDown]
        public void TearDown()
        {
            Close();
            OnTearDown();
        }

        private void Open()
        {
            log = Devices.CreateLogDevice(logPath, deleteOnClose: false, recoverDevice: true);
            store = new(new KVSettings { IndexSize = 1L << 16, LogDevice = log, PageSize = 1L << PageBits, LogMemorySize = 1L << 15, SegmentSize = 1L << SegmentBits, CheckpointDir = checkpointDir }
                , StoreFunctions.Create(KeyStruct.Comparer.Instance, SpanByteRecordTriggers.Instance)
                , (allocatorSettings, storeFunctions) => new(allocatorSettings, storeFunctions));
            session = store.NewSession<KeyStruct, InputStruct, OutputStruct, Empty, Functions>(new Functions());
        }

        private void Close()
        {
            session?.Dispose(); session = null;
            store?.Dispose(); store = null;
            log?.Dispose(); log = null;
        }

        private string Segment(long segment) => $"{logPath}.{segment}";

        private static long SegmentOf(long address) => address >> SegmentBits;

        private void ShiftBeginToSegment(long segment)
        {
            store.Log.ShiftBeginAddress(segment << SegmentBits, truncateLog: true);
            // Truncation deletes under an epoch bump; wait for the segment below begin that nothing holds to go.
            for (var waited = 0; File.Exists(Segment(segment - 1)) && waited < 100; waited++)
                Thread.Sleep(50);
        }

        private List<long> Keys()
        {
            var keys = new List<long>();
            using var scan = store.Log.Scan(store.Log.BeginAddress, store.Log.TailAddress);
            while (scan.GetNext())
                keys.Add(MemoryMarshal.Read<KeyStruct>(scan.Key).kfield1);
            return keys;
        }

        /// <summary>Checkpoints with begin moved to <paramref name="beginSegment"/>, closes, and returns the records the checkpoint holds.</summary>
        private List<long> CheckpointAndClose(long beginSegment)
        {
            ShiftBeginToSegment(beginSegment);
            _ = store.TryInitiateFullCheckpoint(out _, CheckpointType.FoldOver);
            store.CompleteCheckpointAsync().AsTask().GetAwaiter().GetResult();
            var keys = Keys();
            Close();
            return keys;
        }

        [Test]
        [Category("TsavoriteKV")]
        public void A_segment_whose_delete_is_refused_is_deleted_at_the_next_truncation()
        {
            Assume.That(OperatingSystem.IsWindows(), "a file held open refuses its delete on Windows only");
            ClassicAssert.Greater(SegmentOf(store.Log.TailAddress), 8, "CONTROL: the log spans many segments");

            var holder = new FileStream(Segment(0), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            ShiftBeginToSegment(2);
            ClassicAssert.IsFalse(File.Exists(Segment(1)), "CONTROL: the segment nothing holds is deleted");
            ClassicAssert.IsTrue(File.Exists(Segment(0)), "CONTROL: the held segment's delete was refused");
            ClassicAssert.AreEqual(1, log.SegmentDeletes.Held, "the refused delete is held, not dropped");

            holder.Dispose();
            ShiftBeginToSegment(4);
            ClassicAssert.IsFalse(File.Exists(Segment(0)), "the next truncation deletes the segment its first delete left");
            ClassicAssert.AreEqual(0, log.SegmentDeletes.Held, "nothing is held once the file is gone");
            ClassicAssert.GreaterOrEqual(log.SegmentDeletes.Retried, 1, "the delete was retried");
        }

        [Test]
        [Category("TsavoriteKV")]
        public void A_log_with_a_stale_file_below_a_gap_opens_and_reads_every_record_from_begin()
        {
            var expected = CheckpointAndClose(beginSegment: 6);
            ClassicAssert.Greater(expected.Count, 1000, "CONTROL: the checkpoint holds records from begin to tail");
            ClassicAssert.IsFalse(File.Exists(Segment(5)), "CONTROL: truncation removed the segments below begin");
            File.Copy(Segment(6), Segment(3));

            Open();
            Assert.DoesNotThrow(() => store.RecoverAsync().AsTask().GetAwaiter().GetResult(), "the checkpoint's begin and tail lie in the later run, so the store opens");
            CollectionAssert.AreEqual(expected, Keys(), "every record from begin to tail is read, none from the stale file");
            CollectionAssert.AreEqual(new[] { 3 }, log.SegmentsPassedOver, "the stale file is named");
            ClassicAssert.AreEqual(1, log.SegmentDeletes.Held, "the stale file is held for deletion");

            ShiftBeginToSegment(SegmentOf(store.Log.BeginAddress) + 1);
            ClassicAssert.IsFalse(File.Exists(Segment(3)), "the next truncation deletes the stale file");
        }

        [Test]
        [Category("TsavoriteKV")]
        public void A_stray_file_above_the_tail_is_passed_over_as_before()
        {
            var expected = CheckpointAndClose(beginSegment: 2);
            File.WriteAllBytes(Segment(99_785), []);

            Open();
            Assert.DoesNotThrow(() => store.RecoverAsync().AsTask().GetAwaiter().GetResult(), "a stray above the tail never stopped an open");
            CollectionAssert.AreEqual(expected, Keys(), "every record from begin to tail is read");
            ClassicAssert.Less(log.EndSegment, 99_785, "the stray is not taken as the log's extent");
            ClassicAssert.IsEmpty(log.SegmentsPassedOver, "nothing is passed over below the log");
            ClassicAssert.IsTrue(File.Exists(Segment(99_785)), "the stray is left where it is");
        }

        [Test]
        [Category("TsavoriteKV")]
        public void A_gap_between_begin_and_tail_still_refuses()
        {
            _ = CheckpointAndClose(beginSegment: 2);
            var files = Directory.GetFiles(MethodTestDir, "hlog.log.*");
            ClassicAssert.Greater(files.Length, 6, "CONTROL: the log spans many segment files");
            File.Delete(Segment(4));

            Open();
            var refusal = Assert.Catch(() => store.RecoverAsync().AsTask().GetAwaiter().GetResult(), "a log missing a segment between begin and tail is refused");
            StringAssert.Contains("Unable to set last valid segment", refusal.ToString());
        }
    }
}
