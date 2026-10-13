using System;
using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;

namespace CloCloWidget;

// The Run key has no ordering guarantee. At sign-in, wait until the Defender
// antivirus, Windows Security, and Security Center services report Running
// before creating the widget. A bounded wait also covers PCs where Defender
// is disabled by policy or another antivirus is installed.
internal static class StartupReadiness
{
    private static readonly TimeSpan MaximumWait = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);

    public static void WaitForSecurityServices()
    {
        var timer = Stopwatch.StartNew();
        var stableChecks = 0;
        while (timer.Elapsed < MaximumWait)
        {
            try
            {
                stableChecks = ServicesRunning() ? stableChecks + 1 : 0;
                if (stableChecks >= 3) return;
            }
            catch (Exception ex) when (ex is ManagementException or COMException or InvalidOperationException)
            {
                // WMI may itself be unavailable during sign-in. The timeout
                // remains the fallback; never request elevation or change a
                // Windows security service to make the widget start.
                stableChecks = 0;
            }
            catch (UnauthorizedAccessException)
            {
                // A policy may deny service status reads to this user.
                Thread.Sleep(TimeSpan.FromSeconds(15));
                return;
            }

            var remaining = MaximumWait - timer.Elapsed;
            if (remaining > TimeSpan.Zero)
                Thread.Sleep(remaining < PollInterval ? remaining : PollInterval);
        }
    }

    private static bool ServicesRunning()
    {
        using var searcher = new ManagementObjectSearcher(
            "SELECT Name, State FROM Win32_Service WHERE Name='WinDefend' OR Name='SecurityHealthService' OR Name='wscsvc'");
        using var results = searcher.Get();
        var defender = false;
        var security = false;
        var securityCenter = false;
        foreach (ManagementObject service in results)
        {
            using (service)
            {
                if (!string.Equals(service["State"] as string, "Running", StringComparison.OrdinalIgnoreCase))
                    continue;
                switch (service["Name"] as string)
                {
                    case "WinDefend": defender = true; break;
                    case "SecurityHealthService": security = true; break;
                    case "wscsvc": securityCenter = true; break;
                }
            }
        }
        return defender && security && securityCenter;
    }
}
