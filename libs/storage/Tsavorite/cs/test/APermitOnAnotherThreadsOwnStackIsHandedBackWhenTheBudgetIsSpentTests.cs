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
    /// <summary>mesh 10-31 12:45Z, after the idle take-back: the chunk store's 256 MB small budget was still wholly reserved, 54.7M of 55.0M
    /// buffers made with no permit, and the take-back had found 2.9 MB. The permits sat on pool threads' own stacks, which only their owner
    /// touches: a thread that took a burst of one class and moved to other work keeps up to 128 of it. A spent budget now asks each owner to
    /// hand its stacks back, and the owner drops all but a few at its next Get or Return.</summary>
    [TestFixture]
    [NonParallelizable]
    public class APermitOnAnotherThreadsOwnStackIsHandedBackWhenTheBudgetIsSpentTests
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

        /// <summary>An owner that parks <paramref name="buffers"/> - 1 buffers of <paramref name="bytes"/> on its own stack and holds one, returned
        /// at <see cref="Owner.Touch"/>: its next touch of the pool.</summary>
        private sealed class Owner : IDisposable
        {
            private readonly ManualResetEventSlim touch = new(), touched = new(), parked = new(), idle = new();
            private readonly Thread thread;

            public Owner(SectorAlignedBufferPool pool, int buffers, int bytes)
            {
                thread = new Thread(() =>
                {
                    var made = Enumerable.Range(0, buffers).Select(_ => pool.Get(bytes)).ToArray();
                    foreach (var page in made.Skip(1))
                        page.Return();
                    parked.Set();
                    touch.Wait();
                    made[0].Return();
                    touched.Set();
                    idle.Wait();
                }) { IsBackground = true };
                thread.Start();
                ClassicAssert.IsTrue(parked.Wait(TimeSpan.FromSeconds(30)), "CONTROL: the owner parked its buffers");
            }

            public void Touch()
            {
                touch.Set();
                ClassicAssert.IsTrue(touched.Wait(TimeSpan.FromSeconds(30)), "CONTROL: the owner touched the pool");
            }

            public void Dispose()
            {
                touch.Set();   // an owner a failed fact never touched still ends
                idle.Set();
                ClassicAssert.IsTrue(thread.Join(TimeSpan.FromSeconds(30)), "the owner ended");
            }
        }

        private static long UncachedOver(SectorAlignedBufferPool pool, int asks, int bytes) => OnItsOwnThread(() =>
        {
            var before = pool.Counts.MadeUncached;
            for (var i = 0; i < asks; i++)
                pool.Get(bytes).Return();
            return pool.Counts.MadeUncached - before;
        });

        [Test]
        public void Once_a_miss_is_served_an_owner_not_yet_reached_keeps_its_stack()
        {
            var budget = SectorAlignedBufferPool.ManagedBudgetBytes;
            SectorAlignedBufferPool.ManagedBudgetBytes = 64L << 10;   // a 16 KB small budget, held by two owners' 512 B buffers (1 KB each, aligned)
            try
            {
                var pool = new SectorAlignedBufferPool(1, 512);
                using (var first = new Owner(pool, 8, 512))
                using (var second = new Owner(pool, 7, 512))
                {
                    ClassicAssert.AreEqual(0, pool.Counts.MadeUncached, $"CONTROL: both owners' buffers took permits ({pool.Counts})");
                    ClassicAssert.AreEqual(1, UncachedOver(pool, 1, 2048), $"CONTROL: the budget is spent, and both owners are asked ({pool.Counts})");
                    first.Touch();
                    ClassicAssert.AreEqual(0, UncachedOver(pool, 1, 2048), $"CONTROL: the first owner's hand-back serves the next miss ({pool.Counts})");
                    var served = pool.Counts;
                    second.Touch();
                    var after = pool.Counts;
                    ClassicAssert.AreEqual(served.HandedBack, after.HandedBack,
                        $"the served miss withdrew the ask, so the second owner keeps its stack ({served} then {after}). MUTANT2 (the ask is left up): it drops its buffers though no one is starving");
                }
                pool.Free();
            }
            finally
            {
                SectorAlignedBufferPool.ManagedBudgetBytes = budget;
            }
        }

        [Test]
        public void A_permit_on_an_idle_threads_own_stack_is_handed_back_at_its_next_return()
        {
            var budget = SectorAlignedBufferPool.ManagedBudgetBytes;
            SectorAlignedBufferPool.ManagedBudgetBytes = 64L << 10;   // a 16 KB small budget: ten 1 KB buffers (1.5 KB each, aligned) hold it
            try
            {
                var pool = new SectorAlignedBufferPool(1, 512);
                using var touch = new ManualResetEventSlim();
                using var touched = new ManualResetEventSlim();
                using var parked = new ManualResetEventSlim();
                using var idle = new ManualResetEventSlim();
                var owner = new Thread(() =>
                {
                    var made = Enumerable.Range(0, 10).Select(_ => pool.Get(1024)).ToArray();
                    foreach (var page in made.Skip(1))
                        page.Return();   // the owner's own Return: onto its own stack, which no other thread may touch
                    parked.Set();
                    touch.Wait();
                    made[0].Return();   // the owner's next touch of the pool, as a pool thread's next IO is
                    touched.Set();
                    idle.Wait();
                }) { IsBackground = true };
                owner.Start();
                ClassicAssert.IsTrue(parked.Wait(TimeSpan.FromSeconds(30)), "CONTROL: the owner parked its buffers");
                var spent = pool.Counts;
                ClassicAssert.AreEqual(0, spent.MadeUncached, $"CONTROL: the owner's ten buffers each took a permit ({spent})");
                ClassicAssert.Greater(spent.ReservedSmall + 4608, spent.BudgetSmall, $"CONTROL: the owner holds the small budget ({spent})");
                ClassicAssert.GreaterOrEqual(spent.LocalBytes, 9 * 1536, $"the counts say the permits sit on an owner's own stack ({spent})");

                var first = OnItsOwnThread(() => { var before = pool.Counts.MadeUncached; pool.Get(4096).Return(); return pool.Counts.MadeUncached - before; });
                ClassicAssert.AreEqual(1, first, $"CONTROL: the take-back cannot reach another thread's own stack, so the first ask is made with no permit ({pool.Counts})");

                touch.Set();
                ClassicAssert.IsTrue(touched.Wait(TimeSpan.FromSeconds(30)), "CONTROL: the owner touched the pool");
                var handed = pool.Counts;
                var uncached = OnItsOwnThread(() =>
                {
                    var before = pool.Counts.MadeUncached;
                    for (var i = 0; i < Asks; i++)
                        pool.Get(4096).Return();
                    return pool.Counts.MadeUncached - before;
                });
                var after = pool.Counts;
                idle.Set();
                owner.Join();

                ClassicAssert.AreEqual(0, uncached, $"{Asks} asks after the owner's next touch are served with a permit ({handed} then {after}). "
                    + "MUTANT (no owner is asked): the owner keeps its stack, and each ask is made anew, the GC's on Return");
                ClassicAssert.GreaterOrEqual(handed.HandedBack, 5 * 1536, $"the owner dropped all but {4} of its stack, and the bytes are counted ({handed})");
                ClassicAssert.LessOrEqual(handed.LocalBytes, 5 * 1536, $"what the owner keeps is a few buffers ({handed})");
                ClassicAssert.LessOrEqual(after.ReservedSmall, after.BudgetSmall, "a permit is never past the budget");
                pool.Free();
            }
            finally
            {
                SectorAlignedBufferPool.ManagedBudgetBytes = budget;
            }
        }
    }
}
