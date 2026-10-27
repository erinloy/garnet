// Copyright (c) Microsoft Corporation. Licensed under the MIT license.

using System.Threading;

namespace Tsavorite.core
{
    /// <summary>
    /// Pending operations whose drain, by a session still in v's PREPARE, met a key another session had written at v+1, so the session moved
    /// to v+1 and the operation continued. Before, the drain completed them as Error with no output. Engine-level static, like
    /// <see cref="ReadsBelowBegin"/>: the host publishes it as a gauge.
    /// </summary>
    public static class PendingCprShiftRetries
    {
        private static long count;

        /// <summary>Records one retry.</summary>
        public static void Record() => _ = Interlocked.Increment(ref count);

        /// <summary>How many pending operations in this process were retried after a version shift.</summary>
        public static long Count => Interlocked.Read(ref count);
    }
}
