// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using Tsavorite.core;

namespace Tsavorite.test
{
    /// <summary>mesh 10-30 12:44Z: a chunk store's log (2.4 GB, its memory 128 MiB) was read from Z: at ~2.6 ms a read, because every device
    /// opens with FILE_FLAG_NO_BUFFERING and the OS cache never sees it. A device opened with buffering on and its writes written through
    /// is read from the OS cache, and a write it completes is on the disk, as an unbuffered one's is. Read from the handle's own mode.</summary>
    [TestFixture]
    public unsafe class ALogDeviceOpenedOsCachedWritesThroughTests
    {
        private const uint FileWriteThrough = 0x2, FileNoIntermediateBuffering = 0x8;   // FILE_MODE_INFORMATION bits
        private const int FileModeInformation = 16;

        [StructLayout(LayoutKind.Sequential)]
        private struct IoStatusBlock { public IntPtr Status; public IntPtr Information; }

        [DllImport("ntdll.dll")]
        private static extern int NtQueryInformationFile(SafeFileHandle handle, out IoStatusBlock status, out uint mode, uint length, int infoClass);

        private sealed class Opened(string file, bool disableFileBuffering, bool writeThrough)
            : LocalStorageDevice(file, disableFileBuffering: disableFileBuffering, initialLogFileHandles: null, writeThrough: writeThrough)
        {
            public uint Mode()
            {
                ClassicAssert.AreEqual(0, NtQueryInformationFile(GetOrAddHandle(0), out _, out var mode, sizeof(uint), FileModeInformation));
                return mode;
            }
        }

        private string dir;

        [SetUp]
        public void Setup()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) Assert.Ignore("LocalStorageDevice is Windows only");
            dir = Path.Combine(TestUtils.MethodTestDir, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
        }

        [TearDown]
        public void TearDown() => TestUtils.DeleteDirectory(dir, wait: true);

        [Test]
        public void A_device_opened_os_cached_writes_through_and_reads_back_what_it_wrote()
        {
            using (var unbuffered = new Opened(Path.Combine(dir, "control.log"), disableFileBuffering: true, writeThrough: false))
            {
                var mode = unbuffered.Mode();
                ClassicAssert.AreNotEqual(0u, mode & FileNoIntermediateBuffering, "CONTROL: the default device bypasses the OS cache");
                ClassicAssert.AreEqual(0u, mode & FileWriteThrough, "CONTROL: and asks no write-through, which NO_BUFFERING already gives it");
            }

            using var cached = new Opened(Path.Combine(dir, "cached.log"), disableFileBuffering: false, writeThrough: true);
            var sector = (int)cached.SectorSize;
            var write = (byte*)NativeMemory.AlignedAlloc((nuint)sector, (nuint)sector);
            var read = (byte*)NativeMemory.AlignedAlloc((nuint)sector, (nuint)sector);
            try
            {
                for (var i = 0; i < sector; i++) write[i] = (byte)(i * 7);
                using var done = new SemaphoreSlim(0);
                var error = uint.MaxValue;
                cached.WriteAsync((IntPtr)write, 0, 0, (uint)sector, (e, _, _) => { error = e; done.Release(); }, null);
                ClassicAssert.IsTrue(done.Wait(TimeSpan.FromSeconds(10)));
                ClassicAssert.AreEqual(0u, error);
                cached.ReadAsync(0, 0, (IntPtr)read, (uint)sector, (e, _, _) => { error = e; done.Release(); }, null);
                ClassicAssert.IsTrue(done.Wait(TimeSpan.FromSeconds(10)));
                ClassicAssert.AreEqual(0u, error);
                ClassicAssert.IsTrue(new ReadOnlySpan<byte>(write, sector).SequenceEqual(new ReadOnlySpan<byte>(read, sector)), "the sector reads back as written");

                var mode = cached.Mode();
                ClassicAssert.AreEqual(0u, mode & FileNoIntermediateBuffering, "the OS cache holds what this device reads");
                ClassicAssert.AreNotEqual(0u, mode & FileWriteThrough,
                    $"each write reaches the disk before it completes (mode 0x{mode:X}). MUTANT (write-through ignored): a completed write sits in the "
                    + "OS cache, lost at a power cut, and Tsavorite never flushes it");
            }
            finally
            {
                NativeMemory.AlignedFree(write);
                NativeMemory.AlignedFree(read);
            }
        }

        [Test]
        public void Write_through_is_refused_by_name_where_no_device_serves_it()
        {
            var e = Assert.Throws<TsavoriteException>(() => Devices.CreateLogDevice(Path.Combine(dir, "managed.log"), DeviceType.FileStream, writeThrough: true));
            StringAssert.Contains("writeThrough", e.Message);
        }
    }
}
