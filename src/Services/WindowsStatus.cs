using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Windows.Networking.Connectivity;

namespace CloCloWidget;

// Local queries only: Windows' own counters, registry values, WMI and the
// power and display APIs. No network request, file enumeration, elevation
// or system change. Every reader is called off the UI thread and returns
// what it could read; a missing value is null, never an exception.
//
// Private details are left out on purpose: no user name, MAC addresses,
// serial numbers, Wi-Fi network names, global IPv6 addresses, process paths,
// command lines or window titles.
internal static class WindowsStatus
{
    // ---- Overview ----

    public record OsInfo(string? Edition, string? Version, string? Build, string Architecture,
        string PcName, DateTime? Installed, TimeSpan Uptime);

    public static OsInfo ReadOs()
    {
        string? edition = null, version = null, build = null;
        DateTime? installed = null;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            if (key != null)
            {
                edition = key.GetValue("ProductName") as string;
                version = key.GetValue("DisplayVersion") as string;
                var currentBuild = key.GetValue("CurrentBuild") as string;
                var revision = key.GetValue("UBR") is int ubr ? ubr : (int?)null;
                build = currentBuild == null ? null : revision == null ? currentBuild : $"{currentBuild}.{revision}";
                if (key.GetValue("InstallDate") is int seconds && seconds > 0)
                    installed = DateTimeOffset.FromUnixTimeSeconds((uint)seconds).LocalDateTime;
                // Windows 11 still reports "Windows 10" here for compatibility.
                if (edition != null && int.TryParse(currentBuild, out var number) && number >= 22000)
                    edition = edition.Replace("Windows 10", "Windows 11");
            }
        }
        catch { /* A locked-down registry just leaves these blank. */ }

