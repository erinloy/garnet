// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.IO;
using System.Runtime.InteropServices;
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
    /// F2's conditional insert of an answer read from a cold store. A record written since the caller's look can move to that store and
    /// the log's begin address pass it; no search of this log sees it then, so an insert whose bound the begin address passed lands nothing.
    /// </summary>
    [TestFixture]
    internal class AConditionalInsertIsRefusedOnceTheBeginAddressPassedItsBoundTests : TestBase
    {
        private TsavoriteKV<StructStoreFunctions, StructAllocator> store;
        private ClientSession<KeyStruct, InputStruct, OutputStruct, Empty, QuietFunctions, StructStoreFunctions, StructAllocator> session;
        private BasicContext<KeyStruct, InputStruct, OutputStruct, Empty, QuietFunctions, StructStoreFunctions, StructAllocator> bContext;
        private IDevice log;

        [SetUp]
        public void Setup()
        {
            DeleteDirectory(MethodTestDir, wait: true);
            log = Devices.CreateLogDevice(Path.Join(MethodTestDir, "hot.log"), deleteOnClose: true);
            store = new(new KVSettings { IndexSize = 1L << 13, LogDevice = log, PageSize = MinKvLogPageSize, LogMemorySize = 1L << 15, SegmentSize = 1L << 22,
                                         KeepDeleteTombstones = true }
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

        private static ReadOnlySpan<byte> Bytes(ref ValueStruct value) => MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref value, 1));

        private void Write(KeyStruct key, long field)
        {
            var value = new ValueStruct { vfield1 = field, vfield2 = field };
            _ = bContext.Upsert(key, SpanByte.FromPinnedVariable(ref value), Empty.Default);
        }

        // The status the insert ended with: a pended one is completed and its completion's status is the answer.
        private Status Ended(Status status)
        {
            if (!status.IsPending)
                return status;
            _ = bContext.CompletePendingWithOutputs(out var completed, wait: true);
            using (completed)
                while (completed.Next())
                    status = completed.Current.Status;
            return status;
        }

        [Test]
        [Category("TsavoriteKV")]
        public void A_record_written_since_the_look_and_cut_out_of_the_log_refuses_the_insert()
        {
            var key = new KeyStruct { kfield1 = 61, kfield2 = 62 };
            var control = new KeyStruct { kfield1 = 71, kfield2 = 72 };
            var stale = new ValueStruct { vfield1 = 3, vfield2 = 3 };

            var begin = store.Log.BeginAddress;
            var since = store.Log.TailAddress;   // the caller reads the tail, finds this log silent, and reads the cold store
            Write(key, 9);                       // a writer's records land meanwhile ...
            Write(control, 9);
            store.Log.ShiftBeginAddress(store.Log.TailAddress);   // ... and a pass takes them cold and cuts the log past them
            ClassicAssert.Greater(store.Log.BeginAddress, since, "CONTROL: the begin address passed the tail the caller read");

            var unbound = Ended(bContext.ConditionalInsert(control, Bytes(ref stale), begin));
            ClassicAssert.IsTrue(unbound.Record.Copied, $"CONTROL: without the bound the stale answer lands above the newer record it cannot see: {unbound}");
            ClassicAssert.AreEqual(0, store.ConditionalInsertsBoundCut);

            var tail = store.Log.TailAddress;
            var status = Ended(bContext.ConditionalInsertUncut(key, Bytes(ref stale), begin, uncutSince: since));
            ClassicAssert.IsTrue(status.IsCanceled && !status.Record.Copied, $"MUTANT (no judge of the bound): the stale 3 is copied. {status}");
            ClassicAssert.AreEqual(tail, store.Log.TailAddress, "nothing appended");
            ClassicAssert.AreEqual(1, store.ConditionalInsertsBoundCut, "and the refusal is counted");
        }

        [Test]
        [Category("TsavoriteKV")]
        public void A_begin_address_at_or_under_the_bound_refuses_nothing()
        {
            var key = new KeyStruct { kfield1 = 81, kfield2 = 82 };
            var newer = new KeyStruct { kfield1 = 83, kfield2 = 84 };
            var value = new ValueStruct { vfield1 = 5, vfield2 = 5 };
            for (var i = 0; i < 40; i++)
                Write(new KeyStruct { kfield1 = 1000 + i, kfield2 = 1 }, i);

            var since = store.Log.TailAddress;
            Write(newer, 9);
            store.Log.ShiftBeginAddress(since);   // a cut up to the bound itself: every record written since the look is still here
            ClassicAssert.AreEqual(since, store.Log.BeginAddress, "CONTROL: the cut reached the bound and did not pass it");

            var landed = Ended(bContext.ConditionalInsertUncut(key, Bytes(ref value), store.Log.BeginAddress, uncutSince: since));
            ClassicAssert.IsTrue(landed.Record.Copied, $"nothing of the key is in the log, and the bound holds: {landed}");
            var found = Ended(bContext.ConditionalInsertUncut(newer, Bytes(ref value), store.Log.BeginAddress, uncutSince: since));
            ClassicAssert.IsTrue(found.Found && !found.Record.Copied, $"the record written since the look is seen, as without the bound: {found}");
            ClassicAssert.AreEqual(0, store.ConditionalInsertsBoundCut);
        }

        [Test]
        [Category("TsavoriteKV")]
        public void A_cut_made_while_the_insert_reads_the_device_refuses_it_at_its_completion([Values] bool cutWhilePending)
        {
            var key = new KeyStruct { kfield1 = 91, kfield2 = 92 };
            var value = new ValueStruct { vfield1 = 5, vfield2 = 5 };
            Write(new KeyStruct { kfield1 = 91, kfield2 = 99 }, 1);   // another key of the same chain, about to leave memory
            for (var i = 0; i < 40; i++)
                Write(new KeyStruct { kfield1 = 2000 + i, kfield2 = 1 }, i);
            store.Log.FlushAndEvict(wait: true);

            var since = store.Log.TailAddress;
            var pended = bContext.ConditionalInsertUncut(key, Bytes(ref value), store.Log.BeginAddress, uncutSince: since);
            ClassicAssert.IsTrue(pended.IsPending, $"CONTROL: the key's chain starts below memory, so the insert reads the device: {pended}");
            if (cutWhilePending)
            {
                Write(new KeyStruct { kfield1 = 3000, kfield2 = 1 }, 1);
                store.Log.ShiftBeginAddress(store.Log.TailAddress);
            }

            var status = Ended(pended);
            if (cutWhilePending)
            {
                ClassicAssert.IsTrue(status.IsCanceled && !status.Record.Copied, $"MUTANT (the completion does not judge the bound): it is copied. {status}");
                ClassicAssert.AreEqual(1, store.ConditionalInsertsBoundCut);
            }
            else
            {
                ClassicAssert.IsTrue(status.Record.Copied, $"CONTROL: with no cut the same insert lands at its completion: {status}");
                ClassicAssert.AreEqual(0, store.ConditionalInsertsBoundCut);
            }
        }
    }
}
