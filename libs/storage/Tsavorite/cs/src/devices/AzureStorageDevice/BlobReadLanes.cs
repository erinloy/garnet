// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

namespace Tsavorite.devices
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// The process's turns at reading page blobs, in two lanes. A read issued while <see cref="OnWalkLane"/> answers true (a walk:
    /// a scan, a warm, a boot pass) first takes a turn on the walk lane, which holds at most half the read permits, and only then
    /// a turn on the shared lane every read takes. So walks never hold more than half the shared turns, and a request's read never
    /// queues behind more than that many walk reads. A request is never capped by the walk lane.
    /// The order is fixed: walk lane, then shared lane. A turn is never held while waiting on a lane earlier in that order.
    /// </summary>
    public static class BlobReadLanes
    {
        static SemaphoreSlim shared = new(DefaultReadPermits), walk = new(WalkPermitsOf(DefaultReadPermits));
        static long walkReads, walkReadsWaited, requestReads;

        /// <summary>The read permits a process starts with (the vendored device's own bound).</summary>
        public static int DefaultReadPermits => Math.Min(100, Environment.ProcessorCount * 10);

        /// <summary>Whether the read being issued here belongs to a walk. The host sets it once (it reads its own ambient scope);
        /// it is evaluated where the read is issued, so it sees the issuer's context.</summary>
        public static Func<bool> OnWalkLane { get; set; } = static () => false;

        /// <summary>The shared lane: every blob read takes a turn here, the device's other reads (listing, properties) included.</summary>
        public static SemaphoreSlim Shared => Volatile.Read(ref shared);

        /// <summary>Walk reads issued, those that found the walk lane full and waited for it, and request reads issued.</summary>
        public static (long WalkReads, long WalkReadsWaited, long RequestReads) Counts =>
            (Interlocked.Read(ref walkReads), Interlocked.Read(ref walkReadsWaited), Interlocked.Read(ref requestReads));

        /// <summary>The walk lane's permits for <paramref name="readPermits"/> shared ones: half, at least one.</summary>
        public static int WalkPermitsOf(int readPermits) => Math.Max(1, readPermits / 2);

        /// <summary>Resizes both lanes. Only while no read is in flight (a host's start, a test's setup): a turn taken before it
        /// releases the lane it was taken on.</summary>
        public static void SetReadPermits(int readPermits)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(readPermits, 1);
            Volatile.Write(ref walk, new SemaphoreSlim(WalkPermitsOf(readPermits)));
            Volatile.Write(ref shared, new SemaphoreSlim(readPermits));
        }

        /// <summary>Takes this read's turn: the walk lane first when the issuer walks, then the shared lane.</summary>
        public static async ValueTask<Turn> TakeAsync(CancellationToken ct)
        {
            SemaphoreSlim walkLane = null;
            if (OnWalkLane())
            {
                walkLane = Volatile.Read(ref walk);
                Interlocked.Increment(ref walkReads);
                if (!walkLane.Wait(0))
                {
                    Interlocked.Increment(ref walkReadsWaited);
                    await walkLane.WaitAsync(ct).ConfigureAwait(false);
                }
            }
            else Interlocked.Increment(ref requestReads);
            var sharedLane = Shared;
            try { await sharedLane.WaitAsync(ct).ConfigureAwait(false); }
            catch { walkLane?.Release(); throw; }
            return new Turn(sharedLane, walkLane);
        }

        /// <summary>One read's turn; disposing releases the shared lane, then the walk lane.</summary>
        public readonly struct Turn : IDisposable
        {
            readonly SemaphoreSlim sharedLane, walkLane;

            internal Turn(SemaphoreSlim sharedLane, SemaphoreSlim walkLane) { this.sharedLane = sharedLane; this.walkLane = walkLane; }

            /// <summary>Whether the read took the walk lane.</summary>
            public bool Walked => walkLane is not null;

            /// <inheritdoc/>
            public void Dispose()
            {
                sharedLane?.Release();
                walkLane?.Release();
            }
        }
    }
}
