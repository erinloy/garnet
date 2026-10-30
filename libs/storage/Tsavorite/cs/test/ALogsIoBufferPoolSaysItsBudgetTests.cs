// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System.Linq;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using Tsavorite.core;

namespace Tsavorite.test
{
    /// <summary>mesh 10-30 07:03Z: the broker's log pool made 17 MB/s of pinned buffers in steady state (SectorAlignedBufferPool.BuildManaged
    /// under compaction and pending reads). A pool says its budgets and how many buffers it made with no permit, so a spent budget is read
    /// on a scrape, not inferred from a trace.</summary>
    [TestFixture]
    [NonParallelizable]
    public class ALogsIoBufferPoolSaysItsBudgetTests
    {
        [Test]
        public void A_pool_past_its_small_budget_counts_every_buffer_it_makes_with_no_permit()
        {
            var budget = SectorAlignedBufferPool.ManagedBudgetBytes;
            SectorAlignedBufferPool.ManagedBudgetBytes = 64L << 10;   // a 16 KB small budget: three 4 KB buffers (4.5 KB each, aligned) hold permits
            try
            {
                var pool = new SectorAlignedBufferPool(1, 512);
                var held = Enumerable.Range(0, 64).Select(_ => pool.Get(4096)).ToArray();
                var counts = pool.Counts;
                ClassicAssert.AreEqual(16L << 10, counts.BudgetSmall, "the small budget is a quarter of the pool's");
                ClassicAssert.AreEqual(64, counts.Made, "CONTROL: every Get from an empty pool made a buffer");
                ClassicAssert.LessOrEqual(counts.ReservedSmall, counts.BudgetSmall, "a permit is never past the budget");
                ClassicAssert.AreEqual(64 - counts.ReservedSmall / 4608, counts.MadeUncached,
                    $"every buffer made past the budget is counted ({counts}). MUTANT (no count): a spent budget reads as a healthy pool");
                foreach (var page in held) page.Return();
                pool.Free();
            }
            finally
            {
                SectorAlignedBufferPool.ManagedBudgetBytes = budget;
            }
        }
    }
}
