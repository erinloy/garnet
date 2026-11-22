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
    /// A caller that will read many keys whose records left memory orders its reads by where each starts on the device.
    /// ChainAddressBelowMemory answers that address from the index and the records in memory, and issues no device read.
    /// </summary>
    [TestFixture]
    internal class AChainsAddressBelowMemoryIsReadWithoutADeviceReadTests : TestBase
    {
        private TsavoriteKV<StructStoreFunctions, StructAllocator> store;
        private ClientSession<KeyStruct, InputStruct, OutputStruct, Empty, QuietFunctions, StructStoreFunctions, StructAllocator> session;
        private IDevice log;

        [SetUp]
        public void Setup()
        {
            DeleteDirectory(MethodTestDir, wait: true);
            log = Devices.CreateLogDevice(Path.Join(MethodTestDir, "probe.log"), deleteOnClose: true);
            store = new(new KVSettings { IndexSize = 1L << 13, LogDevice = log, PageSize = MinKvLogPageSize, LogMemorySize = 1L << 15, SegmentSize = 1L << 22 }
                , StoreFunctions.Create(KeyStruct.Comparer.Instance, SpanByteRecordTriggers.Instance)
                , (allocatorSettings, storeFunctions) => new(allocatorSettings, storeFunctions));
            session = store.NewSession<KeyStruct, InputStruct, OutputStruct, Empty, QuietFunctions>(new QuietFunctions());
        }

        [TearDown]
        public void TearDown()
        {
            session?.Dispose(); session = null;
            store?.Dispose(); store = null;
            log?.Dispose(); log = null;
            OnTearDown();
        }

        private void Write(KeyStruct key, long value)
        {
            var held = new ValueStruct { vfield1 = value, vfield2 = value };
            _ = session.BasicContext.Upsert(key, SpanByte.FromPinnedVariable(ref held), Empty.Default);
        }

        [Test]
        [Category("TsavoriteKV")]
        public void A_key_whose_record_left_memory_answers_the_address_its_read_would_start_at()
        {
            var context = session.BasicContext;
            var key = new KeyStruct { kfield1 = 51, kfield2 = 52 };
            var never = new KeyStruct { kfield1 = 7001, kfield2 = 1 };
            Write(key, 7);

            ClassicAssert.IsTrue(context.ContainsKeyInMemory(key, out var written).Found, "CONTROL: the record is in memory");
            var inMemory = context.ChainAddressBelowMemory(key, out var at);
            ClassicAssert.IsTrue(inMemory.Found && !inMemory.IsPending, $"a record in memory is found: {inMemory}");
            ClassicAssert.AreEqual(written, at, "at its own address");

            var absent = context.ChainAddressBelowMemory(never, out at);
            ClassicAssert.IsTrue(absent.NotFound && !absent.IsPending, $"a key with no tag is not found: {absent}");
            ClassicAssert.AreEqual(0, at, "and no device read would be issued for it");

            store.Log.FlushAndEvict(wait: true);
            ClassicAssert.Greater(store.Log.HeadAddress, written, "CONTROL: the record left memory");
            ClassicAssert.IsFalse(context.ContainsKeyInMemory(key, out _).Found, "CONTROL: the in-memory probe no longer finds it");

            var below = context.ChainAddressBelowMemory(key, out at);
            ClassicAssert.IsTrue(below.NotFound && !below.IsPending, $"a record below the head is not in memory, and the answer is not pending: {below}");
            ClassicAssert.AreEqual(written, at, "MUTANT (the trace's stop is not answered): the address its read would start at is the record's");

            // the test comparer hashes kfield1 alone: this key shares the tag, and its record is in memory over the first one's
            var neighbour = new KeyStruct { kfield1 = 51, kfield2 = 99 };
            Write(neighbour, 9);
            ClassicAssert.IsTrue(context.ChainAddressBelowMemory(neighbour, out var over).Found, "the neighbour is in memory");
            ClassicAssert.GreaterOrEqual(over, store.Log.HeadAddress);
            var past = context.ChainAddressBelowMemory(key, out at);
            ClassicAssert.IsTrue(past.NotFound, $"the first key is still below the head: {past}");
            ClassicAssert.AreEqual(written, at, "MUTANT (the index entry is answered, not the chain's stop): the trace passes the neighbour in memory");

            InputStruct input = default;
            OutputStruct output = default;
            var read = context.Read(key, ref input, ref output);
            ClassicAssert.IsTrue(read.IsPending, "CONTROL: a read of it does go to the device");
            _ = context.CompletePending(wait: true);

            _ = context.Delete(neighbour);
            var deleted = context.ChainAddressBelowMemory(neighbour, out at);
            ClassicAssert.IsTrue(deleted.NotFound, $"a tombstone in memory is not found: {deleted}");
            ClassicAssert.AreEqual(0, at, "and it answers the read itself: no device read would be issued");

            store.Log.FlushAndEvict(wait: true);
            store.Log.ShiftBeginAddress(store.Log.TailAddress, truncateLog: false);
            var cut = context.ChainAddressBelowMemory(key, out at);
            ClassicAssert.IsTrue(cut.NotFound, $"a chain cut below the begin address is not found: {cut}");
            ClassicAssert.AreEqual(0, at, "and no device read would be issued under the begin address");
        }
    }
}
