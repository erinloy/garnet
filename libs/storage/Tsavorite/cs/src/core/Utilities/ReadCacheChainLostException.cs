// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

namespace Tsavorite.core
{
    /// <summary>
    /// A read-cache chain links a record whose page has already closed: the eviction that should have unlinked it missed it. Waiting cannot
    /// mend the link, so the walk that met it throws this instead of retrying forever. The key behind the link is unreachable through the
    /// index until the store restarts and recovers its index from checkpoint plus replay.
    /// </summary>
    public sealed class ReadCacheChainLostException : TsavoriteException
    {
        /// <summary>A read, upsert, RMW, delete or scan walking one key's chain.</summary>
        public const string ReadWalker = "read";

        /// <summary>The index checkpoint walking a copied bucket past its read-cache records.</summary>
        public const string CheckpointWalker = "checkpoint";

        /// <summary>Creates the exception for a link at <paramref name="address"/> met below <paramref name="closedUntilAddress"/>.</summary>
        public ReadCacheChainLostException(long address, long closedUntilAddress, string walker, long missedEvictions)
            : base($"read-cache chain lost: the {walker} walk met read-cache address {address} below ClosedUntilAddress {closedUntilAddress} "
                + $"after its close completed (missed eviction {missedEvictions} this life)")
        {
            Address = address;
            ClosedUntilAddress = closedUntilAddress;
            Walker = walker;
            MissedEvictions = missedEvictions;
        }

        /// <summary>The dangling read-cache address (absolute).</summary>
        public long Address { get; }

        /// <summary>The read cache's ClosedUntilAddress when the walk threw.</summary>
        public long ClosedUntilAddress { get; }

        /// <summary><see cref="ReadWalker"/> or <see cref="CheckpointWalker"/>.</summary>
        public string Walker { get; }

        /// <summary>The store's missed-eviction count including this one.</summary>
        public long MissedEvictions { get; }
    }
}
