// ZILTCH 2026-10-10. Licensed under the MIT license.

namespace Tsavorite.devices
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// A store's stop, told to its blob devices BEFORE its final checkpoint. A blob write that fails transiently sleeps 1, 2, 4 .. 256 s
    /// between attempts, and the sleep did not observe the stop: a graceful stop with a blob that was failing waited in those sleeps
    /// (each is a lease user, so the device's Dispose waited for the lease duration), then returned with the writes still running.
    /// With a stop asked for a blob directory, a write of that directory (1) wakes at once and retries without sleeping, while the stop's bound
    /// and its attempts last; (2) is then CANCELLED, counted by kind and named, not abandoned: the page may not be in blob, so the caller
    /// must not call its checkpoint durable.
    /// </summary>
    public static class StoreStop
    {
        static readonly object gate = new();
        static readonly List<StoreStopAsk> asks = new();
        static readonly List<Sleeper> sleepers = new();
        static readonly Dictionary<string, long> cancelledByKind = new() { ["log"] = 0, ["index"] = 0, ["checkpoint"] = 0 };

        /// <summary>The stop of the store whose blobs live under <paramref name="blobDirectory"/> (the directory inside the container, no trailing slash).</summary>
        public static StoreStopAsk Ask(string blobDirectory, TimeSpan bound)
        {
            ArgumentException.ThrowIfNullOrEmpty(blobDirectory);
            var ask = new StoreStopAsk(blobDirectory.TrimEnd('/') + "/", bound, DateTime.UtcNow);
            Sleeper[] wake;
            lock (gate)
            {
                asks.Add(ask);
                wake = Matching(ask.Prefix);
            }
            foreach (var s in wake) s.Wake.Cancel();
            return ask;
        }

        /// <summary>Cancelled writes by kind (log, index, checkpoint), process-wide and monotone; every kind is present from the start.</summary>
        public static IReadOnlyDictionary<string, long> CancelledByKind()
        {
            lock (gate) return new Dictionary<string, long>(cancelledByKind);
        }

        public static bool IsAsked(string target)
        {
            lock (gate) return Find(target) is not null;
        }

        /// <summary>The pause between two attempts of a write while its store's stop is asked (the first attempt after the stop wakes a sleeping write follows at once).</summary>
        public static readonly TimeSpan Pace = TimeSpan.FromMilliseconds(200);

        /// <summary>True while a stop is asked for the target's directory and its bound has not passed: the write is retried, paced, however many attempts it has had.</summary>
        public static bool MayRetryNow(string target)
        {
            lock (gate) return Find(target) is { } ask && DateTime.UtcNow < ask.AskedUtc + ask.Bound;
        }

        /// <summary>The backoff between two attempts: its length, cut short when the stop is asked, and false when the stop closed over it (give up).</summary>
        public static async Task<bool> BackoffAsync(string target, TimeSpan delay, CancellationToken operationToken, TimeProvider clock = null)
        {
            var sleeper = new Sleeper(target);
            lock (gate)
            {
                if (Find(target) is not null) return true;
                sleepers.Add(sleeper);
            }
            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(operationToken, sleeper.Wake.Token);
                await Task.Delay(delay, clock ?? TimeProvider.System, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (sleeper.Wake.IsCancellationRequested && !operationToken.IsCancellationRequested)
            {
                // the stop was asked (or closed) while this write slept: retry now
            }
            finally
            {
                lock (gate) sleepers.Remove(sleeper);
                sleeper.Wake.Dispose();
            }
            return !sleeper.Closed;
        }

        /// <summary>Counts one write the stop cancelled and answers the exception that ends it.</summary>
        public static StopCancelledException Cancel(string target, string name, int numAttempts, Exception cause)
        {
            var kind = KindOf(target);
            lock (gate)
            {
                cancelledByKind[kind] = cancelledByKind.GetValueOrDefault(kind) + 1;
                if (Find(target) is { } ask) ask.Count();
            }
            return new StopCancelledException($"storage operation {name} on {target} ({kind}) was cancelled by the store's stop after attempt {numAttempts}: the write is NOT durable", cause);
        }

        internal static void Close(StoreStopAsk ask)
        {
            Sleeper[] wake;
            lock (gate)
            {
                asks.Remove(ask);
                wake = Matching(ask.Prefix);
                foreach (var s in wake) s.Closed = true;
            }
            foreach (var s in wake) s.Wake.Cancel();
        }

        static StoreStopAsk Find(string target)
        {
            foreach (var ask in asks)
                if (target is not null && target.Contains(ask.Prefix, StringComparison.Ordinal)) return ask;
            return null;
        }

        static Sleeper[] Matching(string prefix)
        {
            var found = new List<Sleeper>();
            foreach (var s in sleepers)
                if (s.Target is not null && s.Target.Contains(prefix, StringComparison.Ordinal)) found.Add(s);
            return found.ToArray();
        }

        /// <summary>log (the hot log's pages), index (hash table checkpoint files), or checkpoint (everything else the checkpoint writes).</summary>
        static string KindOf(string target)
        {
            var name = target ?? "";
            name = name[(name.LastIndexOf('/') + 1)..];
            if (name.StartsWith("hlog", StringComparison.Ordinal) || name.StartsWith("events", StringComparison.Ordinal)) return "log";
            if (name.StartsWith("ht.dat", StringComparison.Ordinal) || name.StartsWith("ofb.dat", StringComparison.Ordinal)) return "index";
            return "checkpoint";
        }

        sealed class Sleeper(string target)
        {
            public string Target { get; } = target;
            public CancellationTokenSource Wake { get; } = new();
            public volatile bool Closed;
        }
    }

    /// <summary>One store's asked stop; dispose it when the stop's writes are done (it wakes and cancels any write still sleeping).</summary>
    public sealed class StoreStopAsk : IDisposable
    {
        long cancelled;
        int closed;

        internal StoreStopAsk(string prefix, TimeSpan bound, DateTime askedUtc)
        {
            Prefix = prefix;
            Bound = bound;
            AskedUtc = askedUtc;
        }

        internal string Prefix { get; }

        /// <summary>How long after the ask a failing write keeps being retried at once.</summary>
        public TimeSpan Bound { get; }

        /// <summary>When the stop was asked.</summary>
        public DateTime AskedUtc { get; }

        /// <summary>Writes of this store's directory this stop cancelled so far.</summary>
        public long Cancelled => Interlocked.Read(ref cancelled);

        internal void Count() => Interlocked.Increment(ref cancelled);

        /// <inheritdoc />
        public void Dispose()
        {
            if (Interlocked.Exchange(ref closed, 1) == 0) StoreStop.Close(this);
        }
    }

    /// <summary>A blob write the store's stop cancelled after its retries: not durable.</summary>
    public sealed class StopCancelledException : OperationCanceledException
    {
        /// <summary>Creates the exception with its message and the failure the last attempt ended with.</summary>
        public StopCancelledException(string message, Exception inner) : base(message, inner) { }
    }
}
