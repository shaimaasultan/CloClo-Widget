using System;
using System.Collections.Generic;
using System.Management;

namespace CloCloWidget;

// The GPUs Windows itself knows about, via the same WMI class Device
// Manager reads from (Win32_VideoController) — covers integrated and
// discrete alike, whether or not they're the one actually driving a
// display right now.
public static class GpuInfo
{
    public static List<string> GetNames()
    {
        var names = new List<string>();
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_VideoController");
            using var results = searcher.Get();
            foreach (ManagementObject obj in results)
            {
                using (obj)
                {
                    var name = obj["Name"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(name)) names.Add(name);
                }
            }
        }
        catch
        {
            // Best-effort — WMI can be unavailable in locked-down
            // environments; just report nothing found rather than crash.
        }
        return names;
    }
}
