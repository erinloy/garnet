// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Tsavorite.core
{
    /// <summary>What a device's segment deletes came to: files whose delete was refused and are still held for a retry,
    /// retries made, deletes refused (each refusal of a segment counts once per attempt).</summary>
    public readonly record struct SegmentDeleteCounts(int Held, long Retried, long Refused);

    /// <summary>
    /// Names why a segment file could not be deleted and who holds it. A delete the OS refuses leaves a file whose index
    /// the device has already passed (startSegment moved before the delete), so it outlives every later truncation; at the
    /// next open the device's directory walk takes it as the log's start and recovery refuses (mesh 10-31 13:37Z,
    /// hlog.log.121 held while 122..438 went). The holder is asked of the Restart Manager, which sees every process.
    /// </summary>
    internal static class SegmentFiles
    {
        internal const int ErrorFileNotFound = 2, ErrorPathNotFound = 3;

        internal static string NameOf(int error) => error switch
        {
            2 => "ERROR_FILE_NOT_FOUND (2)",
            3 => "ERROR_PATH_NOT_FOUND (3)",
            5 => "ERROR_ACCESS_DENIED (5)",
            32 => "ERROR_SHARING_VIOLATION (32)",
            33 => "ERROR_LOCK_VIOLATION (33)",
            _ => $"Win32 error {error}",
        };

        /// <summary>The processes holding <paramref name="path"/> open, as "pid N (name)", or why they could not be named.</summary>
        internal static string HoldersOf(string path)
        {
            if (!OperatingSystem.IsWindows())
                return "holders not asked off Windows";
            try
            {
                var key = new char[RmSessionKeyLength + 1];
                if (RmStartSession(out var session, 0, key) != 0)
                    return "holders unknown (no Restart Manager session)";
                try
                {
                    if (RmRegisterResources(session, 1, [path], 0, IntPtr.Zero, 0, null) != 0)
                        return "holders unknown (Restart Manager refused the file)";
                    uint wanted = 0, count = 0, reasons = 0;
                    var rc = RmGetList(session, out wanted, ref count, null, ref reasons);
                    if (rc == 0 && wanted == 0)
                        return "no process (the device's own handle, or one already closed)";
                    var infos = new RmProcessInfo[wanted];
                    count = wanted;
                    if (RmGetList(session, out wanted, ref count, infos, ref reasons) != 0)
                        return "holders unknown (Restart Manager list failed)";
                    var names = new List<string>((int)count);
                    for (var i = 0; i < count; i++)
                        names.Add($"pid {infos[i].Process.ProcessId} ({infos[i].AppName})" + (infos[i].Process.ProcessId == Environment.ProcessId ? " this process" : ""));
                    return string.Join(", ", names);
                }
                finally
                {
                    _ = RmEndSession(session);
                }
            }
            catch (Exception e)
            {
                return $"holders unknown ({e.GetType().Name})";
            }
        }

        private const int RmSessionKeyLength = 32, CchRmMaxAppName = 255, CchRmMaxSvcName = 63;

        [StructLayout(LayoutKind.Sequential)]
        private struct RmUniqueProcess
        {
            public int ProcessId;
            public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct RmProcessInfo
        {
            public RmUniqueProcess Process;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CchRmMaxAppName + 1)] public string AppName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CchRmMaxSvcName + 1)] public string ServiceShortName;
            public int ApplicationType;
            public uint AppStatus;
            public uint TSSessionId;
            [MarshalAs(UnmanagedType.Bool)] public bool Restartable;
        }

        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
        private static extern int RmStartSession(out uint pSessionHandle, int dwSessionFlags, char[] strSessionKey);

        [DllImport("rstrtmgr.dll")]
        private static extern int RmEndSession(uint pSessionHandle);

        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
        private static extern int RmRegisterResources(uint pSessionHandle, uint nFiles, string[] rgsFilenames, uint nApplications, IntPtr rgApplications, uint nServices, string[] rgsServiceNames);

        [DllImport("rstrtmgr.dll")]
        private static extern int RmGetList(uint dwSessionHandle, out uint pnProcInfoNeeded, ref uint pnProcInfo, [In, Out] RmProcessInfo[] rgAffectedApps, ref uint lpdwRebootReasons);
    }
}
