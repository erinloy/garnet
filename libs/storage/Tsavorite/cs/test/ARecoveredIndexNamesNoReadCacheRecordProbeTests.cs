// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System.IO;
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
    /// PROBE (Ziltch matrix-api 11-04): one key's read-cache copies wedged every life, on two builds. Hypothesis: a hash entry carrying the
    /// read-cache bit survives a checkpoint and recovery (IndexRecovery leaves IsReadCache entries alone), so the new life's empty read
    /// cache allocates below it for good. Keys cached with and without a log record, a fold-over checkpoint, recovery: no recovered entry
    /// names the read cache, and a new offer of each key lands.
    /// </summary>
    [TestFixture]
    internal class ARecoveredIndexNamesNoReadCacheRecordProbeTests : TestBase
    {
        private const int Keys = 512;

        private TsavoriteKV<StructStoreFunctions, StructAllocator> Open(IDevice device)
            => new(new KVSettings { IndexSize = 1L << 13, LogDevice = device, PageSize = 1L << 12, LogMemorySize = 1L << 16, SegmentSize = 1L << 22,
                                    CheckpointDir = MethodTestDir, ReadCacheEnabled = true, ReadCacheMemorySize = 1L << 16, ReadCachePageSize = 1L << 12 },
                StoreFunctions.Create(KeyStruct.Comparer.Instance, SpanByteRecordTriggers.Instance),
                (allocatorSettings, storeFunctions) => new(allocatorSettings, storeFunctions));

        private static KeyStruct Key(int i) => new() { kfield1 = i, kfield2 = i + 1 };

        [SetUp]
        public void Setup() => DeleteDirectory(MethodTestDir, wait: true);

        [TearDown]
        public void TearDown() => OnTearDown();

        [Test]
        [Category("TsavoriteKV")]
        [Category("ReadCache")]
        [Category("CheckpointRestore")]
        public async Task A_recovered_index_names_no_read_cache_record_and_every_key_caches_again([Values(CheckpointType.Snapshot, CheckpointType.FoldOver)] CheckpointType kind)
        {
            System.Guid token;
            var cachedBefore = 0;
            using (var device = Devices.CreateLogDevice(Path.Join(MethodTestDir, "rc.log")))
            using (var store = Open(device))
            using (var session = store.NewSession<KeyStruct, InputStruct, OutputStruct, Empty, Functions>(new Functions()))
            {
                var bContext = session.BasicContext;
                for (var i = 0; i < Keys; i += 2)   // even keys have a log record; odd keys are held only "cold"
                {
                    var v = new ValueStruct { vfield1 = i, vfield2 = i + 1 };
                    _ = bContext.Upsert(Key(i), SpanByte.FromPinnedVariable(ref v), Empty.Default);
                }
                store.Log.FlushAndEvict(wait: true);
                for (var i = 0; i < Keys; i++)
                {
                    var v = new ValueStruct { vfield1 = i, vfield2 = i + 1 };
                    _ = bContext.ConditionalInsertReadCache(Key(i), SpanByte.FromPinnedVariable(ref v), store.Log.TailAddress);
                    HashEntryInfo hei = new(store.storeFunctions.GetKeyHashCode64(Key(i)));
                    if (store.FindTag(ref hei) && hei.IsReadCache) cachedBefore++;
                }
                var (done, t) = await store.TakeFullCheckpointAsync(kind);
                ClassicAssert.IsTrue(done, "the checkpoint completes");
                token = t;
            }
            ClassicAssert.Greater(cachedBefore, Keys / 2, "CONTROL: read-cache records headed most chains when the checkpoint was taken");

            using (var device = Devices.CreateLogDevice(Path.Join(MethodTestDir, "rc.log")))
            using (var store = Open(device))
            using (var session = store.NewSession<KeyStruct, InputStruct, OutputStruct, Empty, Functions>(new Functions()))
            {
                _ = await store.RecoverAsync(token, token);
                var namesReadCache = 0;
                string first = null;
                for (var i = 0; i < Keys; i++)
                {
                    HashEntryInfo hei = new(store.storeFunctions.GetKeyHashCode64(Key(i)));
                    if (store.FindTag(ref hei) && hei.IsReadCache)
                    {
                        namesReadCache++;
                        first ??= $"key {i} recovered with read-cache address {LogAddress.AbsoluteAddress(hei.Address)} (cache tail {store.ReadCache.TailAddress})";
                    }
                }
                ClassicAssert.AreEqual(0, namesReadCache, "no recovered entry names the read cache: " + first);

                var bContext = session.BasicContext;
                for (var i = 1; i < Keys; i += 2)
                {
                    var v = new ValueStruct { vfield1 = i, vfield2 = i + 1 };
                    _ = bContext.ConditionalInsertReadCache(Key(i), SpanByte.FromPinnedVariable(ref v), store.Log.TailAddress);
                }
                ClassicAssert.AreEqual(0, store.ReadCacheAllocateSpunOut, "no new life's copy lands below a recovered head");
            }
        }
    }
}
