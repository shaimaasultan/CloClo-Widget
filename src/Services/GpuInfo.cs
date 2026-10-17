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

    public record Details(string Name, string? DriverVersion, DateTime? DriverDate, string? Status);

    // Name plus driver version and date, for the Windows status window.
    public static List<Details> GetDetails()
    {
        var adapters = new List<Details>();
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name, DriverVersion, DriverDate, Status FROM Win32_VideoController");
            searcher.Options.Timeout = TimeSpan.FromSeconds(10);
            using var results = searcher.Get();
            foreach (ManagementObject obj in results)
            {
                using (obj)
                {
                    var name = obj["Name"]?.ToString();
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    DateTime? date = null;
                    try { if (obj["DriverDate"] is string raw) date = ManagementDateTimeConverter.ToDateTime(raw); }
                    catch { /* An unreadable date just isn't shown. */ }
                    adapters.Add(new Details(name.Trim(), obj["DriverVersion"]?.ToString(), date, obj["Status"]?.ToString()));
                }
            }
        }
        catch
        {
            // Same best-effort rule as GetNames.
        }
        return adapters;
    }
}
