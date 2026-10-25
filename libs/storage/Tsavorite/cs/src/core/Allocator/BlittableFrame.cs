// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Tsavorite.core
{
    using static Utility;

    /// <summary>
    /// A frame is an in-memory circular buffer of log pages
    /// </summary>
    internal sealed class BlittableFrame : IDisposable
    {
        public readonly int frameSize, pageSize, sectorSize;
        public readonly byte[][] frame;
        public readonly long[] pointers;

        // Per-slot direct-VM backing block when the Frames native surface is enabled; the parallel
        // entry in <see cref="frame"/> is null. Null array for the managed backend. Freed deterministically in
        // <see cref="Dispose"/> — safe because every BlittableFrame owner is a scan iterator that drains all
        // outstanding device reads (ScanIteratorBase.Dispose) before calling frame.Dispose(), so no device IO
        // references a frame slot when it is unmapped.
        readonly DirectVmBlock[] blocks;

        // The allocator's slot buffers, borrowed on Allocate and handed back on Dispose; null when the frame keeps its own.
        readonly FrameSlotPool pool;

        public BlittableFrame(int frameSize, int pageSize, int sectorSize, FrameSlotPool pool = null)
        {
            this.frameSize = frameSize;
            this.pageSize = pageSize;
            this.sectorSize = sectorSize;
            this.pool = pool;

            frame = new byte[frameSize][];
            pointers = new long[frameSize];
            if (NativeAllocatorInitializer.Enabled)
                blocks = new DirectVmBlock[frameSize];
        }

        /// <summary>Whether frame slot <paramref name="index"/> is already backed (managed pinned array or
        /// direct-VM block). Backend-neutral so slots are reused across pages instead of re-allocated.</summary>
        public bool IsAllocated(int index) => pointers[index] != 0;

        public unsafe void Allocate(int index)
        {
            var adjustedSize = pageSize + 2 * sectorSize;

            if (blocks is not null)
            {
                // Direct-VM (mmap/VirtualAlloc): demand-zero, first-touch-placed, matching GC.AllocateArray.
                var block = DirectVirtualMemory.Allocate(adjustedSize, sectorSize);
                blocks[index] = block;
                pointers[index] = block.AlignedPtr;
                frame[index] = null;
                return;
            }

            var tmp = pool?.Rent() ?? GC.AllocateArray<byte>(adjustedSize, pinned: true);
            var p = (long)Unsafe.AsPointer(ref tmp[0]);
            pointers[index] = RoundUp(p, sectorSize);
            frame[index] = tmp;
        }

        public void Clear(int pageIndex)
        {
            if (frame[pageIndex] is not null)
                Array.Clear(frame[pageIndex], 0, frame[pageIndex].Length);
            else
                DirectVirtualMemory.Clear((nint)pointers[pageIndex], pageSize + 2 * sectorSize);
        }

        public long GetPhysicalAddress(long frameNumber, long offset = 0)
        {
            return pointers[frameNumber % frameSize] + offset;
        }

        public unsafe (byte[] array, long offset) GetArrayAndUnalignedOffset(long frameNumber, long alignedOffset)
        {
            var frameIndex = frameNumber % frameSize;

            long ptr = (long)Unsafe.AsPointer(ref frame[frameIndex]);
            return (frame[frameIndex], alignedOffset + ptr - pointers[frameIndex]);
        }

        public void Dispose()
        {
            // Free native frame blocks deterministically: the owning scan iterator has already drained all
            // outstanding device reads (ScanIteratorBase.Dispose runs before this), so no IO references a slot.
            // Also clear pointers[] so that a post-Dispose reuse (ScanIteratorBase.Reset calls Dispose then keeps
            // the same frame) re-allocates via IsAllocated==false instead of dereferencing a freed block.
            // Managed backend: with a pool, every slot goes back to it and the frame re-borrows after a Reset; without one, the
            // pinned arrays are GC-reclaimed and a reused frame legitimately keeps its still-live arrays.
            if (blocks is null)
            {
                if (pool is null)
                    return;
                for (var i = 0; i < frame.Length; i++)
                {
                    if (frame[i] is { } slot)
                        pool.Return(slot);
                    frame[i] = null;
                    pointers[i] = 0;
                }
                return;
            }
            for (var i = 0; i < blocks.Length; i++)
            {
                DirectVirtualMemory.Free(blocks[i]);
                blocks[i] = default;
                pointers[i] = 0;
            }
        }
    }

    /// <summary>
    /// One allocator's pinned scan-frame slots (page size plus two sectors each), handed from a disposed scan to the next one.
    /// A scan builds a new frame per iterator, and a windowed walk opens one iterator per window, so without this every window
    /// pinned a fresh array for each page it loaded. Holds at most <see cref="ScanReadAhead.Bytes"/> of slots; past that a
    /// returned slot is left to the GC. A rented slot is cleared, matching the zeroed array a fresh allocation gives.
    /// </summary>
    internal sealed class FrameSlotPool
    {
        readonly int slotBytes;
        readonly Stack<byte[]> slots = new();
        long rented, made;

        public FrameSlotPool(int pageSize, int sectorSize) => slotBytes = pageSize + 2 * sectorSize;

        /// <summary>Slots handed out from the pool (not made) since it was built.</summary>
        public long Reused => Volatile.Read(ref rented);

        /// <summary>Slots the pool had to make because it held none.</summary>
        public long Made => Volatile.Read(ref made);

        public byte[] Rent()
        {
            byte[] slot = null;
            lock (slots)
                if (slots.Count > 0)
                    slot = slots.Pop();
            if (slot is null)
            {
                _ = Interlocked.Increment(ref made);
                return GC.AllocateArray<byte>(slotBytes, pinned: true);
            }
            _ = Interlocked.Increment(ref rented);
            Array.Clear(slot);
            return slot;
        }

        public void Return(byte[] slot)
        {
            if (slot.Length != slotBytes)
                return;
            lock (slots)
                if ((long)(slots.Count + 1) * slotBytes <= Math.Max(ScanReadAhead.Bytes, 2L * slotBytes))
                    slots.Push(slot);
        }
    }
}
