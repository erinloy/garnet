// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System.IO;

namespace Tsavorite.devices
{
    /// <summary>
    /// ZILTCH (echo #90130 (1)): a page write the device's <see cref="AzureStorageDevice.WriteFence"/> refused, because the writer
    /// may no longer own the blob (its lease is lost, or went its duration without a renewal). Prod hlog.128, 11-16 03:19Z: a pod
    /// stalled 183 s, another writer took the lapsed lease, and the stalled pod then flushed a page over the new writer's log.
    /// Not transient: the retry loop rethrows it without a retry and without terminating the device, so a lease taken back
    /// (no other node took it) writes again.
    /// </summary>
    public sealed class DeviceWriteFencedException : IOException
    {
        /// <summary>Creates the refusal of one page write.</summary>
        public DeviceWriteFencedException(string blobName, long destinationAddress, string why, bool sent = false)
            : base($"page write to '{blobName}' at {destinationAddress} REFUSED by the writer fence: {why}. " + (sent ? "The blob landed none of it." : "No byte of it was sent."))
        {
            BlobName = blobName;
            DestinationAddress = destinationAddress;
            Why = why;
        }

        /// <summary>The page blob the write was for.</summary>
        public string BlobName { get; }

        /// <summary>The byte offset in the blob the refused portion would have written.</summary>
        public long DestinationAddress { get; }

        /// <summary>The fence's reason.</summary>
        public string Why { get; }
    }
}