        return new OsInfo(edition, version, build, RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant() switch
        {
            "x64" => "64-bit (x64)",
            "arm64" => "64-bit (ARM64)",
            "x86" => "32-bit (x86)",
            var other => other,
        }, Environment.MachineName, installed, Uptime());
    }

    // Time since Windows last started. A shutdown with Fast Startup resumes
    // the previous session, so only a restart resets it (as in Task Manager).
    public static TimeSpan Uptime() => TimeSpan.FromMilliseconds(Environment.TickCount64);

    // ---- Processor and memory ----

    public record Processor(string? Name, int LogicalProcessors, int? Cores, int? MemoryModules, uint? MemorySpeedMts);

    public static Processor ReadProcessor()
    {
        string? name = null;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            name = (key?.GetValue("ProcessorNameString") as string)?.Trim();
        }
        catch { }

        int? cores = null;
        ForEachWmi(@"root\cimv2", "SELECT NumberOfCores FROM Win32_Processor", obj =>
        {
            if (obj["NumberOfCores"] is uint count) cores = (cores ?? 0) + (int)count;
        });

        int? modules = null;
        uint? speed = null;
        ForEachWmi(@"root\cimv2", "SELECT ConfiguredClockSpeed, Speed FROM Win32_PhysicalMemory", obj =>
        {
            modules = (modules ?? 0) + 1;
            var configured = obj["ConfiguredClockSpeed"] as uint?;
            var mts = configured is > 0 ? configured : obj["Speed"] as uint?;
            if (mts is > 0) speed = Math.Max(speed ?? 0, mts.Value);
        });

        return new Processor(name, Environment.ProcessorCount, cores, modules, speed);
    }

    // Keeps the previous counters, so usage and network speed are measured
    // over the time between two refreshes.
    public sealed class Sampler
    {
        private (long Idle, long Kernel, long User)? _cpu;
        private readonly Dictionary<string, (long Received, long Sent, long Tick)> _network = new();

        public double? CpuPercent()
        {
            var now = WindowsStatusNative.SystemTimes();
            var previous = _cpu;
            _cpu = now;
            if (now == null || previous == null) return null;
            var idle = now.Value.Idle - previous.Value.Idle;
            var total = now.Value.Kernel - previous.Value.Kernel + now.Value.User - previous.Value.User;
            return total <= 0 ? null : Math.Clamp(100.0 * (total - idle) / total, 0, 100);
        }

        public (double Down, double Up)? BitsPerSecond(string adapterId, long received, long sent)
        {
            var tick = Environment.TickCount64;
            var found = _network.TryGetValue(adapterId, out var previous);
            _network[adapterId] = (received, sent, tick);
            var seconds = (tick - previous.Tick) / 1000.0;
            if (!found || seconds < 0.5 || received < previous.Received || sent < previous.Sent) return null;
            return ((received - previous.Received) * 8 / seconds, (sent - previous.Sent) * 8 / seconds);
        }
    }

    public record Live(TimeSpan Uptime, double? CpuPercent, WindowsStatusNative.Memory? Memory,
        WindowsStatusNative.Performance? Performance);

    public static Live ReadLive(Sampler sampler) =>
        new(Uptime(), sampler.CpuPercent(), WindowsStatusNative.ReadMemory(), WindowsStatusNative.ReadPerformance());

    public record ProcessMemory(string Name, int Count, long Bytes);

    // Grouped by process name (all of a browser's processes together). The
    // sizes come from Windows' process snapshot, so no process is opened.
    public static List<ProcessMemory> ReadTopProcesses(int count)
    {
        var processes = System.Diagnostics.Process.GetProcesses();
        try
        {
            return processes
                .Select(p =>
                {
                    try { return (Name: p.ProcessName, Bytes: p.WorkingSet64); }
                    catch { return (Name: "", Bytes: 0L); }
                })
                .Where(p => p.Name.Length > 0 && p.Bytes > 0)
                .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .Select(g => new ProcessMemory(g.First().Name, g.Count(), g.Sum(p => p.Bytes)))
                .OrderByDescending(p => p.Bytes)
                .Take(count)
                .ToList();
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }
    }

    // ---- Storage ----

    public record DriveSpace(string Root, string? Label, string? Format, long FreeBytes, long TotalBytes, bool IsSystem);

    public static List<DriveSpace> ReadDrives()
    {
        var drives = new List<DriveSpace>();
        var systemRoot = Path.GetPathRoot(Environment.SystemDirectory);
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
                    var label = drive.VolumeLabel;
                    drives.Add(new DriveSpace(drive.Name, string.IsNullOrWhiteSpace(label) ? null : label.Trim(),
                        drive.DriveFormat, drive.AvailableFreeSpace, total,
                        string.Equals(drive.Name, systemRoot, StringComparison.OrdinalIgnoreCase)));
                }
                catch { /* One inaccessible drive should not hide the others. */ }
            }
        }
        catch { /* A locked-down machine may deny drive enumeration. */ }
        return drives;
    }

    // ---- Network ----

    public record Adapter(string Name, string Description, string Kind, long SpeedBitsPerSecond,
        IReadOnlyList<string> IPv4, int IPv6Count, bool HasGateway, bool? Dhcp, (double Down, double Up)? Rate);

    public record NetworkState(bool? Available, string? Connectivity, bool? Metered,
        IReadOnlyList<Adapter> Adapters, int DisconnectedCount);

    public static NetworkState ReadNetwork(Sampler sampler)
    {
        bool? available;
        try { available = NetworkInterface.GetIsNetworkAvailable(); }
        catch { available = null; }

        // Windows' own record of the current connection. Reading it sends
        // nothing; CloClo never tests internet access itself.
        string? connectivity = null;
        bool? metered = null;
        try
        {
            var profile = NetworkInformation.GetInternetConnectionProfile();
            if (profile == null) connectivity = "No connection";
            else
            {
                connectivity = profile.GetNetworkConnectivityLevel() switch
                {
                    NetworkConnectivityLevel.InternetAccess => "Internet access",
                    NetworkConnectivityLevel.ConstrainedInternetAccess => "Limited (sign-in page or restricted)",
                    NetworkConnectivityLevel.LocalAccess => "Local network only",
                    _ => "No connection",
                };
                var cost = profile.GetConnectionCost();
                metered = cost.NetworkCostType is NetworkCostType.Fixed or NetworkCostType.Variable;
            }
        }
        catch { /* Not available in every session. */ }

        var adapters = new List<Adapter>();
        var disconnected = 0;
        try
        {
            var all = NetworkInterface.GetAllNetworkInterfaces();
            // Windows also lists each filter driver attached to an adapter
            // (e.g. "Wi-Fi-WFP Native MAC Layer LightWeight Filter-0000") as
            // an interface of its own; only the adapter itself is of interest.
            var descriptions = all.Select(n => n.Description).ToList();
            bool IsFilter(NetworkInterface nic) =>
                descriptions.Any(d => d.Length < nic.Description.Length && nic.Description.StartsWith(d + "-", StringComparison.Ordinal));

            foreach (var nic in all)
            {
                try
                {
                    if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel || IsFilter(nic)) continue;
                    if (nic.OperationalStatus != OperationalStatus.Up)
                    {
                        // An unplugged cable or Wi-Fi that's off; not absent
                        // devices or Windows' own Wi-Fi Direct helpers.
                        if (nic.OperationalStatus == OperationalStatus.Down
                            && nic.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211
                            && !nic.Description.Contains("Wi-Fi Direct", StringComparison.OrdinalIgnoreCase))
                            disconnected++;
                        continue;
                    }
                    var properties = nic.GetIPProperties();
                    var unicast = properties.UnicastAddresses;
                    var ipv4 = unicast.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                        .Select(a => $"{a.Address}/{a.PrefixLength}").ToList();
                    var ipv6 = unicast.Count(a => a.Address.AddressFamily == AddressFamily.InterNetworkV6);
                    bool? dhcp = null;
                    try { dhcp = properties.GetIPv4Properties()?.IsDhcpEnabled; } catch { }
                    (double, double)? rate = null;
                    try
                    {
                        var stats = nic.GetIPStatistics();
                        rate = sampler.BitsPerSecond(nic.Id, stats.BytesReceived, stats.BytesSent);
                    }
                    catch { }
                    adapters.Add(new Adapter(nic.Name, nic.Description, Kind(nic.NetworkInterfaceType), nic.Speed,
                        ipv4, ipv6, properties.GatewayAddresses.Count > 0, dhcp, rate));
                }
                catch { /* One unreadable adapter should not hide the others. */ }
            }
        }
        catch { }

        // Connected adapters with a gateway (the ones in real use) first.
        adapters = adapters.OrderByDescending(a => a.HasGateway).ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        return new NetworkState(available, connectivity, metered, adapters, disconnected);
    }

    private static string Kind(NetworkInterfaceType type) => type switch
    {
        NetworkInterfaceType.Wireless80211 => "Wi-Fi",
        NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT
            or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.Ethernet3Megabit => "Ethernet",
        NetworkInterfaceType.Wwanpp or NetworkInterfaceType.Wwanpp2 => "Mobile broadband",
        NetworkInterfaceType.Ppp => "Dial-up or VPN",
        _ => "Other",
    };

    // ---- Power ----

    public record PowerState(bool? OnAc, BatteryInfo.Status? Battery, bool BatterySaver, string? Plan, string? Mode);

    public static PowerState ReadPower()
    {
        var status = WindowsStatusNative.ReadPowerStatus();
        string? plan = null, mode = null;
        try { plan = WindowsStatusNative.ActivePowerPlanName(); } catch { }
        try { mode = WindowsStatusNative.PowerMode(); } catch { }
        BatteryInfo.Status? battery = null;
        try { battery = BatteryInfo.Read(); } catch { }
        return new PowerState(status?.OnAc, battery, status?.BatterySaver ?? false, plan, mode);
    }

    // ---- Displays and graphics ----

    public record Display(string Name, bool Primary, int Width, int Height, double? RefreshHz, int? ScalePercent,
        bool Portrait, string? Connection, bool? HdrSupported, bool? HdrOn, string? Gpu, long AdapterLuid);

    public static List<Display> ReadDisplays()
    {
        var monitors = WindowsStatusNative.ReadMonitors();
        List<WindowsStatusNative.DisplayPath> paths;
        try { paths = WindowsStatusNative.ReadDisplayPaths(); } catch { paths = []; }
        List<WindowsStatusNative.GraphicsAdapter> gpus;
        try { gpus = WindowsStatusNative.ReadGraphicsAdapters(); } catch { gpus = []; }

        var displays = new List<Display>();
        foreach (var monitor in monitors)
        {
            // A duplicated (mirrored) desktop has several paths per monitor.
            var matching = paths.Where(p => string.Equals(p.GdiName, monitor.GdiName, StringComparison.OrdinalIgnoreCase)).ToList();
            var path = matching.FirstOrDefault();
            var names = matching.Select(p => p.FriendlyName).Where(n => n != null).Distinct().ToList();
            displays.Add(new Display(
                names.Count > 0 ? string.Join(" + ", names) : path?.Connection?.StartsWith("Built-in") == true ? "Built-in display" : "Display",
                monitor.Primary, monitor.Width, monitor.Height, path?.RefreshHz, monitor.ScalePercent,
                path?.Portrait ?? monitor.Height > monitor.Width, path?.Connection,
                path?.HdrSupported, path?.HdrOn,
                path == null ? null : gpus.FirstOrDefault(g => g.Luid == path.AdapterLuid)?.Name,
                path?.AdapterLuid ?? 0));
        }
        // Primary first, then left to right as Windows numbers them.
        return displays.OrderByDescending(d => d.Primary).ToList();
    }

    public record Gpu(string Name, ulong? DedicatedBytes, ulong? SharedBytes, string? DriverVersion,
        DateTime? DriverDate, string? Status, int DisplaysConnected);

    public static List<Gpu> ReadGraphics()
    {
        var details = GpuInfo.GetDetails();
        List<WindowsStatusNative.GraphicsAdapter> adapters;
        try { adapters = WindowsStatusNative.ReadGraphicsAdapters(); } catch { adapters = []; }
        List<WindowsStatusNative.DisplayPath> paths;
        try { paths = WindowsStatusNative.ReadDisplayPaths(); } catch { paths = []; }

        int Displays(WindowsStatusNative.GraphicsAdapter? adapter) =>
            adapter == null ? 0 : paths.Where(p => p.AdapterLuid == adapter.Luid).Select(p => p.GdiName).Distinct().Count();

        var result = details.Select(d =>
        {
            var adapter = adapters.FirstOrDefault(a => string.Equals(a.Name, d.Name, StringComparison.OrdinalIgnoreCase));
            return new Gpu(d.Name, adapter?.DedicatedBytes, adapter?.SharedBytes, d.DriverVersion, d.DriverDate, d.Status, Displays(adapter));
        }).ToList();

        // If WMI is unavailable, DirectX still names the adapters.
        foreach (var adapter in adapters.Where(a => !result.Any(g => string.Equals(g.Name, a.Name, StringComparison.OrdinalIgnoreCase))))
            result.Add(new Gpu(adapter.Name, adapter.DedicatedBytes, adapter.SharedBytes, null, null, null, Displays(adapter)));

        return result.OrderByDescending(g => g.DedicatedBytes ?? 0).ToList();
    }

    // ---- Security and updates ----

    public record Antivirus(string Name, bool? On, bool? UpToDate);

    public record SecurityState(
        IReadOnlyList<Antivirus> Antivirus, IReadOnlyList<string> OtherFirewalls,
        bool? DefenderRealTime, DateTime? DefenderSignatures, DateTime? DefenderQuickScan, bool? TamperProtection,
        IReadOnlyList<(string Profile, bool? On)> Firewall,
        bool? SecureBoot, bool? Uac, bool? MemoryIntegrity, string? SmartAppControl);

    // What Windows Security shows, read the way it reads it. Standard users
    // can read all of this; nothing is checked online or changed.
    public static SecurityState ReadSecurity()
    {
        var antivirus = new List<Antivirus>();
        ForEachWmi(@"root\SecurityCenter2", "SELECT displayName, productState FROM AntiVirusProduct", obj =>
        {
            var name = obj["displayName"]?.ToString();
            if (string.IsNullOrWhiteSpace(name)) return;
            bool? on = null, upToDate = null;
            if (obj["productState"] is uint state)
            {
                on = ((state >> 12) & 0xF) is 1 or 3;
                upToDate = ((state >> 4) & 0xF) == 0;
            }
            antivirus.Add(new Antivirus(name.Trim(), on, upToDate));
        });

        var otherFirewalls = new List<string>();
        ForEachWmi(@"root\SecurityCenter2", "SELECT displayName FROM FirewallProduct", obj =>
        {
            if (obj["displayName"]?.ToString() is { Length: > 0 } name) otherFirewalls.Add(name.Trim());
        });

        bool? realTime = null, tamper = null;
        DateTime? signatures = null, quickScan = null;
        ForEachWmi(@"root\Microsoft\Windows\Defender",
            "SELECT RealTimeProtectionEnabled, AntivirusSignatureLastUpdated, QuickScanEndTime, IsTamperProtected, AMServiceEnabled FROM MSFT_MpComputerStatus",
            obj =>
            {
                if (obj["AMServiceEnabled"] is false) return; // Another antivirus is in charge.
                realTime = obj["RealTimeProtectionEnabled"] as bool?;
                tamper = obj["IsTamperProtected"] as bool?;
                signatures = WmiDate(obj["AntivirusSignatureLastUpdated"]);
                quickScan = WmiDate(obj["QuickScanEndTime"]);
            });

        var firewall = new List<(string, bool?)>();
        foreach (var (profile, label) in new[] { ("DomainProfile", "Domain network"), ("StandardProfile", "Private network"), ("PublicProfile", "Public network") })
        {
            // Group Policy, when set, overrides the local setting.
            var policy = RegistryInt(@"SOFTWARE\Policies\Microsoft\WindowsFirewall\" + profile, "EnableFirewall");
            var local = RegistryInt(@"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\" + profile, "EnableFirewall");
            var value = policy ?? local;
            firewall.Add((label, value == null ? null : value != 0));
        }

        var secureBoot = RegistryInt(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State", "UEFISecureBootEnabled");
        var uac = RegistryInt(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", "EnableLUA");
        var hvci = RegistryInt(@"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity", "Enabled");
        var sac = RegistryInt(@"SYSTEM\CurrentControlSet\Control\CI\Policy", "VerifiedAndReputablePolicyState") switch
        {
            0 => "Off",
            1 => "On",
            2 => "Evaluation",
            _ => null,
        };

        return new SecurityState(antivirus, otherFirewalls, realTime, signatures, quickScan, tamper, firewall,
            secureBoot == null ? null : secureBoot != 0, uac == null ? null : uac != 0,
            hvci == null ? null : hvci != 0, sac);
    }

    public record UpdateState(string? LatestId, DateTime? LatestInstalled, bool RestartPending);

    // The local list of installed updates (what Settings > Windows Update >
    // Update history draws from). It is not compared with what is available
    // online: that would mean contacting Windows Update.
    public static UpdateState ReadUpdates()
    {
        string? latestId = null;
        DateTime? latest = null;
        ForEachWmi(@"root\cimv2", "SELECT HotFixID, InstalledOn FROM Win32_QuickFixEngineering", obj =>
        {
            var date = ParseInstalledOn(obj["InstalledOn"]?.ToString());
            if (date != null && (latest == null || date > latest))
            {
                latest = date;
                latestId = obj["HotFixID"]?.ToString();
            }
        });

        var restart = RegistryKeyExists(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired")
            || RegistryKeyExists(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending");
        return new UpdateState(latestId, latest, restart);
    }

    private static DateTime? ParseInstalledOn(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (DateTime.TryParseExact(raw, ["M/d/yyyy", "MM/dd/yyyy", "yyyyMMdd"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return date;
        // Some entries hold a FILETIME in hexadecimal instead.
        if (long.TryParse(raw, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var fileTime))
        {
            try { return DateTime.FromFileTime(fileTime).Date; } catch { }
        }
        return null;
    }

    // ---- Helpers ----

    // A WMI query with a time limit, so a stuck provider ends instead of
    // holding a background thread. Errors (including access denied on a
    // locked-down PC) just leave the values unread.
    private static void ForEachWmi(string scope, string query, Action<ManagementBaseObject> read)
    {
        try
        {
            var options = new System.Management.EnumerationOptions { Timeout = TimeSpan.FromSeconds(10), ReturnImmediately = true, Rewindable = false };
            using var searcher = new ManagementObjectSearcher(new ManagementScope(scope), new ObjectQuery(query), options);
            using var results = searcher.Get();
            foreach (var obj in results)
            {
                using (obj)
                {
                    try { read(obj); } catch { }
                }
            }
        }
        catch { }
    }

    private static DateTime? WmiDate(object? value)
    {
        try { return value is string raw && raw.Length > 0 ? ManagementDateTimeConverter.ToDateTime(raw) : value as DateTime?; }
        catch { return null; }
    }

    private static int? RegistryInt(string path, string name)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(path);
            return key?.GetValue(name) is int value ? value : null;
        }
        catch { return null; }
    }

    private static bool RegistryKeyExists(string path)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(path);
            return key != null;
        }
        catch { return false; }
    }
}
