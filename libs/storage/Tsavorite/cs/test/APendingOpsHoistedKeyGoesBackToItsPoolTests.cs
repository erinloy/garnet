// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System.IO;
using System.Text;
using Garnet.test;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using Tsavorite.core;
using static Tsavorite.test.TestUtils;

namespace Tsavorite.test
{
    using SpanByteStoreFunctions = StoreFunctions<SpanByteComparer, SpanByteRecordTriggers>;

    /// <summary>
    /// mesh 10-31: both of the broker's log pools held their whole small budget with 0-2 reads in flight, and 99% of the buffers they made had no
    /// permit. A pending op copies a key past 32 B into a pooled buffer, and the op's state dropped it without returning it whenever the key was not
    /// handed to a completed-output iterator (a conditional copy, a completion with no outputs, a failed one). A buffer never returned keeps its permit
    /// for the life of the pool, so the budget filled with keys nobody held. The op's state now returns its key when it is disposed.
    /// </summary>
    [TestFixture]
    internal class APendingOpsHoistedKeyGoesBackToItsPoolTests : TestBase
    {
        private const int Records = 400;
        private const int KeyBytes = 64;   // past ConditionallyHoistedKey.InlineCapacity, as every RG key is

        private static byte[] KeyOf(int i) => Encoding.ASCII.GetBytes($"rg-store|mesh:broker|node:{i:D8}".PadRight(KeyBytes, '.'));

        [Test]
        [Category("TsavoriteKV")]
        public void A_pending_read_completed_without_outputs_returns_its_key_so_the_next_round_makes_no_buffer()
        {
            DeleteDirectory(MethodTestDir, wait: true);
            using var log = Devices.CreateLogDevice(Path.Join(MethodTestDir, "hoist.log"), deleteOnClose: true);
            using var store = new TsavoriteKV<SpanByteStoreFunctions, SpanByteAllocator<SpanByteStoreFunctions>>(
                new() { IndexSize = 1L << 13, LogDevice = log, LogMemorySize = 1L << 15, PageSize = 1L << 12 },
                StoreFunctions.Create(SpanByteComparer.Instance, SpanByteRecordTriggers.Instance),
                (allocatorSettings, storeFunctions) => new(allocatorSettings, storeFunctions));
            using var session = store.NewSession<TestSpanByteKey, PinnedSpanByte, SpanByteAndMemory, Empty, SpanByteFunctions<Empty>>(new SpanByteFunctions<Empty>());
            var bContext = session.BasicContext;

            var value = Encoding.ASCII.GetBytes("value");
            for (var i = 0; i < Records; i++)
                _ = bContext.Upsert(TestSpanByteKey.FromArray(KeyOf(i)), value);
            store.Log.FlushAndEvict(wait: true);
            ClassicAssert.AreEqual(store.Log.TailAddress, store.Log.HeadAddress, "CONTROL: every record is below head, so every read goes pending");

            long Round()
            {
                var before = store.IoBuffers.Made;
                PinnedSpanByte input = default;
                var pending = 0;
                for (var i = 0; i < Records; i++)
                {
                    var output = new SpanByteAndMemory();
                    var status = bContext.Read(TestSpanByteKey.FromArray(KeyOf(i)), ref input, ref output);
                    if (status.IsPending) pending++;
                    output.Memory?.Dispose();
                }
                _ = bContext.CompletePending(wait: true);
                ClassicAssert.AreEqual(Records, pending, "CONTROL: each read went to the device");
                return store.IoBuffers.Made - before;
            }

            _ = Round();   // first round fills the pool's caches
            _ = Round();
            var made = Round();
            TestContext.Progress.WriteLine($"{Records} pending reads of {KeyBytes} B keys made {made} pool buffer(s) in their third round ({store.IoBuffers})");
            ClassicAssert.Less(made, Records / 10,
                $"a round of {Records} pending reads reuses what the last round returned ({store.IoBuffers}). MUTANT (the op's state drops its key): every key is a new buffer that never comes back, and its permit with it");
        }
    }
}
