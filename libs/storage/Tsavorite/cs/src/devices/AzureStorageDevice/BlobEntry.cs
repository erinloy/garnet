// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

namespace Tsavorite.devices
{
    using System;
    using System.Collections.Concurrent;
    using System.Threading;
    using System.Threading.Tasks;

    // This class bundles a page blob object with a queue and a counter to ensure 
    // 1) BeginCreate is not called more than once
    // 2) No writes are issued before EndCreate
    // The creator of a BlobEntry is responsible for populating the object with an underlying Page Blob. Any subsequent callers
    // either directly write to the created page blob, or queues the write so the creator can clear it after creation is complete.
    // In-progress creation is denoted by a null value on the underlying page blob
    class BlobEntry
    {
        public BlobUtilsV12.PageBlobClients PageBlob { get; private set; }
        public Azure.ETag ETag { get; set; }

        ConcurrentQueue<Action> pendingWrites;
        readonly AzureStorageDevice azureStorageDevice;
        int waitingCount;



        /// <summary>
        /// Creates a new BlobEntry to represent a page blob that already exists in storage.
        /// </summary>
        /// <param name="pageBlob"></param>
        /// <param name="eTag"></param>
        /// <param name="azureStorageDevice"></param>
        public BlobEntry(BlobUtilsV12.PageBlobClients pageBlob, Azure.ETag eTag, AzureStorageDevice azureStorageDevice)
        {
            PageBlob = pageBlob;
            this.azureStorageDevice = azureStorageDevice;
            ETag = eTag;
        }

        /// <summary>
        /// Creates a new BlobEntry to represent a page blob that will be created by <see cref="CreateAsync(long, BlobUtilsV12.PageBlobClients, bool, bool)"/>.
        /// </summary>
        public BlobEntry(AzureStorageDevice azureStorageDevice)
        {
            this.azureStorageDevice = azureStorageDevice;
            pendingWrites = new ConcurrentQueue<Action>();
            waitingCount = 0;
        }

        /// <summary>A find refused before any call: queued reads fail with <paramref name="why"/>.</summary>
        public void Fail(Exception why)
        {
            CreationException = why;
            int waitingCountSnapshot = Interlocked.Exchange(ref waitingCount, -1);
            for (int i = 0; i < waitingCountSnapshot; i++)
            {
                Action action;
                while (!pendingWrites.TryDequeue(out action)) { }
                try { action(); } catch { /* ignore exceptions from queued actions */ }
            }
            pendingWrites = null;
        }

        /// <summary>
        /// Exception from a failed creation, if any.
        /// </summary>
        public Exception CreationException { get; private set; }

        /// <summary>
        /// Asynchronously finds the given pageBlob as it stands, for a read: it fails by name when no blob exists, and never creates one (Ziltch prod 11-01:
        /// a read of segment 0 created hlog.0, a 1 GB blob of zeros, stamped at the read). Reads queued meanwhile run once it is found, or fail with it.
        /// </summary>
        public Task OpenAsync(BlobUtilsV12.PageBlobClients pageBlob) => CreateAsync(0, pageBlob, replace: false, openOnly: true);

        /// <summary>
        /// Asynchronously invoke create on the given pageBlob.
        /// </summary>
        /// <param name="size">maximum size of the blob</param>
        /// <param name="pageBlob">The page blob to create</param>
        public async Task CreateAsync(long size, BlobUtilsV12.PageBlobClients pageBlob, bool replace = false, bool openOnly = false)
        {
            try
            {
                await azureStorageDevice.BlobManager.PerformWithRetriesAsync(
                    azureStorageDevice.BlobManager.AsynchronousStorageReadMaxConcurrency,
                    true,
                    openOnly ? "PageBlobClient.GetPropertiesAsync" : "PageBlobClient.CreateAsync",
                    openOnly ? "OpenDeviceSegment" : "CreateDevice",
                    "",
                    pageBlob.Default.Name,
                    3000,
                    !openOnly,   // an absent segment fails its reads, never the device: a critical error terminates every later call
                    async (numAttempts) =>
                    {
                        var client = (numAttempts > 1) ? pageBlob.Default : pageBlob.Aggressive;
                        if (openOnly)
                        {
                            try
                            {
                                ETag = (await client.GetPropertiesAsync(cancellationToken: azureStorageDevice.StorageErrorHandler.Token).ConfigureAwait(false)).Value.ETag;
                            }
                            catch (Azure.RequestFailedException e) when (BlobUtilsV12.BlobDoesNotExist(e))
                            {
                                throw new InvalidOperationException($"segment blob '{pageBlob.Default.Name}' does not exist, and a read never creates one", e);
                            }
                            return 1;
                        }

                        var response = await client.CreateAsync(
                        size: size,
                        conditions: replace ? null : new Azure.Storage.Blobs.Models.PageBlobRequestConditions() { IfNoneMatch = Azure.ETag.All },
                        cancellationToken: azureStorageDevice.StorageErrorHandler.Token).ConfigureAwait(false);

                        ETag = response.Value.ETag;
                        return 1;
                    },
                    async () =>
                    {
                        var response = await pageBlob.Default.GetPropertiesAsync().ConfigureAwait(false);
                        ETag = response.Value.ETag;
                    }).ConfigureAwait(false);

                // At this point the blob is fully created. After this line all consequent writers will write immediately. We just
                // need to clear the queue of pending writers.
                PageBlob = pageBlob;

                // Take a snapshot of the current waiting count. Exactly this many actions will be cleared.
                // Swapping in -1 will inform any stragglers that we are not taking their actions and prompt them to retry (and call write directly)
                int waitingCountSnapshot = Interlocked.Exchange(ref waitingCount, -1);
                Action action;

                // Clear actions
                for (int i = 0; i < waitingCountSnapshot; i++)
                {
                    // inserts into the queue may lag behind the creation thread. We have to wait until that happens.
                    // This is so rare, that we are probably okay with a busy wait.
                    while (!pendingWrites.TryDequeue(out action)) { }
                    action();
                }

                // Mark for deallocation for the GC
                pendingWrites = null;
            }
            catch (Exception ex)
            {
                // On creation failure, capture the exception, drain the queue with that exception,
                // then mark as complete and clear pending writes.
                CreationException = ex;
                int waitingCountSnapshot = Interlocked.Exchange(ref waitingCount, -1);
                Action action;
                for (int i = 0; i < waitingCountSnapshot; i++)
                {
                    while (!pendingWrites.TryDequeue(out action)) { }
                    try { action(); } catch { /* ignore exceptions from queued actions */ }
                }
                pendingWrites = null;
                throw;
            }
        }

        /// <summary>
        /// Attempts to enqueue an action to be invoked by the creator after creation is done. Should only be invoked when
        /// creation is in-flight. This call is allowed to fail (and return false) if concurrently the creation is complete.
        /// The caller should call the write action directly instead of queueing in this case.
        /// </summary>
        /// <param name="writeAction">The write action to perform</param>
        /// <returns>Whether the action was successfully enqueued</returns>
        public bool TryQueueAction(Action writeAction)
        {
            int currentCount;
            do
            {
                currentCount = waitingCount;

                // If current count became -1, creation is complete. New queue entries will not be processed and we must call the action ourselves.
                if (currentCount == -1) return false;

            } while (Interlocked.CompareExchange(ref waitingCount, currentCount + 1, currentCount) != currentCount);

            // Enqueue last. The creation thread is obliged to wait until it has processed waitingCount many actions.
            // It is extremely unlikely that we will get scheduled out here anyways.
            pendingWrites.Enqueue(writeAction);
            return true;
        }
    }
}