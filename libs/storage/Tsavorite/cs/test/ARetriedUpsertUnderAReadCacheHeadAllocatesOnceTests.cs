// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.IO;
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
    /// An upsert whose hash-entry CAS fails stashes its allocation and retries. With a read-cache record at the chain head, the retry
    /// bound was that head's address, which carries the read-cache bit and so exceeds every log address: no stash was ever reused, and
    /// each retry allocated a fresh record. One router store's retry storm wrote ~36M dead copies of one record (99 GB, 10-20).
    /// </summary>
    [TestFixture]
    internal class ARetriedUpsertUnderAReadCacheHeadAllocatesOnceTests : TestBase
    {
        private const int Failures = 8;

        private TsavoriteKV<StructStoreFunctions, StructAllocator> store;
        private ClientSession<KeyStruct, InputStruct, OutputStruct, Empty, PoppingFunctions, StructStoreFunctions, StructAllocator> session;
        private IDevice log;

        /// <summary>Each write of the upsert's record first pops the chain's read-cache head, as ReadCacheEvict unlinks one, so its CAS fails.</summary>
        internal sealed class PoppingFunctions : Functions
        {
            internal TsavoriteKV<StructStoreFunctions, StructAllocator> Store;
            internal int Pops, Writes;

            public override bool InitialWriter(ref LogRecord dstLogRecord, in RecordSizeInfo sizeInfo, ref InputStruct input, ReadOnlySpan<byte> srcValue, ref OutputStruct output, ref UpsertInfo upsertInfo)
            {
                Writes++;
                if (Pops > 0)
                {
                    HashEntryInfo hei = new(upsertInfo.KeyHash);
                    ClassicAssert.IsTrue(Store.FindTag(ref hei) && hei.IsReadCache, "CONTROL: a read-cache record heads the chain");
                    var next = LogRecord.GetInfo(Store.readcacheBase.GetPhysicalAddress(hei.Address)).PreviousAddress;
                    ClassicAssert.IsTrue(hei.TryCAS(next), "the pop moved the hash entry");
                    Pops--;
                }
                return base.InitialWriter(ref dstLogRecord, in sizeInfo, ref input, srcValue, ref output, ref upsertInfo);
            }
        }

        [SetUp]
        public void Setup()
        {
            DeleteDirectory(MethodTestDir, wait: true);
            log = Devices.CreateLogDevice(Path.Join(MethodTestDir, "retried.log"), deleteOnClose: true);
            store = new(new KVSettings { IndexSize = 1L << 13, LogDevice = log, PageSize = 1L << 16, LogMemorySize = 1L << 20, SegmentSize = 1L << 22,
                                         ReadCacheEnabled = true, ReadCacheMemorySize = 1L << 20, ReadCachePageSize = 1L << 16 }
                , StoreFunctions.Create(KeyStruct.Comparer.Instance, SpanByteRecordTriggers.Instance)
                , (allocatorSettings, storeFunctions) => new(allocatorSettings, storeFunctions));
            session = store.NewSession<KeyStruct, InputStruct, OutputStruct, Empty, PoppingFunctions>(new PoppingFunctions { Store = store });
        }

        [TearDown]
        public void TearDown()
        {
            session?.Dispose(); session = null;
            store?.Dispose(); store = null;
            log?.Dispose(); log = null;
            OnTearDown();
        }

        [Test]
        [Category("TsavoriteKV")]
        [Category("ReadCache")]
        public void An_upsert_whose_cas_fails_under_a_read_cache_head_writes_one_record([Values] bool readCacheHead)
        {
            var bContext = session.BasicContext;
            var value = new ValueStruct { vfield1 = 7, vfield2 = 8 };
            var key = new KeyStruct { kfield1 = 51, kfield2 = 1 };
            _ = bContext.Upsert(key, SpanByte.FromPinnedVariable(ref value), Empty.Default);
            store.Log.FlushAndEvict(wait: true);   // the key's record is on disk, so the upsert writes a new one

            // The test comparer hashes kfield1 alone: these neighbours share the key's tag, and each enters the read cache at its head.
            var since = store.Log.TailAddress;
            for (var i = 0; i <= Failures; i++)
            {
                var neighbour = new KeyStruct { kfield1 = 51, kfield2 = 100 + i };
                if (readCacheHead)
                    ClassicAssert.IsTrue(bContext.ConditionalInsertReadCache(neighbour, SpanByte.FromPinnedVariable(ref value), since), "CONTROL: the neighbour is cached");
                else
                    _ = bContext.Upsert(neighbour, SpanByte.FromPinnedVariable(ref value), Empty.Default);
            }

            var oneRecord = TailGrowth(() => _ = bContext.Upsert(new KeyStruct { kfield1 = 52, kfield2 = 1 }, SpanByte.FromPinnedVariable(ref value), Empty.Default));
            var functions = session.functions;
            functions.Writes = 0;
            functions.Pops = readCacheHead ? Failures : 0;   // CONTROL arm: a log record heads the chain and nothing fails
            var newValue = new ValueStruct { vfield1 = 9, vfield2 = 10 };
            var grew = TailGrowth(() => _ = bContext.Upsert(key, SpanByte.FromPinnedVariable(ref newValue), Empty.Default));

            ClassicAssert.AreEqual(readCacheHead ? Failures + 1 : 1, functions.Writes, "CONTROL: each popped head failed one CAS, and the write after the last one landed");
            ClassicAssert.AreEqual(oneRecord, grew, $"the retries reuse the first allocation: the log grew by {grew / (double)oneRecord:0.#} records");
            InputStruct input = default;
            OutputStruct output = default;
            ClassicAssert.IsTrue(bContext.Read(key, ref input, ref output).Found, "and the key reads its new value");
            ClassicAssert.AreEqual(9, output.value.vfield1);
        }

        private long TailGrowth(Action write)
        {
            var tail = store.Log.TailAddress;
            write();
            return store.Log.TailAddress - tail;
        }
    }
}
