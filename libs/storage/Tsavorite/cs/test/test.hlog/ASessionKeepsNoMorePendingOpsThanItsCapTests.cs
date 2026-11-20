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
    /// A SESSION KEEPS NO MORE PENDING OPS THAN ITS CAP (rg 11-19). A router life held 143K live PendingIoContext (~63 MB): the
    /// per-session pools of pending ops and input wrappers were uncapped, so each session kept its PEAK pending depth for its life,
    /// and a store's pooled sessions live as long as the store. A burst of 4,000 pending reads now leaves at most the cap pooled,
    /// and the steady depth still reuses them. MUTANT (uncapped): 4,000 of each stay pooled.
    /// </summary>
    [TestFixture]
    internal class ASessionKeepsNoMorePendingOpsThanItsCapTests : TestBase
    {
        private const int Keys = 4_000;

        private static KeyStruct Key(int i) => new() { kfield1 = i, kfield2 = i + 1 };

        [Test]
        [Category("TsavoriteKV")]
        public void A_burst_of_pending_reads_leaves_at_most_the_cap_pooled_and_every_read_answers()
        {
            DeleteDirectory(MethodTestDir);
            _ = Directory.CreateDirectory(MethodTestDir);
            using (var device = Devices.CreateLogDevice(Path.Join(MethodTestDir, "burst.log")))
            using (var store = new TsavoriteKV<StructStoreFunctions, StructAllocator>(new()
            {
                IndexSize = 1L << 16,
                LogDevice = device,
                LogMemorySize = 1L << 15,
                PageSize = 1L << 12,
                CheckpointDir = MethodTestDir,
            }, StoreFunctions.Create(KeyStruct.Comparer.Instance, SpanByteRecordTriggers.Instance),
                (allocatorSettings, storeFunctions) => new(allocatorSettings, storeFunctions)))
            {
                using (var writer = store.NewSession<KeyStruct, InputStruct, OutputStruct, Empty, Functions>(new Functions()))
                {
                    var w = writer.BasicContext;
                    for (var i = 0; i < Keys; i++)
                    {
                        var value = new ValueStruct { vfield1 = i, vfield2 = i + 1 };
                        _ = w.Upsert(Key(i), SpanByte.FromPinnedVariable(ref value), Empty.Default);
                    }
                    _ = w.CompletePending(true);
                }
                store.Log.FlushAndEvict(wait: true);

                using var session = store.NewSession<KeyStruct, InputStruct, OutputStruct, Empty, Functions>(new Functions());
                var bContext = session.BasicContext;
                var pending = 0;
                for (var i = 0; i < Keys; i++)
                {
                    InputStruct input = default;
                    OutputStruct output = default;
                    if (bContext.Read(Key(i), ref input, ref output, Empty.Default).IsPending) pending++;
                }
                ClassicAssert.AreEqual(Keys, pending, "CONTROL: every key was evicted, so every read went pending at once");

                var found = 0;
                _ = bContext.CompletePendingWithOutputs(out var outputs, wait: true);
                using (outputs)
                    while (outputs.Next())
                        if (outputs.Current.Status.Found && outputs.Current.Output.value.vfield2 == outputs.Current.Output.value.vfield1 + 1) found++;
                ClassicAssert.AreEqual(Keys, found, "every pending read answered its own value");

                var cap = TsavoriteKV<StructStoreFunctions, StructAllocator>.TsavoriteExecutionContext<InputStruct, OutputStruct, Empty>.PooledPendingOpsMax;
                ClassicAssert.Less(cap, Keys, "CONTROL: the burst is past the cap");
                ClassicAssert.AreEqual(cap, session.ctx.pendingIoContextPool.Count,
                    $"the session keeps the cap of pending ops pooled, not its peak (MUTANT, uncapped: {Keys})");
                ClassicAssert.AreEqual(cap, session.ctx.heapContainerPool.Count,
                    $"and the cap of input wrappers, not its peak (MUTANT, uncapped: {Keys})");

                // the steady depth reuses what is pooled: a second burst under the cap leaves the pools where they were
                for (var i = 0; i < cap / 2; i++)
                {
                    InputStruct input = default;
                    OutputStruct output = default;
                    _ = bContext.Read(Key(i), ref input, ref output, Empty.Default);
                }
                _ = bContext.CompletePending(wait: true);
                ClassicAssert.AreEqual(cap, session.ctx.pendingIoContextPool.Count, "a burst under the cap takes from the pool and gives back to it");
            }
            DeleteDirectory(MethodTestDir);
        }
    }
}
