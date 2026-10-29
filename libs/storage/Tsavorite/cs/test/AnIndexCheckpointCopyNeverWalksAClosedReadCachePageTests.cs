// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

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
    /// AN INDEX CHECKPOINT'S COPY OF A BUCKET NEVER WALKS A CLOSED READ-CACHE PAGE (Ziltch broker 10-26).
    ///
    /// <para>The index checkpoint copies a stretch of the hash table and then walks each copied read-cache chain down to its log
    /// record. A read-cache close bumped before the checkpoint thread protected does not wait for it, so the copy can name records
    /// whose page is cleared or refilled by the time it is walked. The broker's 10-26 index checkpoint carried 1,914 entries past the
    /// log's final address and 344 off the record alignment; recovery cleared them, and every key behind them read absent.</para>
    ///
    /// <para>The window is BUILT, not raced: a bucket is copied while its key's chain starts at a read-cache record, the read cache is
    /// filled until that record's page is closed (the live bucket is unlinked, the copy is not), and the copy is then walked. It must
    /// come out naming the key's log record. Before the fix the walk read the refilled page and the entry named some other record's
    /// bytes, or 0.</para>
    /// </summary>
    [TestFixture]
    internal unsafe class AnIndexCheckpointCopyNeverWalksAClosedReadCachePageTests : TestBase
    {
        private TsavoriteKV<StructStoreFunctions, StructAllocator> store;
        private ClientSession<KeyStruct, InputStruct, OutputStruct, Empty, Functions, StructStoreFunctions, StructAllocator> session;
        private IDevice log;

        [SetUp]
        public void Setup()
        {
            DeleteDirectory(MethodTestDir, wait: true);
            log = Devices.CreateLogDevice(Path.Join(MethodTestDir, "copy.log"), deleteOnClose: true);
            store = new(new KVSettings { IndexSize = 1L << 13, LogDevice = log, PageSize = 1L << 16, LogMemorySize = 1L << 20, SegmentSize = 1L << 22,
                                         ReadCacheEnabled = true, ReadCacheMemorySize = 1L << 17, ReadCachePageSize = 1L << 12 }
                , StoreFunctions.Create(KeyStruct.Comparer.Instance, SpanByteRecordTriggers.Instance)
                , (allocatorSettings, storeFunctions) => new(allocatorSettings, storeFunctions));
            session = store.NewSession<KeyStruct, InputStruct, OutputStruct, Empty, Functions>(new Functions());
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
        [Category("CheckpointRestore")]
        public void A_copied_chain_whose_read_cache_page_closed_still_names_its_log_record([Values] bool closed)
        {
            var bContext = session.BasicContext;
            var key = new KeyStruct { kfield1 = 7, kfield2 = 1 };
            var value = new ValueStruct { vfield1 = 7, vfield2 = 8 };
            _ = bContext.Upsert(key, SpanByte.FromPinnedVariable(ref value), Empty.Default);
            store.Log.FlushAndEvict(wait: true);

            HashEntryInfo hei = new(store.storeFunctions.GetKeyHashCode64(key));
            ClassicAssert.IsTrue(store.FindTag(ref hei) && !hei.IsReadCache, "CONTROL: the key's chain starts at its log record");
            var logRecord = hei.Address;

            ClassicAssert.IsTrue(bContext.ConditionalInsertReadCache(key, SpanByte.FromPinnedVariable(ref value), store.Log.TailAddress), "CONTROL: the key is cached");
            hei = new(store.storeFunctions.GetKeyHashCode64(key));
            ClassicAssert.IsTrue(store.FindTag(ref hei) && hei.IsReadCache, "CONTROL: a read-cache record heads the key's chain");
            var cached = LogAddress.AbsoluteAddress(hei.Address);

            HashBucket copy = *hei.bucket;   // the checkpoint's copy, taken while the read-cache record is live

            if (closed)
            {
                var neighbour = 1000;
                while (store.readcacheBase.ClosedUntilAddress <= cached)
                    Cache(neighbour++);
                // Refill the closed frame, as a busy cache does, so a walk of it reads another record's bytes.
                for (var refill = 0; refill < 64; refill++)
                    Cache(neighbour++);
                HashBucketEntry live = default;
                live.word = hei.bucket->bucket_entries[hei.slot];
                ClassicAssert.AreEqual(logRecord, live.Address, "CONTROL: the close unlinked the live bucket; only the copy still names the closed record");
            }

            var before = store.CheckpointClosedReadCacheRewalks;
            store.epoch.Resume();
            try { store.SkipReadCacheBucket(&copy, hei.bucket); }
            finally { store.epoch.Suspend(); }

            HashBucketEntry entry = default;
            entry.word = copy.bucket_entries[hei.slot];
            ClassicAssert.AreEqual(hei.tag, entry.Tag, "the copied entry keeps the key's tag");
            ClassicAssert.AreEqual(logRecord, entry.Address,
                $"the copied entry names the key's log record {logRecord}, not {entry.Address}: a recovered index whose entry names anything else has lost the key");
            ClassicAssert.AreEqual(closed ? 1 : 0, store.CheckpointClosedReadCacheRewalks - before, closed
                ? "the closed chain was walked again from the live bucket"
                : "CONTROL: a live chain is walked once, from the copy");
        }

        private void Cache(int kfield1)
        {
            var key = new KeyStruct { kfield1 = kfield1, kfield2 = 1 };
            var value = new ValueStruct { vfield1 = kfield1, vfield2 = 0 };
            _ = session.BasicContext.ConditionalInsertReadCache(key, SpanByte.FromPinnedVariable(ref value), store.Log.TailAddress);
        }
    }
}
