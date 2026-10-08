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
    /// F2's cold-to-hot copy is a conditional insert of a key the hot log has never held: its tag has no entry in the hot index.
    /// The source record comes from another store, as it does in a two-tier store.
    /// </summary>
    [TestFixture]
    internal class ConditionalInsertOfANeverHeldKeyTests : TestBase
    {
        private TsavoriteKV<StructStoreFunctions, StructAllocator> hot, cold;
        private ClientSession<KeyStruct, InputStruct, OutputStruct, Empty, QuietFunctions, StructStoreFunctions, StructAllocator> hotSession, coldSession;
        private IDevice hotLog, coldLog;

        private TsavoriteKV<StructStoreFunctions, StructAllocator> Open(string name, out IDevice device)
        {
            device = Devices.CreateLogDevice(Path.Join(MethodTestDir, name + ".log"), deleteOnClose: true);
            return new(new KVSettings { IndexSize = 1L << 13, LogDevice = device, PageSize = MinKvLogPageSize, LogMemorySize = 1L << 15, SegmentSize = 1L << 22 }
                , StoreFunctions.Create(KeyStruct.Comparer.Instance, SpanByteRecordTriggers.Instance)
                , (allocatorSettings, storeFunctions) => new(allocatorSettings, storeFunctions));
        }

        [SetUp]
        public void Setup()
        {
            DeleteDirectory(MethodTestDir, wait: true);
            hot = Open("hot", out hotLog);
            cold = Open("cold", out coldLog);
            hotSession = hot.NewSession<KeyStruct, InputStruct, OutputStruct, Empty, QuietFunctions>(new QuietFunctions());
            coldSession = cold.NewSession<KeyStruct, InputStruct, OutputStruct, Empty, QuietFunctions>(new QuietFunctions());
        }

        [TearDown]
        public void TearDown()
        {
            hotSession?.Dispose(); hotSession = null;
            coldSession?.Dispose(); coldSession = null;
            hot?.Dispose(); hot = null;
            cold?.Dispose(); cold = null;
            hotLog?.Dispose(); hotLog = null;
            coldLog?.Dispose(); coldLog = null;
            OnTearDown();
        }

        [Test]
        [Category("TsavoriteKV")]
        public void A_conditional_insert_appends_a_key_this_log_never_held([Values] bool anotherKeyOfItsChainIsHere)
        {
            var key = new KeyStruct { kfield1 = 51, kfield2 = 52 };
            var value = new ValueStruct { vfield1 = 7, vfield2 = 8 };
            _ = coldSession.BasicContext.Upsert(key, SpanByte.FromPinnedVariable(ref value), Empty.Default);   // the key's only copy is in the other store

            var hotContext = hotSession.BasicContext;
            if (anotherKeyOfItsChainIsHere)
            {
                // CONTROL: the test comparer hashes kfield1 alone, so this key shares the tag; the hot index then holds an entry for it.
                var neighbour = new KeyStruct { kfield1 = 51, kfield2 = 99 };
                var other = new ValueStruct { vfield1 = 1, vfield2 = 1 };
                _ = hotContext.Upsert(neighbour, SpanByte.FromPinnedVariable(ref other), Empty.Default);
            }

            var tail = hot.Log.TailAddress;
            using var iter = cold.Log.Scan(cold.Log.BeginAddress, cold.Log.TailAddress);
            ClassicAssert.IsTrue(iter.GetNext(), "CONTROL: the other store holds the record");
            var source = iter as ISourceLogRecord;
            var status = hotContext.ConditionalInsert(in source, 0);   // the key had no chain in this log when the caller looked elsewhere
            if (status.IsPending) _ = hotContext.CompletePending(wait: true);

            ClassicAssert.IsTrue(status.Record.Copied, $"nothing for the key is in this log, so the record is appended: {status}");
            ClassicAssert.Greater(hot.Log.TailAddress, tail, "the log grew by the record");
            InputStruct input = default;
            OutputStruct output = default;
            ClassicAssert.IsTrue(hotContext.Read(key, ref input, ref output).Found, "and the key now reads from this log");
            ClassicAssert.AreEqual(7, output.value.vfield1);
        }
    }
}
