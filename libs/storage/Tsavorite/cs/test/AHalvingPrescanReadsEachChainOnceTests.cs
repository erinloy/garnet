// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System.Collections.Generic;
using System.Diagnostics;
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
    /// Ziltch broker 10-30 14:4xZ: a halving's read-only pre-scan was a quarter of the broker's busy CPU (one core, end to end, after every
    /// scrub pass), most of it walking a bucket's chain once for every entry of its sibling: quadratic in a chain's length, and the broker's
    /// chains run ~3 overflow buckets. A pre-scan reads each sibling chain once, and names the same pairs at any step size.
    /// </summary>
    [TestFixture]
    internal class AHalvingPrescanReadsEachChainOnceTests : TestBase
    {
        private const int Keys = 200_000;
        private const long WholeTable = 1L << 40;   // one step over every bucket (not long.MaxValue: the step adds it to its start)

        [SetUp]
        public void Setup() => DeleteDirectory(MethodTestDir, wait: true);

        [TearDown]
        public void TearDown() => OnTearDown();

        private static long Prescan(TsavoriteKV<StructStoreFunctions, StructAllocator> store, long stepBuckets, int cap)
        {
            var found = new List<IndexTagCollision>();
            long pairs = 0;
            for (long at = 0; ;)
            {
                var step = store.FindHalvingCollisions(at, stepBuckets, found, cap);
                ClassicAssert.IsFalse(step.Refused, "CONTROL: no resize is in flight");
                pairs += found.Count;
                found.Clear();
                if (step.Done) return pairs;
                at = step.Next;
            }
        }

        [Test]
        [Category("TsavoriteKV")]
        [Category("Index")]
        public void A_prescan_over_long_chains_reads_each_sibling_chain_once_and_names_the_same_pairs_at_any_step()
        {
            using var log = Devices.CreateLogDevice(Path.Join(MethodTestDir, "prescan.log"), deleteOnClose: true);
            // 8 buckets for 200,000 keys: each bucket's chain holds ~12,800 tags across ~1,800 overflow buckets, so a sibling read once per
            // entry is ~10^8 slot reads a pair.
            using var store = new TsavoriteKV<StructStoreFunctions, StructAllocator>(
                new KVSettings { IndexSize = 1L << 9, LogDevice = log, PageSize = 1L << 16, LogMemorySize = 1L << 24, SegmentSize = 1L << 24 },
                StoreFunctions.Create(KeyStruct.Comparer.Instance, SpanByteRecordTriggers.Instance),
                (allocatorSettings, storeFunctions) => new(allocatorSettings, storeFunctions));
            using (var session = store.NewSession<KeyStruct, InputStruct, OutputStruct, Empty, Functions>(new Functions()))
                for (var i = 0; i < Keys; i++)
                {
                    var key = new KeyStruct { kfield1 = i, kfield2 = i + 1 };
                    var value = new ValueStruct { vfield1 = i, vfield2 = i + 1 };
                    _ = session.BasicContext.Upsert(key, SpanByte.FromPinnedVariable(ref value), Empty.Default);
                }

            _ = Prescan(store, WholeTable, int.MaxValue);   // warm: the JIT and the scratch, so the timed scan is the scan
            var clock = Stopwatch.StartNew();
            var whole = Prescan(store, WholeTable, int.MaxValue);
            var took = clock.Elapsed;
            var byBucket = Prescan(store, 1, int.MaxValue);
            var capped = Prescan(store, WholeTable, 64);
            TestContext.Progress.WriteLine($"{whole:N0} pairs in {took.TotalMilliseconds:F1} ms; by bucket {byBucket:N0}; capped at 64 a step {capped:N0}");

            ClassicAssert.Greater(whole, 10_000, "CONTROL: siblings of long chains share many tags, so the scan has pairs to name");
            ClassicAssert.AreEqual(whole, byBucket, "a step of one bucket names the same pairs: no tag of one bucket's chain is read as another's");
            ClassicAssert.AreEqual(whole, capped, "a step stopped by its cap resumes where it said, and the pairs add up");
            ClassicAssert.Less(took.TotalMilliseconds, 500,
                "the whole table is read once a chain. MUTANT (each high entry walks its low sibling's chain): ~10^8 slot reads a pair, seconds");
        }
    }
}
