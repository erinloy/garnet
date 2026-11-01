// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

#pragma warning disable 0162

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Tsavorite.core
{
    /// <summary>
    /// Memory allocator for objects
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public sealed class MallocFixedPageSize<T> : IDisposable
    {
        // We will never get an index of 0 from (Bulk)Allocate
        private const long InvalidAllocationIndex = 0;

        private const int PageSizeBits = 16;
        private const int PageSize = 1 << PageSizeBits;
        private const int PageSizeMask = PageSize - 1;
        private const int LevelSizeBits = 12;
        private const int LevelSize = 1 << LevelSizeBits;

        private readonly T[][] values = new T[LevelSize][];
        private readonly IntPtr[] pointers = new IntPtr[LevelSize];

        private volatile int writeCacheLevel;

        private volatile int count;

        internal static int RecordSize => Unsafe.SizeOf<T>();
        internal static bool IsBlittable => Utility.IsBlittable<T>();

        private int checkpointCallbackCount;
        private int checkpointErrorCode;
        private TaskCompletionSource<bool> checkpointTcs;

        private readonly ConcurrentQueue<long> freeList;

        readonly ILogger logger;

        // Direct-VM pages (a store whose settings say NativeHashIndex): each page is its own mapping, made when an allocation first
        // needs it (an empty allocator maps nothing) and unmapped when a trim drops it or the allocator is disposed, or, while an
        // overflow checkpoint write may read it, when that write drains. Managed pages are pinned arrays the pinned heap keeps.
        private readonly bool native;
        private readonly DirectVmBlock[] blocks;
        private static readonly T[] NativePage = [];
        private int disposed, pagesFreed;

        /// <summary>Direct-VM overflow pages unmapped (a trim, a dispose, or a parked dispose's drain), and disposes parked under an
        /// overflow checkpoint write: process-wide counts for tests.</summary>
        internal static long NativePagesFreed, NativeDisposesParked;

        // what a checkpoint writes for a level no allocation mapped (only the first, holding the reserved first chunk): zeros
        private const int EmptyLevelBytes = 1 << 16;
        private static nint emptyLevel;

#if DEBUG
        private enum AllocationMode { None, Single, Bulk };
        private AllocationMode allocationMode;
#endif

        // This is used only for Flush and Restore, not random reads, so 512 is fine.
        const int SectorSize = 512;

        private int initialAllocation = 0;

        /// <summary>Number of allocations performed</summary>
        public int NumAllocations => count - initialAllocation; // Ignores the initial allocation

        /// <summary>
        /// Create new instance
        /// </summary>
        public unsafe MallocFixedPageSize(ILogger logger = null, bool native = false)
        {
            this.logger = logger;
            freeList = new ConcurrentQueue<long>();
            this.native = native && IsBlittable;
            if (this.native)
                blocks = new DirectVmBlock[LevelSize];
            else
                NewPage(0);

            writeCacheLevel = -1;
            Interlocked.MemoryBarrier();

            // Allocate one block so we never return a null pointer; this allocation is never freed.
            // Use BulkAllocate so the caller can still do either BulkAllocate or single Allocate().
            BulkAllocate();
            initialAllocation = AllocateChunkSize;
#if DEBUG
            // Clear this for the next allocation.
            allocationMode = AllocationMode.None;
#endif
        }

        /// <summary>
        /// Get physical address -- for blittable objects only
        /// </summary>
        /// <param name="logicalAddress">The logicalAddress of the allocation. For BulkAllocate, this may be an address within the chunk size, to reference that particular record.</param>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public long GetPhysicalAddress(long logicalAddress)
        {
            Debug.Assert(IsBlittable, "GetPhysicalAddress requires the values to be blittable");
            return (long)pointers[logicalAddress >> PageSizeBits] + (logicalAddress & PageSizeMask) * RecordSize;
        }

        /// <summary>
        /// Get object
        /// </summary>
        /// <param name="index">The index of the allocation. For BulkAllocate, this may be a value within the chunk size, to reference that particular record.</param>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe ref T Get(long index)
        {
            Debug.Assert(index != InvalidAllocationIndex, "Invalid allocation index");
            if (IsBlittable)
                return ref Unsafe.AsRef<T>((byte*)(pointers[index >> PageSizeBits]) + (index & PageSizeMask) * RecordSize);
            else
                return ref values[index >> PageSizeBits][index & PageSizeMask];
        }

        /// <summary>
        /// Set object
        /// </summary>
        /// <param name="index"></param>
        /// <param name="value"></param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe void Set(long index, ref T value)
        {
            Debug.Assert(index != InvalidAllocationIndex, "Invalid allocation index");
            if (IsBlittable)
                Unsafe.AsRef<T>((byte*)(pointers[index >> PageSizeBits]) + (index & PageSizeMask) * RecordSize) = value;
            else
                values[index >> PageSizeBits][index & PageSizeMask] = value;
        }

        /// <summary>
        /// Free object
        /// </summary>
        /// <param name="pointer"></param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Free(long pointer)
        {
            freeList.Enqueue(pointer);
        }

        internal int FreeListCount => freeList.Count;   // For test

        /// <summary>The first allocation a caller gets: the ones before it are the never-freed first block.</summary>
        internal long FirstAllocation => initialAllocation;

        /// <summary>Drains the free list, returning the free allocations below <paramref name="line"/>; one at or past it is dropped, for
        /// a trim to give back. Only at a full barrier: no allocation may run beside it.</summary>
        internal List<long> TakeFree(long line)
        {
            var below = new List<long>();
            while (freeList.TryDequeue(out var a))
                if (a < line) below.Add(a);
            return below;
        }

        /// <summary>Whether <see cref="TrimTo"/> may give back what lies past <paramref name="line"/>: past the first block, and no
        /// checkpoint is writing the pages.</summary>
        internal bool CanTrimTo(long line) => line >= initialAllocation && line <= count && checkpointCallbackCount == 0;

        /// <summary>Makes <paramref name="line"/> the next allocation and gives back every page past the ones it needs: the line's own,
        /// and the next when the line is inside a page (an allocation makes the next page only as it enters a page, so that one must
        /// stand), zeroing what it keeps past the line. Only at a full barrier, with nothing at or past the line reachable and the free list
        /// holding nothing past it.</summary>
        internal unsafe void TrimTo(long line)
        {
            Debug.Assert(CanTrimTo(line));
            var keep = (int)(line >> PageSizeBits) + ((line & PageSizeMask) == 0 ? 0 : 1);
            // what stays allocated past the line is handed out again as new: a stale link left in it would lead a chain past the line
            for (long a = line; a < count && (a >> PageSizeBits) <= keep;)
            {
                var page = (int)(a >> PageSizeBits);
                var end = Math.Min(count, ((long)page + 1) << PageSizeBits);
                if (IsBlittable)
                    new Span<byte>((void*)((long)pointers[page] + (a & PageSizeMask) * RecordSize), (int)((end - a) * RecordSize)).Clear();
                else
                    Array.Clear(values[page], (int)(a & PageSizeMask), (int)(end - a));
                a = end;
            }
            for (var i = Math.Max(keep + 1, 2); i < LevelSize && values[i] is not null; i++)
            {
                if (native)
                {
                    Unmap(i);
                    continue;
                }
                values[i] = null;
                pointers[i] = IntPtr.Zero;
            }
            writeCacheLevel = -1;
            count = (int)line;
            Interlocked.MemoryBarrier();
        }

        internal const int AllocateChunkSize = 16;     // internal for test

        /// <summary>
        /// Allocate a block of size RecordSize * kAllocateChunkSize. 
        /// </summary>
        /// <remarks>Warning: cannot mix 'n' match use of Allocate and BulkAllocate because there is no header indicating record size, so 
        /// the freeList does not distinguish them.</remarks>
        /// <returns>The logicalAddress (index) of the block</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe long BulkAllocate()
        {
#if DEBUG
            Debug.Assert(allocationMode != AllocationMode.Single, "Cannot mix Single and Bulk allocation modes");
            allocationMode = AllocationMode.Bulk;
#endif
            return InternalAllocate(AllocateChunkSize);
        }

        /// <summary>
        /// Allocate a block of size RecordSize.
        /// </summary>
        /// <returns>The logicalAddress (index) of the block</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe long Allocate()
        {
#if DEBUG
            Debug.Assert(allocationMode != AllocationMode.Bulk, "Cannot mix Single and Bulk allocation modes");
            allocationMode = AllocationMode.Single;
#endif
            return InternalAllocate(1);
        }

        private unsafe long InternalAllocate(int blockSize)
        {
            if (freeList.TryDequeue(out long result))
                return result;

            // Determine insertion index.
            int index = Interlocked.Add(ref count, blockSize) - blockSize;
            int offset = index & PageSizeMask;
            int baseAddr = index >> PageSizeBits;

            // Handle indexes in first batch specially because they do not use write cache.
            if (baseAddr == 0)
            {
                // Direct-VM: the first two pages are mapped by the first allocation past the reserved chunk, not at construction.
                if (native)
                {
                    if (index != 0)
                        MapFirstPages();
                    return index;
                }

                // If index 0, then allocate space for next level.
                if (index == 0)
                {
                    NewPage(1);
                    Interlocked.MemoryBarrier();
                }

                // Return location.
                return index;
            }

            // See if write cache contains corresponding array.
            var cache = writeCacheLevel;
            T[] array;

            if (cache != -1)
            {
                // Write cache is correct array only if index is within [arrayCapacity, 2*arrayCapacity).
                if (cache == baseAddr)
                {
                    // Return location.
                    return index;
                }
            }

            // Spin-wait until level has an allocated array.
            var spinner = new SpinWait();
            while (true)
            {
                array = values[baseAddr];
                if (array != null)
                {
                    break;
                }
                spinner.SpinOnce();
            }

            // Perform extra actions if inserting at offset 0 of level.
            if (offset == 0)
            {
                // Update write cache to point to current level.
                writeCacheLevel = baseAddr;
                Interlocked.MemoryBarrier();

                // Allocate for next page
                NewPage(baseAddr + 1);

                Interlocked.MemoryBarrier();
            }

            // Return location.
            return index;
        }

        // A page's pointer is set before its array, which is what an allocation spinning on the level waits for.
        private unsafe void NewPage(int level)
        {
            if (native)
            {
                var block = DirectVirtualMemory.Allocate((long)PageSize * RecordSize, SectorSize);
                blocks[level] = block;
                pointers[level] = block.AlignedPtr;
                Volatile.Write(ref values[level], NativePage);
                return;
            }
            var tmp = GC.AllocateArray<T>(PageSize + SectorSize, pinned: IsBlittable);
            if (IsBlittable)
                pointers[level] = (IntPtr)(((long)Unsafe.AsPointer(ref tmp[0]) + (SectorSize - 1)) & ~(SectorSize - 1));
            values[level] = tmp;
        }

        private void MapFirstPages()
        {
            if (Volatile.Read(ref values[1]) is not null)
                return;
            lock (blocks)
            {
                if (values[1] is not null)
                    return;
                NewPage(0);
                NewPage(1);
                Interlocked.MemoryBarrier();
            }
        }

        private void Unmap(int level)
        {
            var block = blocks[level];
            blocks[level] = default;
            values[level] = null;
            pointers[level] = IntPtr.Zero;
            DirectVirtualMemory.Free(block);
            _ = Interlocked.Increment(ref NativePagesFreed);
        }

        private void UnmapAll()
        {
            if (Interlocked.Exchange(ref pagesFreed, 1) != 0)
                return;
            for (var i = 0; i < LevelSize; i++)
                if (!blocks[i].IsEmpty)
                    Unmap(i);
        }

        /// <summary>Bytes of the pages this allocator holds: pinned arrays, or direct-VM mappings.</summary>
        internal long PageBytes
        {
            get
            {
                long pages = 0;
                for (var i = 0; i < LevelSize && values[i] is not null; i++)
                    pages++;
                return pages * PageSize * RecordSize;
            }
        }

        /// <summary>Whether the pages are direct-VM mappings.</summary>
        internal bool IsNative => native;

        /// <summary>
        /// Dispose. A direct-VM allocator unmaps its pages now, or when the overflow checkpoint write still reading them drains.
        /// </summary>
        public void Dispose()
        {
            count = 0;
            if (!native)
                return;
            _ = Interlocked.Exchange(ref disposed, 1);
            if (Volatile.Read(ref checkpointCallbackCount) == 0)
                UnmapAll();
            else
                _ = Interlocked.Increment(ref NativeDisposesParked);
        }

        private static unsafe nint EmptyLevel(uint bytes)
        {
            if (bytes > EmptyLevelBytes)
                throw new TsavoriteException($"an unmapped overflow level of {bytes} bytes is past the {EmptyLevelBytes}-byte empty level");
            if (emptyLevel == 0)
            {
                var zeros = (nint)NativeMemory.AlignedAlloc(EmptyLevelBytes, 4096);
                NativeMemory.Clear((void*)zeros, EmptyLevelBytes);
                if (Interlocked.CompareExchange(ref emptyLevel, zeros, 0) != 0)
                    NativeMemory.AlignedFree((void*)zeros);
            }
            return emptyLevel;
        }

        /// <summary>Every allocated page's aligned start and byte length (blittable values only): what an index lock holds resident.</summary>
        internal void ForEachPage(Action<IntPtr, nint> page)
        {
            if (!IsBlittable) return;
            for (var i = 0; i < LevelSize && values[i] is not null; i++)
                page(pointers[i], (nint)PageSize * RecordSize);
        }


        #region Checkpoint

        /// <summary>
        /// Is checkpoint complete
        /// </summary>
        /// <returns></returns>
        public bool IsCheckpointCompleted() => checkpointCallbackCount == 0;

        /// <summary>
        /// Is checkpoint completed
        /// </summary>
        /// <returns></returns>
        public async ValueTask IsCheckpointCompletedAsync(CancellationToken token = default)
        {
            await checkpointTcs.Task.WaitAsync(token).ConfigureAwait(false);
        }

        public Task GetCheckpointTask() => checkpointTcs.Task;

        /// <summary>
        /// Public facing persistence API
        /// </summary>
        /// <param name="device"></param>
        /// <param name="offset"></param>
        /// <param name="numBytesWritten"></param>
        public void BeginCheckpoint(IDevice device, ulong offset, out ulong numBytesWritten)
            => BeginCheckpoint(device, offset, out numBytesWritten, false, default, default);

        /// <summary>
        /// Internal persistence API
        /// </summary>
        /// <param name="device"></param>
        /// <param name="offset"></param>
        /// <param name="numBytesWritten"></param>
        /// <param name="useReadCache"></param>
        /// <param name="skipReadCache"></param>
        /// <param name="epoch"></param>
        internal unsafe void BeginCheckpoint(IDevice device, ulong offset, out ulong numBytesWritten, bool useReadCache, SkipReadCache skipReadCache, LightEpoch epoch)
        {
            int localCount = count;
            int recordsCountInLastLevel = localCount & PageSizeMask;
            int numCompleteLevels = localCount >> PageSizeBits;
            int numLevels = numCompleteLevels + (recordsCountInLastLevel > 0 ? 1 : 0);
            checkpointCallbackCount = numLevels;
            checkpointErrorCode = 0;
            checkpointTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            uint alignedPageSize = PageSize * (uint)RecordSize;
            uint lastLevelSize = (uint)recordsCountInLastLevel * (uint)RecordSize;

            int sectorSize = (int)device.SectorSize;
            numBytesWritten = 0;
            for (int i = 0; i < numLevels; i++)
            {
                OverflowPagesFlushAsyncResult result = default;

                uint writeSize = (uint)((i == numCompleteLevels) ? (lastLevelSize + (sectorSize - 1)) & ~(sectorSize - 1) : alignedPageSize);

                var source = pointers[i] != IntPtr.Zero ? pointers[i] : EmptyLevel(writeSize);
                if (!useReadCache)
                {
                    device.WriteAsync(source, offset + numBytesWritten, writeSize, AsyncFlushCallback, result);
                }
                else
                {
                    result.mem = new SectorAlignedMemory((int)writeSize, (int)device.SectorSize);
                    bool prot = false;
                    if (!epoch.ThisInstanceProtected())
                    {
                        prot = true;
                        epoch.Resume();
                    }

                    Buffer.MemoryCopy((void*)source, result.mem.aligned_pointer, writeSize, writeSize);
                    int j = 0;
                    if (i == 0) j += AllocateChunkSize * RecordSize;
                    for (; j < writeSize; j += sizeof(HashBucket))
                    {
                        skipReadCache((HashBucket*)(result.mem.aligned_pointer + j), (HashBucket*)((byte*)source + j));
                    }

                    if (prot) epoch.Suspend();

                    device.WriteAsync((IntPtr)result.mem.aligned_pointer, offset + numBytesWritten, writeSize, AsyncFlushCallback, result);
                }
                numBytesWritten += writeSize;
            }
        }

        private unsafe void AsyncFlushCallback(uint errorCode, uint numBytes, object context)
        {
            if (errorCode != 0)
            {
                logger?.LogError($"{nameof(AsyncFlushCallback)} error: {{errorCode}}", errorCode);
                _ = Interlocked.CompareExchange(ref checkpointErrorCode, (int)errorCode, 0);
            }

            var mem = ((OverflowPagesFlushAsyncResult)context).mem;
            mem?.Dispose();

            if (Interlocked.Decrement(ref checkpointCallbackCount) == 0)
            {
                if (native && Volatile.Read(ref disposed) == 1)
                    UnmapAll();   // a dispose parked under this write
                var err = checkpointErrorCode;
                if (err != 0)
                    checkpointTcs.TrySetException(new TsavoriteException($"Overflow-bucket checkpoint flush failed with error code {err}"));
                else
                    checkpointTcs.TrySetResult(true);
            }
        }

        /// <summary>
        /// Max valid address
        /// </summary>
        /// <returns></returns>
        public int GetMaxValidAddress() => count;

        /// <summary>
        /// Get page size
        /// </summary>
        /// <returns></returns>
        public int GetPageSize() => PageSize;
        #endregion

        #region Recover
        /// <summary>
        /// Recover
        /// </summary>
        /// <param name="device"></param>
        /// <param name="buckets"></param>
        /// <param name="numBytes"></param>
        /// <param name="cancellationToken"></param>
        /// <param name="offset"></param>
        public async ValueTask<ulong> RecoverAsync(IDevice device, ulong offset, int buckets, ulong numBytes, CancellationToken cancellationToken)
        {
            BeginRecovery(device, offset, buckets, numBytes, out ulong numBytesRead, isAsync: true);
            await recoveryCountdown.WaitAsync(cancellationToken).ConfigureAwait(false);
            return numBytesRead;
        }

        // Implementation of asynchronous recovery
        private CountdownWrapper recoveryCountdown;

        internal unsafe void BeginRecovery(IDevice device,
                                    ulong offset,
                                    int buckets,
                                    ulong numBytesToRead,
                                    out ulong numBytesRead,
                                    bool isAsync = false)
        {
            // Allocate as many records in memory
            while (count < buckets)
            {
                Allocate();
            }

            int numRecords = (int)(numBytesToRead / (ulong)RecordSize);
            int recordsCountInLastLevel = numRecords & PageSizeMask;
            int numCompleteLevels = numRecords >> PageSizeBits;
            int numLevels = numCompleteLevels + (recordsCountInLastLevel > 0 ? 1 : 0);

            recoveryCountdown = new CountdownWrapper(numLevels, isAsync);

            numBytesRead = 0;
            uint alignedPageSize = (uint)PageSize * (uint)RecordSize;
            uint lastLevelSize = (uint)recordsCountInLastLevel * (uint)RecordSize;
            for (int i = 0; i < numLevels; i++)
            {
                //read a full page
                uint length = (uint)PageSize * (uint)RecordSize;
                OverflowPagesReadAsyncResult result = default;
                if (pointers[i] == IntPtr.Zero)
                    recoveryCountdown.Decrement();   // a direct-VM level no allocation mapped held only the reserved first chunk
                else
                    device.ReadAsync(offset + numBytesRead, pointers[i], length, AsyncPageReadCallback, result);
                numBytesRead += (i == numCompleteLevels) ? lastLevelSize : alignedPageSize;
            }
        }

        private unsafe void AsyncPageReadCallback(uint errorCode, uint numBytes, object context)
        {
            if (errorCode != 0)
            {
                logger?.LogError($"{nameof(AsyncPageReadCallback)} error: {{errorCode}}", errorCode);
            }
            recoveryCountdown.Decrement();
        }
        #endregion
    }
}