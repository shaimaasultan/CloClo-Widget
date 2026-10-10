using System;
using System.Collections.Generic;
using System.IO;
using System.Net.NetworkInformation;

namespace CloCloWidget;

// Local queries only. No network request, file enumeration or system change.
public static class WindowsStatus
{
    public record DriveSpace(string Root, long FreeBytes, long TotalBytes);
    public record Snapshot(bool? NetworkAvailable, IReadOnlyList<DriveSpace> Drives);

    public static Snapshot Read()
    {
        bool? networkAvailable;
        try { networkAvailable = NetworkInterface.GetIsNetworkAvailable(); }
        catch { networkAvailable = null; }

        var drives = new List<DriveSpace>();
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                try
                {
                    // Skip removable and network drives: querying a missing
                    // device or remote share can stall a small widget.
                    if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
                    var total = drive.TotalSize;
                    if (total <= 0) continue;
                    drives.Add(new DriveSpace(drive.Name, drive.AvailableFreeSpace, total));
                }
                catch { /* One inaccessible drive should not hide the others. */ }
            }
        }
        catch { /* A locked-down machine may deny drive enumeration. */ }

        return new Snapshot(networkAvailable, drives);
    }
}
