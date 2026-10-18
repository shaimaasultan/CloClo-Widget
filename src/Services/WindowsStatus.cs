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
        string PcName, DateTime? Installed, TimeSpan Uptime, DateTime? SignedIn);

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
        }, Environment.MachineName, installed, Uptime(), WindowsStatusNative.SessionSignInTime());
    }

    // Time since Windows last started. A shutdown with Fast Startup resumes
    // the previous session, so only a restart resets it (as in Task Manager).
    public static TimeSpan Uptime() => TimeSpan.FromMilliseconds(Environment.TickCount64);

    // Of that, the time spent in sleep or hibernation (closing the lid,
    // usually): uptime less the time Windows was awake.
    public static TimeSpan? Asleep(TimeSpan uptime)
    {
        if (WindowsStatusNative.AwakeSinceBoot() is not { } awake) return null;
        return uptime > awake ? uptime - awake : TimeSpan.Zero;
    }

    // The kinds of time the PC was asleep or off. A shutdown with Fast
    // Startup hibernates the kernel session, so Windows logs it like a
    // hibernation; to the user it was a shutdown. Windows always restarts in
    // full, so FastStartupRestart only covers a restart that still came back
    // through Fast Startup. Shutdown, Restart and Unexpected end Windows'
    // session and reset uptime, so only the power history has them.
    public enum SleepKind { Sleep, Hibernate, FastStartupShutdown, FastStartupRestart, Shutdown, Restart, Unexpected }

    // Cause is what Windows gives as the reason for a sleep or hibernation
    // ("power button or lid"); null for the other kinds or when it isn't
    // logged. The rest is what Windows logged, to show next to what the user
    // did: its messages going down and starting up (in Windows' own words),
    // the power state asked for and the one entered, who asked for a
    // shutdown ("Start", "sign-in screen" or an app's file name) and a
    // sign-out just before it.
    public record SleepPeriod(DateTime Start, DateTime End, SleepKind Kind, string? Cause = null)
    {
        public string? DownMessage { get; init; }
        public string? UpMessage { get; init; }
        public string? TargetState { get; init; }
        public string? EffectiveState { get; init; }
        public string? BootType { get; init; }
        public string? RequestedBy { get; init; }
        public DateTime? SignedOutBefore { get; init; }
    }

    // Each sleep, hibernation or Fast Startup shutdown since Windows started,
    // oldest first. Null when the log can't be read.
    public static List<SleepPeriod>? ReadSleepPeriods() => ReadSleepPeriods(null);

    // Each sleep, hibernation or Fast Startup shutdown woken from since a UTC
    // time (or since Windows started), oldest first, from the event Windows
    // itself logs on every wake (Power-Troubleshooter 1, readable without
    // administrator rights). CloClo can't time these itself: it isn't
    // running while the PC sleeps after a sign-out. Null when the log can't
    // be read.
    private static List<SleepPeriod>? ReadSleepPeriods(DateTime? since)
    {
        var from = since ?? DateTime.UtcNow - Uptime();
        var events = ReadSystemEvents("Microsoft-Windows-Power-Troubleshooter", "EventID=1", from);
        if (events == null) return null;
        // A sleep woken from today may have started days ago.
        var back = from.AddDays(-7);
        // Kernel-Power 42 is logged as the PC goes down and says what was
        // asked for: TargetState 6 (S5) is a shutdown that Fast Startup turned
        // into a hibernation, 5 a real hibernation, with its reason.
        var entries = Times(ReadSystemEvents("Microsoft-Windows-Kernel-Power", "EventID=42", back, withMessage: true));
        // Kernel-Boot 27, logged as Windows starts, gives the boot type: 1
        // after a Fast Startup shutdown, 2 on a resume from hibernation. It
        // decides when there's no Kernel-Power 42. Not sign-outs: a user may
        // sign out before a real hibernate too.
        var boots = Times(ReadSystemEvents("Microsoft-Windows-Kernel-Boot", "EventID=27", from, withMessage: true));
        var requests = ReadShutdownRequests(back) ?? [];
        var sessions = ReadSignIns(back) ?? [];
        var periods = new List<SleepPeriod>();
        foreach (var (_, data) in events)
        {
            if (!data.TryGetValue("SleepTime", out var sleep) || !data.TryGetValue("WakeTime", out var wake)
                || !DateTime.TryParse(sleep, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var start)
                || !DateTime.TryParse(wake, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var end)
                || end <= start)
                continue;
            // EffectiveState 5 is hibernation (S4); lower states are sleep. Its
            // Kernel-Power 42 is the one logged nearest the sleep time, a few
            // seconds after it, and its boot the one nearest the wake. A
            // restart asked for just before makes a shutdown a restart.
            var entry = Nearest(entries, start, TimeSpan.FromMinutes(2));
            var boot = Nearest(boots, end, TimeSpan.FromMinutes(1));
            var shutdown = entry != null && entry.TryGetValue("TargetState", out var target)
                ? target == "6"
                : boot != null && boot.TryGetValue("BootType", out var type) && type is "1" or "0x1";
            var request = shutdown ? requests.LastOrDefault(r => r.Time <= start && start - r.Time < TimeSpan.FromMinutes(2)) : null;
            data.TryGetValue("EffectiveState", out var state);
            var kind = state != "5" ? SleepKind.Sleep
                : !shutdown ? SleepKind.Hibernate
                : request?.Restart == true ? SleepKind.FastStartupRestart
                : SleepKind.FastStartupShutdown;
            periods.Add(new SleepPeriod(Local(start), Local(end), kind,
                kind is SleepKind.Hibernate or SleepKind.Sleep && entry != null ? SleepCause(entry) : null)
            {
                DownMessage = Flatten(entry?.GetValueOrDefault(MessageKey)),
                UpMessage = Flatten(boot?.GetValueOrDefault(MessageKey)),
                TargetState = entry?.GetValueOrDefault("TargetState"),
                EffectiveState = state,
                BootType = boot?.GetValueOrDefault("BootType"),
                RequestedBy = request?.By,
                SignedOutBefore = SignedOutBefore(sessions, request?.Time ?? start),
            });
        }
        return periods.OrderBy(p => p.Start).ToList();
    }

    // Kernel-Power 42's reason, in Windows' own words from its message
    // ("Sleep Reason: Button or Lid"), so other languages read right too.
    // The common ones are reworded for the card.
    private static string? SleepCause(Dictionary<string, string> entry)
    {
        if (entry.TryGetValue("Reason", out var reason))
        {
            if (reason == "0") return "power button or lid";
            if (reason == "4") return "Start menu or an app";
        }
        var line = entry.GetValueOrDefault(MessageKey)?.Split('\n')
            .Select(l => l.Trim()).LastOrDefault(l => l.Length > 0);
        var colon = line?.IndexOf(':') ?? -1;
        return colon > 0 && colon < line!.Length - 1 ? line[(colon + 1)..].Trim() : null;
    }

    // Windows' full starts since a UTC time, oldest first, each as the time
    // the PC was off before it: after the shutdown or restart asked for
    // (User32 1074) since the boot before, or, with none, from the last
    // event Windows logged until it started again. Kernel-Power 41 marks a
    // stop without a clean shutdown. Null when the log can't be read.
    private static List<SleepPeriod>? ReadFullStarts(DateTime since)
    {
        var back = since.AddDays(-31);
        var boots = ReadSystemEvents("Microsoft-Windows-Kernel-Boot", "EventID=27", back, withMessage: true);
        if (boots == null) return null;
        var ordered = Times(boots).OrderBy(b => b.Time).ToList();
        var requests = ReadShutdownRequests(back) ?? [];
        var unclean = Times(ReadSystemEvents("Microsoft-Windows-Kernel-Power", "EventID=41", since, withMessage: true));
        var starts = new List<SleepPeriod>();
        for (var i = 0; i < ordered.Count; i++)
        {
            var (boot, data) = ordered[i];
            if (boot < since || !(data.TryGetValue("BootType", out var type) && type is "0" or "0x0")) continue;
            var previous = i > 0 ? ordered[i - 1].Time : back;
            var request = requests.LastOrDefault(r => r.Time > previous && r.Time < boot);
            var crash = Nearest(unclean, boot, TimeSpan.FromMinutes(5));
            var kind = request != null ? (request.Restart ? SleepKind.Restart : SleepKind.Shutdown)
                : crash != null ? SleepKind.Unexpected
                : SleepKind.Shutdown;
            var off = request?.Time ?? LastEventBefore(boot.AddSeconds(-5)) ?? previous;
            starts.Add(new SleepPeriod(Local(off), Local(boot), kind)
            {
                DownMessage = Flatten(request?.Message ?? crash?.GetValueOrDefault(MessageKey)),
                UpMessage = Flatten(data.GetValueOrDefault(MessageKey)),
                BootType = type,
                RequestedBy = request?.By,
            });
        }
        return starts;
    }

    // How the running Windows was started: after a full shutdown, a
    // restart, or without shutting down cleanly (a crash, power loss or a
    // held power button).
    public enum StartKind { Shutdown, Restart, Unexpected }

    // From the full start that began this Windows session. Null when unknown.
    public static StartKind? ReadLastStart() =>
        ReadFullStarts(DateTime.UtcNow - Uptime() - TimeSpan.FromMinutes(2))?.LastOrDefault()?.Kind switch
        {
            SleepKind.Restart => StartKind.Restart,
            SleepKind.Shutdown => StartKind.Shutdown,
            SleepKind.Unexpected => StartKind.Unexpected,
            _ => null,
        };

    // A shutdown or restart asked for (User32 1074): when, in UTC, who asked
    // ("Start", "sign-in screen", "Windows Update" or an app's file name) and
    // Windows' message for it.
    private sealed record ShutdownRequest(DateTime Time, bool Restart, string? By, string? Message);

    // Shutdowns and restarts asked for from Start, the sign-in screen, an
    // update or shutdown.exe since a UTC time, oldest first.
    private static List<ShutdownRequest>? ReadShutdownRequests(DateTime since) =>
        ReadSystemEvents("User32", "EventID=1074", since, withMessage: true)?
            .Select(e => new ShutdownRequest(e.Record.Time.ToUniversalTime(),
                e.Data.TryGetValue("param5", out var action) && action.Contains("restart", StringComparison.OrdinalIgnoreCase),
                RequestedBy(e.Data.GetValueOrDefault("param1")), e.Data.GetValueOrDefault(MessageKey)))
            .OrderBy(r => r.Time).ToList();

    // "C:\Windows\system32\winlogon.exe (PC)" → "sign-in screen".
    private static string? RequestedBy(string? process)
    {
        if (string.IsNullOrEmpty(process)) return null;
        var paren = process.LastIndexOf(" (", StringComparison.Ordinal);
        var file = Path.GetFileName(paren > 0 ? process[..paren] : process);
        return file.ToLowerInvariant() switch
        {
            "startmenuexperiencehost.exe" => "Start",
            "winlogon.exe" => "sign-in screen",
            "trustedinstaller.exe" or "monotificationux.exe" or "musnotification.exe" => "Windows Update",
            _ => file,
        };
    }

    // Fast Startup is on when its setting is (HiberbootEnabled) and
    // hibernation, which it needs, isn't turned off. Null when unreadable.
    public static bool? ReadFastStartup()
    {
        try
        {
            using var session = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Power");
            using var power = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Power");
            if (session?.GetValue("HiberbootEnabled") is not int hiberboot) return null;
            return hiberboot == 1 && power?.GetValue("HibernateEnabled") is not 0;
        }
        catch
        {
            return null;
        }
    }

    public record PowerHistory(List<SleepPeriod> Periods, List<SignInSession> SignIns, List<ChargerChange> Charger);

    // Everything since a local time (midnight yesterday, for Windows
    // status): each sleep, hibernation, shutdown and restart, oldest first,
    // across restarts, and this account's sign-ins and charger changes,
    // including ones from the week before (a sign-in or unplugging that
    // reaches into it). Null when the log can't be read.
    public static PowerHistory? ReadPowerHistory(DateTime since)
    {
        var utc = since.ToUniversalTime();
        var periods = ReadSleepPeriods(utc);
        var signIns = ReadSignIns(utc.AddDays(-7));
        if (periods == null || signIns == null) return null;
        return new PowerHistory([.. periods.Concat(ReadFullStarts(utc) ?? []).OrderBy(p => p.Start)], signIns,
            ReadChargerChanges(utc.AddDays(-7)) ?? []);
    }

    // The charger plugged in or out (Kernel-Power 105), with the battery's
    // charge then (in mWh) and Windows' message. Windows logs it while it's
    // running only, so a change while the PC is off shows on the next start.
    public record ChargerChange(DateTime Time, bool PluggedIn, int? Charge, int? FullCharge, string? Message);

    // Charger changes since a UTC time, oldest first. Null when the log
    // can't be read.
    private static List<ChargerChange>? ReadChargerChanges(DateTime since) =>
        ReadSystemEvents("Microsoft-Windows-Kernel-Power", "EventID=105", since, withMessage: true)?
            .Where(e => e.Data.ContainsKey("AcOnline"))
            .Select(e => new ChargerChange(e.Record.Time, e.Data["AcOnline"] == "true",
                int.TryParse(e.Data.GetValueOrDefault("RemainingCapacity"), out var charge) ? charge : null,
                int.TryParse(e.Data.GetValueOrDefault("FullChargeCapacity"), out var full) && full > 0 ? full : null,
                Flatten(e.Data.GetValueOrDefault(MessageKey))))
            .OrderBy(c => c.Time).ToList();

    // SignedOut is null while that session is still signed in, or when it
    // ended without a sign-out (the PC stopped). The messages are Windows'
    // own words for the two events.
    public record SignInSession(DateTime SignedIn, DateTime? SignedOut)
    {
        public string? SignInMessage { get; init; }
        public string? SignOutMessage { get; init; }
    }

    // This account's sign-ins since Windows started (or a UTC time), oldest
    // first, from Winlogon's sign-in (7001) and sign-out (7002) events,
    // paired by their session number. The account SID only filters out
    // other accounts' sessions; it isn't kept or shown. Null when the log
    // can't be read.
    public static List<SignInSession>? ReadSignIns() => ReadSignIns(null);

    private static List<SignInSession>? ReadSignIns(DateTime? since)
    {
        var events = ReadSystemEvents("Microsoft-Windows-Winlogon", "(EventID=7001 or EventID=7002)", since, withMessage: true);
        if (events == null) return null;
        var me = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value;
        var sessions = new List<SignInSession>();
        var open = new Dictionary<string, int>(); // session number → index in sessions
        foreach (var (record, data) in events.OrderBy(e => e.Record.Time))
        {
            if (me == null || !data.TryGetValue("UserSid", out var sid) || sid != me
                || !data.TryGetValue("TSId", out var session))
                continue;
            var message = Flatten(data.GetValueOrDefault(MessageKey));
            if (record.Id == 7001)
            {
                open[session] = sessions.Count;
                sessions.Add(new SignInSession(record.Time, null) { SignInMessage = message });
            }
            else if (open.Remove(session, out var index))
            {
                sessions[index] = sessions[index] with { SignedOut = record.Time, SignOutMessage = message };
            }
        }
        return sessions;
    }

    // A sign-out in the half hour before a UTC time with no sign-in after
    // it: the user signed out first, then shut down or hibernated.
    private static DateTime? SignedOutBefore(List<SignInSession> sessions, DateTime utc)
    {
        var at = Local(utc);
        var last = sessions.Select(s => s.SignedOut)
            .Where(o => o is { } t && t < at.AddSeconds(-1) && at - t < TimeSpan.FromMinutes(30)).Max();
        return last is { } signedOut && !sessions.Any(s => s.SignedIn > signedOut && s.SignedIn < at) ? signedOut : null;
    }

    // The time of the last event Windows logged before a UTC time: about
    // when a PC that stopped without shutting down went off.
    private static DateTime? LastEventBefore(DateTime utc)
    {
        try
        {
            var query = new System.Diagnostics.Eventing.Reader.EventLogQuery("System",
                System.Diagnostics.Eventing.Reader.PathType.LogName,
                $"*[System[TimeCreated[@SystemTime<='{utc:yyyy-MM-ddTHH:mm:ss}Z']]]") { ReverseDirection = true };
            using var reader = new System.Diagnostics.Eventing.Reader.EventLogReader(query);
            using var record = reader.ReadEvent();
            return record?.TimeCreated?.ToUniversalTime();
        }
        catch
        {
            return null;
        }
    }

    private static List<(DateTime Time, Dictionary<string, string> Data)> Times(
        List<((int Id, DateTime Time) Record, Dictionary<string, string> Data)>? events) =>
        events?.Select(e => (e.Record.Time.ToUniversalTime(), e.Data)).ToList() ?? [];

    // The event logged nearest a UTC time, within a margin.
    private static Dictionary<string, string>? Nearest(
        List<(DateTime Time, Dictionary<string, string> Data)> events, DateTime utc, TimeSpan within) =>
        events.Where(e => (e.Time - utc).Duration() < within)
            .OrderBy(e => (e.Time - utc).Duration()).Select(e => e.Data).FirstOrDefault();

    // A message on one line: "The system is entering sleep. Sleep Reason: Button or Lid".
    private static string? Flatten(string? message) =>
        message == null ? null : string.Join(" ", message.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));

    private static DateTime Local(DateTime utc) => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime();

    // Where ReadSystemEvents puts an event's rendered message when asked;
    // not a name Windows uses for event data.
    private const string MessageKey = "@Message";

    // Events of one System-log provider since Windows started (or since a
    // given UTC time), with their named data values and, if asked, the
    // message. Null when the log can't be read.
    private static List<((int Id, DateTime Time) Record, Dictionary<string, string> Data)>? ReadSystemEvents(
        string provider, string idFilter, DateTime? since = null, bool withMessage = false)
    {
        try
        {
            var started = since ?? DateTime.UtcNow - Uptime();
            var query = new System.Diagnostics.Eventing.Reader.EventLogQuery("System",
                System.Diagnostics.Eventing.Reader.PathType.LogName,
                $"*[System[Provider[@Name='{provider}'] and {idFilter} and " +
                $"TimeCreated[@SystemTime>='{started:yyyy-MM-ddTHH:mm:ss}Z']]]");
            using var reader = new System.Diagnostics.Eventing.Reader.EventLogReader(query);
            var events = new List<((int, DateTime), Dictionary<string, string>)>();
            for (var record = reader.ReadEvent(); record != null; record = reader.ReadEvent())
            {
                using (record)
                {
                    if (record.TimeCreated is not { } time) continue;
                    var data = System.Xml.Linq.XElement.Parse(record.ToXml()).Descendants()
                        .Where(e => e.Name.LocalName == "Data" && e.Attribute("Name") != null)
                        .GroupBy(e => e.Attribute("Name")!.Value)
                        .ToDictionary(g => g.Key, g => g.First().Value);
                    if (withMessage)
                    {
                        try { if (record.FormatDescription() is { } message) data[MessageKey] = message; }
                        catch { /* A missing message resource just leaves the reason out. */ }
                    }
                    events.Add(((record.Id, time), data));
                }
            }
            return events;
        }
        catch
        {
            return null;
        }
    }

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

    public record Live(TimeSpan Uptime, TimeSpan? Asleep, double? CpuPercent, WindowsStatusNative.Memory? Memory,
        WindowsStatusNative.Performance? Performance);

    public static Live ReadLive(Sampler sampler)
    {
        var uptime = Uptime();
        return new(uptime, Asleep(uptime), sampler.CpuPercent(), WindowsStatusNative.ReadMemory(), WindowsStatusNative.ReadPerformance());
    }

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
