// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.IO;
using System.Threading;
using Garnet.test;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using Tsavorite.core;
using static Tsavorite.test.TestUtils;

namespace Tsavorite.test
{
    using ReasonAllocator = SpanByteAllocator<StoreFunctions<KeyStruct.Comparer, ACopyThatLostItsCasIsDisposedAsACopyTests.ReasonTriggers>>;
    using ReasonStoreFunctions = StoreFunctions<KeyStruct.Comparer, ACopyThatLostItsCasIsDisposedAsACopyTests.ReasonTriggers>;

    /// <summary>
    /// A copy to the tail runs no writer callback, so an application that counts keys at InitialWriter and nets the count back on
    /// InitialWriterCASFailed un-counted a key for every copy that lost its CAS on an empty chain. A lost copy is disposed as CopyCASFailed.
    /// </summary>
    [TestFixture]
    internal unsafe class ACopyThatLostItsCasIsDisposedAsACopyTests : TestBase
    {
        internal struct ReasonTriggers(int[] disposed) : IRecordTriggers
        {
            public readonly bool CallOnFlush => false;
            public readonly bool CallOnEvict => false;
            public readonly bool CallOnDiskRead => false;
            public readonly bool CallPostCopyToTail => false;
            public readonly bool CallOnTruncate => false;
            public readonly void OnDispose(ref LogRecord logRecord, DisposeReason reason) => Interlocked.Increment(ref disposed[(int)reason]);
            public readonly void OnDisposeDiskRecord(ref DiskLogRecord logRecord, DisposeReason reason) { }
            public readonly void OnFlush(ref LogRecord logRecord, long logicalAddress) { }
            public readonly void OnEvict(ref LogRecord logRecord, EvictionSource source) { }
            public readonly void OnDiskRead(ref LogRecord logRecord) { }
            public readonly void OnRecovery(Guid checkpointToken) { }
            public readonly void OnRecoverySnapshotRead(ref LogRecord logRecord) { }
            public readonly void OnCheckpoint(CheckpointTrigger trigger, Guid checkpointToken) { }
            public readonly void OnTruncate(long newBeginAddress) { }
        }

        private TsavoriteKV<ReasonStoreFunctions, ReasonAllocator> store;
        private ClientSession<KeyStruct, InputStruct, OutputStruct, Empty, Functions, ReasonStoreFunctions, ReasonAllocator> session;
        private IDevice log;
        private int[] disposed;

        [SetUp]
        public void Setup()
        {
            DeleteDirectory(MethodTestDir, wait: true);
            log = Devices.CreateLogDevice(Path.Join(MethodTestDir, "lostcopyreason.log"), deleteOnClose: true);
            disposed = new int[Enum.GetValues<DisposeReason>().Length];
            store = new(new KVSettings { IndexSize = 64, LogDevice = log, PageSize = 1L << 16, LogMemorySize = 1L << 20, SegmentSize = 1L << 22 }
                , StoreFunctions.Create(KeyStruct.Comparer.Instance, new ReasonTriggers(disposed))
                , (allocatorSettings, storeFunctions) => new(allocatorSettings, storeFunctions));
            session = store.NewSession<KeyStruct, InputStruct, OutputStruct, Empty, Functions>(new Functions());
        }

        [TearDown]
        public void TearDown()
        {
            if (store != null)
                store.copyBeforeCasForTest = null;
            session?.Dispose(); session = null;
            store?.Dispose(); store = null;
            log?.Dispose(); log = null;
            OnTearDown();
        }

        [Test]
        [Category("TsavoriteKV")]
        public void A_copy_of_a_key_the_log_never_held_that_loses_its_cas_is_not_told_as_a_writers()
        {
            var key = new KeyStruct { kfield1 = 1, kfield2 = 1 };
            var hash = store.storeFunctions.GetKeyHashCode64(key);
            var tag = HashBucketEntry.GetTag(hash);
            KeyStruct other = default;
            for (var k = 2; ; k++)
            {
                other = new KeyStruct { kfield1 = k, kfield2 = 1 };
                if (HashBucketEntry.GetTag(store.storeFunctions.GetKeyHashCode64(other)) == tag)
                    break;
            }
            var otherValue = new ValueStruct { vfield1 = 30, vfield2 = 0 };
            _ = session.BasicContext.Upsert(other, SpanByte.FromPinnedVariable(ref otherValue), Empty.Default);
            var taken = Head(hash);

            // The copy reads an empty chain; another key's record takes the head before the copy's CAS.
            SetHead(hash, tag, 0);
            var tries = 0;
            store.copyBeforeCasForTest = hei =>
            {
                if (tries++ == 0)
                    SetHead(hash, tag, taken);
            };
            var value = new ValueStruct { vfield1 = 2, vfield2 = 0 };
            var since = store.Log.TailAddress;
            var copy = System.Threading.Tasks.Task.Run(() =>
            {
                using var s = store.NewSession<KeyStruct, InputStruct, OutputStruct, Empty, Functions>(new Functions());
                return s.BasicContext.ConditionalInsert(key, SpanByte.FromPinnedVariable(ref value), since);
            });
            ClassicAssert.IsTrue(copy.Wait(10_000), "CONTROL: the copy finished");
            store.copyBeforeCasForTest = null;

            ClassicAssert.AreEqual(2, tries, "CONTROL: the first CAS lost and the copy tried once more");
            ClassicAssert.IsTrue(copy.Result.Record.Copied, "CONTROL: the second try landed");
            ClassicAssert.AreEqual(2L, ReadV(key), "a read finds the copy");
            ClassicAssert.AreEqual(30L, ReadV(other), "the other key's record stays in the chain");
            ClassicAssert.AreEqual(1, disposed[(int)DisposeReason.CopyCASFailed], "the lost copy was disposed as a copy");
            ClassicAssert.AreEqual(0, disposed[(int)DisposeReason.InitialWriterCASFailed],
                "MUTANT (a lost copy told as InitialWriterCASFailed): a key counter nets back a key no writer callback counted");
        }

        private long Head(long hash)
        {
            HashEntryInfo hei = new(hash);
            ClassicAssert.IsTrue(store.FindTag(ref hei), "CONTROL: the key's tag is in the index");
            return hei.Address;
        }

        private void SetHead(long hash, ushort tag, long address)
        {
            HashEntryInfo hei = new(hash);
            ClassicAssert.IsTrue(store.FindTag(ref hei), "CONTROL: the key's tag is in the index");
            hei.firstBucket->bucket_entries[hei.slot] = new HashBucketEntry { Tag = tag, Address = address, Tentative = false }.word;
        }

        private long ReadV(KeyStruct key)
        {
            InputStruct input = default;
            OutputStruct output = default;
            ClassicAssert.IsTrue(session.BasicContext.Read(key, ref input, ref output).Found, "CONTROL: the key is found");
            return output.value.vfield1;
        }
    }
}
