// Copyright (c) Microsoft Corporation. Licensed under the MIT license.

using System.Threading;

namespace Tsavorite.core
{
    /// <summary>
    /// Reads that answered NotFound only because the key's record chain went below <c>BeginAddress</c>: the key HAD a record, and it was
    /// truncated or moved out of this log. In a one-log store that is gone history; in a two-tier store (F2) it is exactly the read that
    /// must ask the cold tier, so the count is the false-absence rate a missing cold leg would cause. Engine-level static, like
    /// <see cref="UndeliveredPendingReads"/>: the host publishes it as a gauge.
    /// </summary>
    public static class ReadsBelowBegin
    {
        private static long count;

        /// <summary>Records one read whose chain ended below BeginAddress.</summary>
        public static void Record() => _ = Interlocked.Increment(ref count);

        /// <summary>How many reads in this process ended below BeginAddress.</summary>
        public static long Count => Interlocked.Read(ref count);

        /// <summary>For tests only.</summary>
        public static void ResetForTests() => _ = Interlocked.Exchange(ref count, 0);
    }
}
