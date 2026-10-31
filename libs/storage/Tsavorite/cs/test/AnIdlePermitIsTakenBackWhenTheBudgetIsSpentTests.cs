// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using Tsavorite.core;

namespace Tsavorite.test
{
    /// <summary>mesh 10-31 10:35Z: each of the broker's two log pools held its whole 256 MB small budget with 0-2 reads in flight (peak 33), and
    /// 95% of the buffers it made had no permit (mesh_broker 4.96M of 5.23M, chunks 36.4M of 36.7M). The permits sat on caches nobody asked:
    /// a buffer returned on another thread waits on its maker's cross-thread stack until that thread asks the same class again. A pool whose
    /// budget is spent now takes back what idles (cross-thread stacks, the depot, the asking thread's other classes) before it makes a buffer
    /// with no permit.</summary>
    [TestFixture]
    [NonParallelizable]
    public class AnIdlePermitIsTakenBackWhenTheBudgetIsSpentTests
    {
        private const int Asks = 20;

        private static T OnItsOwnThread<T>(Func<T> work)
        {
            T result = default;
            var thread = new Thread(() => result = work()) { IsBackground = true };
            thread.Start();
            ClassicAssert.IsTrue(thread.Join(TimeSpan.FromSeconds(30)), "the thread finished");
            return result;
        }

        private static void WithSmallBudget(Action body)
        {
            var budget = SectorAlignedBufferPool.ManagedBudgetBytes;
            SectorAlignedBufferPool.ManagedBudgetBytes = 64L << 10;   // a 16 KB small budget: three 4 KB buffers (4.5 KB each, aligned) hold permits
            try { body(); }
            finally { SectorAlignedBufferPool.ManagedBudgetBytes = budget; }
        }

        private static long UncachedOverAsks(SectorAlignedBufferPool pool, int bytes) => OnItsOwnThread(() =>
        {
            var before = pool.Counts.MadeUncached;
            for (var i = 0; i < Asks; i++)
                pool.Get(bytes).Return();
            return pool.Counts.MadeUncached - before;
        });

        [Test]
        public void A_permit_parked_on_an_idle_threads_cross_thread_stack_is_taken_back_for_a_thread_that_asks() => WithSmallBudget(() =>
        {
            var pool = new SectorAlignedBufferPool(1, 512);
            using var idle = new ManualResetEventSlim();
            SectorAlignedMemory[] made = null;
            using var gotThem = new ManualResetEventSlim();
            var maker = new Thread(() => { made = Enumerable.Range(0, 3).Select(_ => pool.Get(4096)).ToArray(); gotThem.Set(); idle.Wait(); }) { IsBackground = true };
            maker.Start();
            ClassicAssert.IsTrue(gotThem.Wait(TimeSpan.FromSeconds(30)), "CONTROL: the maker made its buffers");
            foreach (var page in made)
                page.Return();   // a foreign Return, as an IO completion's: onto the maker's cross-thread stack
            var spent = pool.Counts;
            ClassicAssert.AreEqual(0, spent.MadeUncached, "CONTROL: the maker's three buffers each took a permit");
            ClassicAssert.Greater(spent.ReservedSmall + 4608, spent.BudgetSmall, $"CONTROL: the idle stack holds the small budget ({spent})");
            ClassicAssert.AreEqual(0, spent.Reclaimed, "CONTROL: a pool within its budget takes nothing back");

            var uncached = UncachedOverAsks(pool, 4096);
            var after = pool.Counts;
            idle.Set();
            maker.Join();
            ClassicAssert.AreEqual(0, uncached, $"{Asks} asks on a thread whose own caches are empty are served with a permit ({after}). MUTANT (nothing taken back): each is made anew, the GC's on Return");
            ClassicAssert.GreaterOrEqual(after.Reclaimed, 4608, "the permits taken back are counted");
            ClassicAssert.LessOrEqual(after.ReservedSmall, after.BudgetSmall, "a permit is never past the budget");
            pool.Free();
        });

        [Test]
        public void A_permit_on_the_asking_threads_own_other_class_is_taken_back_for_the_class_it_asks() => WithSmallBudget(() =>
        {
            var pool = new SectorAlignedBufferPool(1, 512);
            var uncached = OnItsOwnThread(() =>
            {
                var small = Enumerable.Range(0, 10).Select(_ => pool.Get(1024)).ToArray();   // ten 1.5 KB buffers: the small budget, on one class
                foreach (var page in small)
                    page.Return();                                                          // parked on this thread's own 1 KB stack
                ClassicAssert.Greater(pool.Counts.ReservedSmall + 4608, pool.Counts.BudgetSmall, $"CONTROL: the 1 KB stack holds the small budget ({pool.Counts})");
                var before = pool.Counts.MadeUncached;
                for (var i = 0; i < Asks; i++)
                    pool.Get(4096).Return();
                return pool.Counts.MadeUncached - before;
            });
            ClassicAssert.AreEqual(0, uncached, $"{Asks} asks for a class whose stack is empty take permits from the thread's own idle class ({pool.Counts}). MUTANT (nothing taken back): each is made anew");
            pool.Free();
        });
    }
}
