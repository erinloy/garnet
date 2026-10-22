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
    /// Compaction with shiftBeginAddress: false copies live records to the tail but leaves BeginAddress unchanged, so the caller can
    /// shift it later. The default behavior (shiftBeginAddress: true) shifts BeginAddress to the compacted address.
    /// </summary>
    [TestFixture]
    internal class CompactionShiftBeginAddressTests : TestBase
    {
        private const int Keys = 200;
        private TsavoriteKV<StructStoreFunctions, StructAllocator> store;
        private ClientSession<KeyStruct, InputStruct, OutputStruct, Empty, QuietFunctions, StructStoreFunctions, StructAllocator> session;
        private BasicContext<KeyStruct, InputStruct, OutputStruct, Empty, QuietFunctions, StructStoreFunctions, StructAllocator> bContext;
        private IDevice log;

        [SetUp]
        public void Setup()
        {
            DeleteDirectory(MethodTestDir, wait: true);
            log = Devices.CreateLogDevice(Path.Join(MethodTestDir, "compaction-shift.log"), deleteOnClose: true);
            store = new(new KVSettings { IndexSize = 1L << 13, LogDevice = log, PageSize = MinKvLogPageSize, LogMemorySize = 1L << 20, SegmentSize = 1L << 22 }
                , StoreFunctions.Create(KeyStruct.Comparer.Instance, SpanByteRecordTriggers.Instance)
                , (allocatorSettings, storeFunctions) => new(allocatorSettings, storeFunctions));
            session = store.NewSession<KeyStruct, InputStruct, OutputStruct, Empty, QuietFunctions>(new QuietFunctions());
            bContext = session.BasicContext;
        }

        [TearDown]
        public void TearDown()
        {
            session?.Dispose(); session = null;
            store?.Dispose(); store = null;
            log?.Dispose(); log = null;
            OnTearDown();
        }

        private static KeyStruct Key(int i) => new() { kfield1 = 1_000 + i, kfield2 = 2_000 + i };

        private Status Read(KeyStruct key, out OutputStruct output)
        {
            InputStruct input = default;
            output = default;
            var status = bContext.Read(key, ref input, ref output, Empty.Default);
            if (status.IsPending)
            {
                _ = bContext.CompletePendingWithOutputs(out var outputs, wait: true);
                (status, output) = GetSinglePendingResult(outputs);
            }
            return status;
        }

        [Test]
        [Category("TsavoriteKV"), Category("Compaction")]
        public void CompactionWithoutShiftingBeginAddressTest([Values] CompactionType type, [Values] bool shift)
        {
            for (var i = 0; i < Keys; i++)
            {
                var value = new ValueStruct { vfield1 = i, vfield2 = -i };
                _ = bContext.Upsert(Key(i), SpanByte.FromPinnedVariable(ref value), Empty.Default);
            }
            store.Log.ShiftReadOnlyAddress(store.Log.TailAddress, wait: true);
            var begin = store.Log.BeginAddress;
            var tail = store.Log.TailAddress;
            var until = store.Log.SafeReadOnlyAddress;

            var reached = shift ? session.Compact(until, type) : session.Compact(until, type, default(DefaultCompactionFunctions), shiftBeginAddress: false);

            ClassicAssert.Greater(reached, begin, "CONTROL: the pass reached past the old BeginAddress");
            ClassicAssert.Greater(store.Log.TailAddress, tail, "CONTROL: the pass copied the live records to the tail");
            ClassicAssert.AreEqual(shift ? reached : begin, store.Log.BeginAddress, shift ? "the default moves BeginAddress to the address reached" : "BeginAddress is left to the caller");
            for (var i = 0; i < Keys; i++)
            {
                var status = Read(Key(i), out var output);
                ClassicAssert.IsTrue(status.Found, $"key {i} reads ({status})");
                ClassicAssert.AreEqual(i, output.value.vfield1, $"key {i} reads its value");
            }

            store.Log.ShiftBeginAddress(reached, truncateLog: false);
            ClassicAssert.AreEqual(reached, store.Log.BeginAddress, "the caller's shift moves BeginAddress");
            for (var i = 0; i < Keys; i++)
                ClassicAssert.IsTrue(Read(Key(i), out _).Found, $"key {i} reads from its copy after the shift");
        }
    }
}
