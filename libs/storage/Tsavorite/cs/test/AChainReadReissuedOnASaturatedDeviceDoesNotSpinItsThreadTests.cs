// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.IO;
using System.Threading.Tasks;
using Garnet.test;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using Tsavorite.core;
using static Tsavorite.test.TestUtils;

namespace Tsavorite.test
{
    using StructStoreFunctions = StoreFunctions<LongKeyComparerModulo, SpanByteRecordTriggers>;

    /// <summary>
    /// A pending read whose disk record is another key's re-issues the read at the chain's previous address. That re-issue went through
    /// AsyncGetFromDisk, which spins its protected thread while the device is past its throttle, though the read that just completed had
    /// already left the device's in-flight count. On Windows the completions that drain the device run on pool workers, so a storm of chain
    /// reads in CompletePendingAsync spun every worker and no completion ran (Ziltch broker 10-26: 20-32 workers in the spin, door dark ~17 min).
    /// </summary>
    [TestFixture]
    internal class AChainReadReissuedOnASaturatedDeviceDoesNotSpinItsThreadTests : TestBase
    {
        private TsavoriteKV<StructStoreFunctions, SpanByteAllocator<StructStoreFunctions>> store;
        private ClientSession<TestSpanByteKey, long, long, Empty, SimpleLongSimpleFunctions, StructStoreFunctions, SpanByteAllocator<StructStoreFunctions>> session;
        private IDevice log;

        [SetUp]
        public void Setup()
        {
            DeleteDirectory(MethodTestDir, wait: true);
            log = Devices.CreateLogDevice(Path.Join(MethodTestDir, "reissue.log"), deleteOnClose: true);
            // Every key hashes alike, so all of them share one chain.
            store = new(new() { IndexSize = 1L << 13, LogDevice = log, PageSize = 1L << 12, LogMemorySize = 1L << 15 }
                , StoreFunctions.Create(new LongKeyComparerModulo(1), SpanByteRecordTriggers.Instance)
                , (allocatorSettings, storeFunctions) => new(allocatorSettings, storeFunctions));
            session = store.NewSession<TestSpanByteKey, long, long, Empty, SimpleLongSimpleFunctions>(new SimpleLongSimpleFunctions());
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
        public void A_chain_read_reissued_while_the_device_is_past_its_throttle_completes()
        {
            var bContext = session.BasicContext;
            for (long key = 0; key < 2; key++)
            {
                var value = key + 100;
                _ = bContext.Upsert(TestSpanByteKey.FromPinnedSpan(SpanByte.FromPinnedVariable(ref key)), SpanByte.FromPinnedVariable(ref value));
            }
            store.Log.FlushAndEvict(wait: true);

            long older = 0, output = 0;
            var status = bContext.Read(TestSpanByteKey.FromPinnedSpan(SpanByte.FromPinnedVariable(ref older)), ref output);
            ClassicAssert.IsTrue(status.IsPending, "CONTROL: the read goes to the device, where the chain's head is the other key");

            var limit = log.ThrottleLimit;
            log.ThrottleLimit = -1;
            if (!log.Throttle())
            {
                log.ThrottleLimit = limit;
                Assert.Ignore($"{log.GetType().Name} does not throttle on its ThrottleLimit");
            }

            var complete = Task.Run(() => bContext.CompletePendingWithOutputs(out var outputs, wait: true) ? outputs : null);
            var done = complete.Wait(TimeSpan.FromSeconds(10));
            log.ThrottleLimit = limit;   // ends a spin, so a red arm does not hang the teardown
            ClassicAssert.IsTrue(done, "the re-issued chain read completes while the device reads as past its throttle, not spinning until it drains");

            using var outputs = complete.Result;
            ClassicAssert.IsTrue(outputs.Next(), "CONTROL: the read completed");
            ClassicAssert.IsTrue(outputs.Current.Status.Found, "the chain's older key is found");
            ClassicAssert.AreEqual(100, outputs.Current.Output);
        }
    }
}
