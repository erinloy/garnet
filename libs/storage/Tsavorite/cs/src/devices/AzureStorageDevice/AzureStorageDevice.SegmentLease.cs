// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

namespace Tsavorite.devices
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Azure;
    using Azure.Storage.Blobs.Models;
    using Azure.Storage.Blobs.Specialized;

    /// <summary>
    /// ZILTCH (echo #94774 (2)), prod hlog.128 11-16 03:21Z: a deposed writer's page PUT was already on the wire when its lease
    /// lapsed; the client cancelled it, Azure applied it anyway, after the new writer had flushed that page, and an index entry
    /// came to name bytes 80 into another record. <see cref="WriteFence"/> asks before a PUT is sent and cannot refuse one in
    /// flight. Here the blob refuses it: the writer holds a blob lease on each segment it writes, every page PUT carries its id,
    /// and the next writer breaks that lease before its first write, so a late PUT of the one before answers 412 and lands nothing.
    /// </summary>
    public partial class AzureStorageDevice
    {
        /// <summary>
        /// The writer's segment lease id (a GUID), asked at each page PUT, or null: unleased, as before. A writer that took the
        /// cell from another answers an id of its own, and its first write to a segment breaks the other's lease.
        /// </summary>
        public Func<string> SegmentLeaseId { get; set; }

        long segmentLeasesBroken, segmentPutsRefused;

        /// <summary>Segment leases this device broke, held by another writer's id (each a deposed writer fenced).</summary>
        public long SegmentLeasesBroken => Interlocked.Read(ref segmentLeasesBroken);

        /// <summary>Page PUTs the blob refused because this device's segment lease was no longer the blob's.</summary>
        public long SegmentPutsRefused => Interlocked.Read(ref segmentPutsRefused);

        /// <summary>Holds <paramref name="leaseId"/> on the entry's blob before a PUT. Taken once per blob and id. A lease held by
        /// another id is broken only while <see cref="WriteFence"/> lets this writer write, so a deposed writer never takes
        /// a segment back: its in-flight PUT gets the blob's 412, and its next write is refused by the fence first.</summary>
        async Task HoldSegmentLeaseAsync(BlobEntry entry, string leaseId, long destinationAddress, CancellationToken token)
        {
            if (entry.HeldLease == leaseId) return;
            await entry.LeaseGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (entry.HeldLease == leaseId) return;
                var blob = entry.PageBlob.Default;
                var lease = blob.GetBlobLeaseClient(leaseId);
                try
                {
                    await lease.AcquireAsync(BlobLeaseClient.InfiniteLeaseDuration, cancellationToken: token).ConfigureAwait(false);
                }
                catch (RequestFailedException e) when (e.Status == 409)
                {
                    if (WriteFence?.Invoke() is { } fenced)
                        throw new DeviceWriteFencedException(blob.Name, destinationAddress, fenced);
                    await BreakSegmentLeaseAsync(blob, token).ConfigureAwait(false);
                    Interlocked.Increment(ref segmentLeasesBroken);
                    await lease.AcquireAsync(BlobLeaseClient.InfiniteLeaseDuration, cancellationToken: token).ConfigureAwait(false);
                }
                entry.HeldLease = leaseId;
            }
            finally
            {
                entry.LeaseGate.Release();
            }
        }

        static async Task BreakSegmentLeaseAsync(PageBlobClient blob, CancellationToken token)
        {
            try
            {
                await blob.GetBlobLeaseClient().BreakAsync(TimeSpan.Zero, cancellationToken: token).ConfigureAwait(false);
            }
            catch (RequestFailedException e) when (e.Status == 409 && e.ErrorCode == "LeaseNotPresentWithLeaseOperation" || e.Status == 404)
            {
                // No lease to break, or no blob.
            }
        }

        /// <summary>A segment blob's delete. Refused on its lease (the next writer took it): fenced, not a critical storage error.</summary>
        async Task DeleteSegmentBlobAsync(PageBlobClient client, BlobEntry entry, CancellationToken token)
        {
            var conditions = await RemovalConditionsAsync(entry, token).ConfigureAwait(false);
            try
            {
                await client.DeleteAsync(conditions: conditions, cancellationToken: token).ConfigureAwait(false);
            }
            catch (RequestFailedException e) when (conditions?.LeaseId != null && IsLeaseRefusal(e))
            {
                throw SegmentLeaseRefused(entry, client.Name, 0, e);
            }
        }

        /// <summary>A segment delete's conditions: this writer's lease when it holds the blob (the blob refuses the delete if
        /// that lease was broken), else the blob is freed of a deposed writer's lease first, only while the fence lets this writer write.</summary>
        async Task<BlobRequestConditions> RemovalConditionsAsync(BlobEntry entry, CancellationToken token)
        {
            if (SegmentLeaseId?.Invoke() is not { } id || entry.PageBlob.Default is null) return null;
            if (entry.HeldLease == id) return new BlobRequestConditions { LeaseId = id };
            if (WriteFence?.Invoke() is { } fenced) throw new DeviceWriteFencedException(entry.PageBlob.Default.Name, 0, fenced);
            await BreakSegmentLeaseAsync(entry.PageBlob.Default, token).ConfigureAwait(false);
            return null;
        }

        /// <summary>A segment's create. A replace (a stray passed over at recovery) may meet a deposed writer's lease: broken first,
        /// only while the fence lets this writer write; a refusal fails the entry's queued writes with it.</summary>
        async Task CreateSegmentAsync(BlobEntry entry, long size, BlobUtilsV12.PageBlobClients pageBlob, bool replace)
        {
            if (replace && SegmentLeaseId?.Invoke() is not null)
            {
                try
                {
                    if (WriteFence?.Invoke() is { } fenced) throw new DeviceWriteFencedException(pageBlob.Default.Name, 0, fenced);
                    await BreakSegmentLeaseAsync(pageBlob.Default, StorageErrorHandler.Token).ConfigureAwait(false);
                }
                catch (Exception e) when (e is DeviceWriteFencedException or RequestFailedException)
                {
                    entry.Fail(e);
                    throw;
                }
            }
            await entry.CreateAsync(size, pageBlob, replace).ConfigureAwait(false);
        }

        /// <summary>The blob refused a PUT on its lease: this writer's lease is gone (broken by the next writer). Fenced, not retried.</summary>
        DeviceWriteFencedException SegmentLeaseRefused(BlobEntry entry, string blobName, long destinationAddress, RequestFailedException e)
        {
            // Asked again on the next write: a writer that takes the cell back holds the segment again, past the fence.
            entry.HeldLease = null;
            Interlocked.Increment(ref segmentPutsRefused);
            return new DeviceWriteFencedException(blobName, destinationAddress,
                $"the blob refused it on its segment lease ({e.Status} {e.ErrorCode}): another writer holds the segment now", sent: true);
        }

        static bool IsLeaseRefusal(RequestFailedException e)
            => e.Status == 412 && e.ErrorCode is "LeaseIdMismatchWithBlobOperation" or "LeaseNotPresentWithBlobOperation" or "LeaseLost" or "LeaseIdMissing";
    }
}
