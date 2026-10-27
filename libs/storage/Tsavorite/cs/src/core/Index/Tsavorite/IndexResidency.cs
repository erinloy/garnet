// Copyright (c) Microsoft Corporation. Licensed under the MIT license.

using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Tsavorite.core
{
    public unsafe partial class TsavoriteBase
    {
        /// <summary>
        /// Lock the hash index (main table and every overflow page) in physical memory once recovery has loaded it, before recovery's
        /// bucket pass. For a store that is opened, read and closed under memory pressure (an offline rebuild's source): a working-set
        /// minimum alone is trimmed through under pressure, and a trimmed table turns recovery's pass and every keyed read into a hard
        /// fault each. The process's working-set minimum must already cover the table; a lock the OS refuses throws by name. The lock
        /// is for a store whose index will not grow: a grow's new table is not locked.
        /// </summary>
        public bool LockIndexResidentAtRecovery { get; set; }

        /// <summary>Bytes of hash index this store holds locked in physical memory (0 unless <see cref="LockIndexResidentAtRecovery"/>).</summary>
        public long LockedIndexBytes => Interlocked.Read(ref lockedIndexBytes);

        private long lockedIndexBytes;
        private (nint At, nint Bytes)[] lockedIndexRanges = [];

        /// <summary>Locks the current table and every overflow page; returns the bytes locked.</summary>
        internal long LockIndexResident()
        {
            var table = state[resizeInfo.version];
            var ranges = new System.Collections.Generic.List<(nint, nint)> { ((nint)table.tableAligned, (nint)(table.size * sizeof(HashBucket))) };
            overflowBucketsAllocator.ForEachPage((at, bytes) => ranges.Add((at, bytes)));
            long locked = 0;
            foreach (var (at, bytes) in ranges)
            {
                if (!IndexPageLock.Lock(at, bytes, out var error))
                {
                    UnlockIndexRanges(ranges.GetRange(0, ranges.IndexOf((at, bytes))));
                    throw new TsavoriteException($"LockIndexResident: the OS refused to lock {bytes:N0} bytes of the hash index at 0x{at:X} (error {error}) "
                        + $"after {locked:N0} of {Sum(ranges):N0}; raise the process's working-set minimum to cover the index first.");
                }
                locked += bytes;
            }
            lockedIndexRanges = ranges.ToArray();
            Interlocked.Exchange(ref lockedIndexBytes, locked);
            return locked;
        }

        private void UnlockIndexResident()
        {
            UnlockIndexRanges(Interlocked.Exchange(ref lockedIndexRanges, []));
            Interlocked.Exchange(ref lockedIndexBytes, 0);
        }

        private static void UnlockIndexRanges(System.Collections.Generic.IEnumerable<(nint At, nint Bytes)> ranges)
        {
            foreach (var (at, bytes) in ranges) _ = IndexPageLock.Unlock(at, bytes);
        }

        private static long Sum(System.Collections.Generic.List<(nint At, nint Bytes)> ranges)
        {
            long sum = 0;
            foreach (var (_, bytes) in ranges) sum += bytes;
            return sum;
        }
    }

    /// <summary>VirtualLock on Windows, mlock elsewhere.</summary>
    internal static class IndexPageLock
    {
        public static bool Lock(nint at, nint bytes, out int error)
        {
            var ok = OperatingSystem.IsWindows() ? VirtualLock(at, bytes) : mlock(at, bytes) == 0;
            error = ok ? 0 : Marshal.GetLastWin32Error();
            return ok;
        }

        public static bool Unlock(nint at, nint bytes) => OperatingSystem.IsWindows() ? VirtualUnlock(at, bytes) : munlock(at, bytes) == 0;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool VirtualLock(nint address, nint size);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool VirtualUnlock(nint address, nint size);

        [DllImport("libc", SetLastError = true)]
        private static extern int mlock(nint address, nint size);

        [DllImport("libc", SetLastError = true)]
        private static extern int munlock(nint address, nint size);
    }
}
