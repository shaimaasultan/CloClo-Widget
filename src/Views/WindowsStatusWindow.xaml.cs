using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace CloCloWidget;

// A dashboard of the PC's current state, read locally (see WindowsStatus).
// Every source is read on a background thread, separately, so one slow
// source (usually WMI) never holds up the rest or the window. Timers run
// only while the window is open:
//   every 2 s  - CPU and memory, plus the open tab's live values
//   every 15 s - drives, network, power and displays
//   on open and Refresh - Windows version, processor, graphics, security
public partial class WindowsStatusWindow : Window
{
    private const int TopProcessCount = 8;

    private readonly WindowsStatus.Sampler _sampler = new();
    private readonly DispatcherTimer _liveTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _slowTimer = new() { Interval = TimeSpan.FromSeconds(15) };
    private readonly HashSet<string> _reading = new();
    private bool _closed;
    private int _liveTicks;

    // The latest of each reading, kept for the Overview tab, which draws
    // from several of them.
    private WindowsStatus.OsInfo? _os;
    private WindowsStatus.Processor? _processor;
    private WindowsStatus.Live? _live;
    private List<WindowsStatus.ProcessMemory>? _processes;
    private List<WindowsStatus.DriveSpace>? _drives;
    private WindowsStatus.NetworkState? _network;
    private WindowsStatus.PowerState? _power;
    private WindowsStatus.UpdateState? _updates;
    private List<WindowsStatus.SleepPeriod>? _sleeps;
    private List<WindowsStatus.SignInSession>? _signIns;

    public WindowsStatusWindow()
    {
        InitializeComponent();
        foreach (var host in Hosts) Show(host, [Card.Message("Reading…")]);
        _liveTimer.Tick += (_, _) => LiveTick();
        _slowTimer.Tick += (_, _) => SlowTick();
        Loaded += async (_, _) =>
        {
            await RefreshAllAsync();
            if (_closed) return;
            _liveTimer.Start();
            _slowTimer.Start();
        };
        Closed += (_, _) =>
        {
            _closed = true;
            _liveTimer.Stop();
            _slowTimer.Stop();
            foreach (var host in Hosts) host.Children.Clear();
        };
    }

    private StackPanel[] Hosts =>
        [OverviewCards, CpuCards, StorageCards, NetworkCards, PowerCards, DisplayCards, GraphicsCards, SecurityCards];

    // ---- Scheduling ----

    private Task RefreshAllAsync() => Task.WhenAll(
        Read("os", WindowsStatus.ReadOs, os => { _os = os; ShowOverview(); }),
        ReadSleeps(),
        Read("processor", WindowsStatus.ReadProcessor, p => { _processor = p; ShowCpu(); }),
        ReadLive(),
        Read("processes", () => WindowsStatus.ReadTopProcesses(TopProcessCount), p => { _processes = p; ShowCpu(); }),
        Read("drives", WindowsStatus.ReadDrives, d => { _drives = d; ShowStorage(); ShowOverview(); }),
        ReadNetwork(),
        ReadPower(),
        Read("displays", WindowsStatus.ReadDisplays, ShowDisplays),
        Read("graphics", WindowsStatus.ReadGraphics, ShowGraphics),
        Read("security", () => (WindowsStatus.ReadSecurity(), WindowsStatus.ReadUpdates()), s =>
        {
            _updates = s.Item2;
            ShowSecurity(s.Item1, s.Item2);
            ShowOverview();
        }));

    private void LiveTick()
    {
        if (_closed || !IsVisible || WindowState == WindowState.Minimized) return;
        _liveTicks++;
        _ = ReadLive();
        if (Tabs.SelectedItem == CpuTab && _liveTicks % 2 == 0)
            _ = Read("processes", () => WindowsStatus.ReadTopProcesses(TopProcessCount), p => { _processes = p; ShowCpu(); });
        if (Tabs.SelectedItem == NetworkTab) _ = ReadNetwork();
        if (Tabs.SelectedItem == PowerTab || Tabs.SelectedItem == OverviewTab) _ = ReadPower();
    }

    private void SlowTick()
    {
        if (_closed || !IsVisible || WindowState == WindowState.Minimized) return;
        _ = Read("drives", WindowsStatus.ReadDrives, d => { _drives = d; ShowStorage(); ShowOverview(); });
        _ = ReadNetwork();
        _ = ReadPower();
        _ = Read("displays", WindowsStatus.ReadDisplays, ShowDisplays);
        _ = ReadSleeps();
    }

    // The PC can sleep while this window is open, so these are read again
    // on the slow timer; each is one filtered event-log query.
    private Task ReadSleeps() => Read("sleeps", () => (WindowsStatus.ReadSleepPeriods(), WindowsStatus.ReadSignIns()), read =>
    {
        (_sleeps, _signIns) = read;
        ShowOverview();
    });

    private Task ReadLive() => Read("live", () => WindowsStatus.ReadLive(_sampler), live =>
    {
        _live = live;
        ShowCpu();
        ShowOverview();
    });

    private Task ReadNetwork() => Read("network", () => WindowsStatus.ReadNetwork(_sampler), network =>
    {
        _network = network;
        ShowNetwork();
        ShowOverview();
    });

    private Task ReadPower() => Read("power", WindowsStatus.ReadPower, power =>
    {
        _power = power;
        ShowPower();
        ShowOverview();
    });

    // Reads one source in the background and shows it. A source still being
    // read is not started again, so a slow one never piles up.
    private async Task Read<T>(string name, Func<T> read, Action<T> show)
    {
        if (_closed || !_reading.Add(name)) return;
        try
        {
            var result = await Task.Run(read);
            if (!_closed) show(result);
        }
        catch
        {
            // Readers catch their own errors; this only guards the display.
        }
        finally
        {
            _reading.Remove(name);
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        RefreshButton.IsEnabled = false;
        try { await RefreshAllAsync(); }
        finally { if (!_closed) RefreshButton.IsEnabled = true; }
    }

    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, Tabs) || !IsLoaded) return;
        // Bring the newly opened tab's live values up to date at once.
        if (Tabs.SelectedItem == CpuTab) _ = Read("processes", () => WindowsStatus.ReadTopProcesses(TopProcessCount), p => { _processes = p; ShowCpu(); });
        else if (Tabs.SelectedItem == NetworkTab) _ = ReadNetwork();
        else if (Tabs.SelectedItem == PowerTab) _ = ReadPower();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    // ---- Tabs ----

    private void ShowOverview()
    {
        var glance = new List<(string, string)>();
        if (_live != null)
        {
            glance.Add(("Processor", _live.CpuPercent is { } cpu ? $"{cpu:0}% in use" : "Measuring…"));
            if (_live.Memory is { } memory)
                glance.Add(("Memory", $"{memory.LoadPercent}% in use · {Size(memory.AvailableBytes)} available"));
        }
        var system = _drives?.FirstOrDefault(d => d.IsSystem);
        if (system != null) glance.Add(("System drive", $"{Size(system.FreeBytes)} free of {Size(system.TotalBytes)}"));
        if (_power != null) glance.Add(("Power", PowerSummary(_power)));
        if (_network != null) glance.Add(("Network", NetworkSummary(_network)));
        if (_updates?.RestartPending == true) glance.Add(("Updates", "A restart is needed to finish installing updates"));

        var pc = new List<(string, string)>();
        if (_os != null)
        {
            pc.Add(("Windows", _os.Edition ?? "Unknown"));
            if (_os.Version != null) pc.Add(("Version", _os.Version));
            if (_os.Build != null) pc.Add(("OS build", _os.Build));
            pc.Add(("System type", _os.Architecture));
            pc.Add(("PC name", _os.PcName));
        }
        var uptime = _live?.Uptime ?? _os?.Uptime;
        if (uptime != null)
        {
            pc.Add(("Up for", Duration(uptime.Value)));
            pc.Add(("Started", When(DateTime.Now - uptime.Value)));
        }
        if (_os?.SignedIn is { } signedIn) pc.Add(("Signed in", WithDate(signedIn)));
        // The most recent sign-out since Windows started, and how long it
        // stayed signed out until the next sign-in (or until now).
        if (_signIns?.LastOrDefault(s => s.SignedOut != null)?.SignedOut is { } signedOut)
        {
            pc.Add(("Last signed out", WithDate(signedOut)));
            var next = _signIns.FirstOrDefault(s => s.SignedIn >= signedOut)?.SignedIn;
            pc.Add(("Signed out for", next is { } back
                ? $"{Duration(back - signedOut)} ({When(signedOut)} → {When(back)})"
                : $"{Duration(DateTime.Now - signedOut)} (since {When(signedOut)})"));
        }
        // Without the wake events there's no per-day split, only totals.
        if (_sleeps == null && _live?.Asleep is { } asleep && uptime != null)
        {
            pc.Add(("Asleep for", Duration(asleep)));
            pc.Add(("Awake for", Duration(uptime.Value - asleep)));
        }
        if (_os?.Installed is { } installed) pc.Add(("Windows installed", installed.ToString("d")));

        var cards = new List<Card>();
        if (glance.Count > 0) cards.Add(new Card("At a glance", glance));
        if (pc.Count > 0) cards.Add(new Card("This PC", pc));
        if (_sleeps != null && uptime != null) cards.AddRange(DayCards(DateTime.Now - uptime.Value, DateTime.Now, _sleeps, _signIns));
        Show(OverviewCards, cards.Count > 0 ? cards : [Card.Message("Reading…")]);
    }

    private void ShowCpu()
    {
        var cards = new List<Card>();
        var processorRows = new List<(string, string)>();
        if (_processor?.Name != null) processorRows.Add(("Model", _processor.Name));
        if (_processor != null)
            processorRows.Add(("Cores", _processor.Cores is { } cores
                ? $"{cores} cores, {_processor.LogicalProcessors} logical processors"
                : $"{_processor.LogicalProcessors} logical processors"));
        if (_live?.Performance is { } perf)
            processorRows.Add(("Running", $"{perf.Processes:N0} processes · {perf.Threads:N0} threads · {perf.Handles:N0} handles"));
        var cpu = _live?.CpuPercent;
        cards.Add(new Card("Processor", processorRows, cpu, cpu is { } c ? $"{c:0}% in use" : "Measuring…"));

        if (_live?.Memory is { } memory)
        {
            var used = memory.TotalBytes - memory.AvailableBytes;
            var rows = new List<(string, string)>
            {
                ("In use", Size(used)),
                ("Available", Size(memory.AvailableBytes)),
                ("Usable", memory.InstalledBytes is { } installed
                    ? $"{Size(memory.TotalBytes)} of {Size(installed)} installed"
                    : Size(memory.TotalBytes)),
            };
            if (_processor?.MemoryModules is { } modules)
                rows.Add(("Modules", _processor.MemorySpeedMts is { } speed ? $"{modules} · {speed:N0} MT/s" : $"{modules}"));
            if (_live.Performance is { } p)
            {
                rows.Add(("Committed", $"{Size(p.CommitBytes)} of {Size(p.CommitLimitBytes)}"));
                rows.Add(("Cached", Size(p.CacheBytes)));
            }
            cards.Add(new Card("Memory", rows, memory.LoadPercent, $"{memory.LoadPercent}% in use"));
        }

        if (_processes != null)
        {
            cards.Add(_processes.Count == 0
                ? new Card("Most memory", [("", "Unavailable")])
                : new Card("Most memory", _processes.Select(p =>
                    (p.Count > 1 ? $"{p.Name} (×{p.Count})" : p.Name, Size((ulong)p.Bytes))).ToList()));
        }
        Show(CpuCards, cards);
    }

    private void ShowStorage()
    {
        if (_drives == null) return;
        if (_drives.Count == 0)
        {
            Show(StorageCards, [Card.Message("No fixed drive information available")]);
            return;
        }
        Show(StorageCards, _drives.Select(d =>
        {
            var used = d.TotalBytes - d.FreeBytes;
            var percent = 100.0 * used / d.TotalBytes;
            var title = d.Root.TrimEnd('\\') + (d.Label != null ? $"  {d.Label}" : "") + (d.IsSystem ? "  (Windows)" : "");
            var rows = new List<(string, string)>
            {
                ("Free", Size((ulong)d.FreeBytes)),
                ("Used", Size((ulong)used)),
                ("Capacity", Size((ulong)d.TotalBytes)),
            };
            if (d.Format != null) rows.Add(("File system", d.Format));
            return new Card(title, rows, percent, $"{percent:0}% used");
        }).ToList());
    }

    private void ShowNetwork()
    {
        if (_network == null) return;
        var n = _network;
        var status = new List<(string, string)>
        {
            ("Windows reports", n.Connectivity ?? (n.Available switch
            {
                true => "A network connection is available",
                false => "No network connection",
                _ => "Unknown",
            })),
        };
        if (n.Metered is { } metered) status.Add(("Metered", metered ? "Yes (data use may be limited)" : "No"));
        status.Add(("Adapters", $"{n.Adapters.Count} connected" + (n.DisconnectedCount > 0 ? $", {n.DisconnectedCount} not connected" : "")));
        var cards = new List<Card> { new("Connection", status) };

        foreach (var a in n.Adapters)
        {
            var rows = new List<(string, string)>
            {
                ("Type", a.Kind),
                ("Hardware", a.Description),
            };
            if (a.SpeedBitsPerSecond > 0) rows.Add(("Link speed", Rate(a.SpeedBitsPerSecond)));
            rows.Add(("Activity", a.Rate is { } r ? $"↓ {Rate(r.Down)}   ↑ {Rate(r.Up)}" : "Measuring…"));
            rows.Add(("Local IPv4", a.IPv4.Count > 0 ? string.Join(", ", a.IPv4) : "None"));
            if (a.IPv6Count > 0) rows.Add(("IPv6", $"{a.IPv6Count} address{(a.IPv6Count == 1 ? "" : "es")} (not shown)"));
            if (a.Dhcp is { } dhcp) rows.Add(("Address set by", dhcp ? "The network (DHCP)" : "Manual setting"));
            rows.Add(("Gateway", a.HasGateway ? "Yes" : "None (local link only)"));
            cards.Add(new Card(a.Name, rows));
        }
        Show(NetworkCards, cards);
    }

    private void ShowPower()
    {
        if (_power == null) return;
        var p = _power;
        var rows = new List<(string, string)>
        {
            ("Power source", p.OnAc switch { true => "Plugged in", false => "Battery", _ => "Unknown" }),
        };
        if (p.Battery is { } b)
        {
            rows.Add(("Battery", $"{b.Percent:0}%" + (b.Charging ? ", charging" : p.OnAc == true ? ", not charging" : "")));
            if (b.TimeRemaining != null) rows.Add(("Time remaining", b.TimeRemaining));
            rows.Add(("Battery saver", p.BatterySaver ? "On" : "Off"));
        }
        else rows.Add(("Battery", "No battery"));
        rows.Add(("Power plan", p.Plan ?? "Unknown"));
        if (p.Mode != null) rows.Add(("Power mode", p.Mode));
        Show(PowerCards, [new Card("Power", rows, p.Battery?.Percent, p.Battery is { } battery ? $"{battery.Percent:0}% charged" : null)]);
    }

    private void ShowDisplays(List<WindowsStatus.Display> displays)
    {
        if (displays.Count == 0)
        {
            Show(DisplayCards, [Card.Message("No display information available")]);
            return;
        }
        Show(DisplayCards, displays.Select((d, i) =>
        {
            var rows = new List<(string, string)> { ("Resolution", $"{d.Width:N0} × {d.Height:N0}") };
            if (d.RefreshHz is { } hz) rows.Add(("Refresh rate", $"{hz:0.##} Hz"));
            if (d.ScalePercent is { } scale) rows.Add(("Scaling", $"{scale}%"));
            rows.Add(("Orientation", d.Portrait ? "Portrait" : "Landscape"));
            if (d.Connection != null) rows.Add(("Connection", d.Connection));
            if (d.HdrSupported is { } hdr) rows.Add(("HDR", !hdr ? "Not supported" : d.HdrOn == true ? "On" : "Off"));
            if (d.Gpu != null) rows.Add(("Graphics", d.Gpu));
            return new Card($"{i + 1}. {d.Name}" + (d.Primary ? "  (main display)" : ""), rows);
        }).ToList());
    }

    private void ShowGraphics(List<WindowsStatus.Gpu> gpus)
    {
        if (gpus.Count == 0)
        {
            Show(GraphicsCards, [Card.Message("No graphics adapter information available")]);
            return;
        }
        Show(GraphicsCards, gpus.Select(g =>
        {
            var rows = new List<(string, string)>();
            if (g.DedicatedBytes is { } dedicated)
                rows.Add(("Video memory", $"{Size(dedicated)} dedicated" + (g.SharedBytes is { } shared ? $", {Size(shared)} shared" : "")));
            if (g.DriverVersion != null) rows.Add(("Driver version", g.DriverVersion));
            if (g.DriverDate is { } date) rows.Add(("Driver date", date.ToString("d")));
            rows.Add(("Displays", g.DisplaysConnected switch { 0 => "None connected", 1 => "1 connected", var n => $"{n} connected" }));
            if (g.Status != null && !string.Equals(g.Status, "OK", StringComparison.OrdinalIgnoreCase)) rows.Add(("Status", g.Status));
            return new Card(g.Name, rows);
        }).ToList());
    }

    private void ShowSecurity(WindowsStatus.SecurityState s, WindowsStatus.UpdateState u)
    {
        var protection = new List<(string, string)>();
        if (s.Antivirus.Count == 0) protection.Add(("Antivirus", "Not reported"));
        foreach (var av in s.Antivirus)
            protection.Add(("Antivirus", av.Name + av.On switch { true => " · on", false => " · off", _ => "" }
                + av.UpToDate switch { true => ", up to date", false => ", out of date", _ => "" }));
        if (s.DefenderRealTime is { } realTime) protection.Add(("Real-time protection", OnOff(realTime)));
        if (s.DefenderSignatures is { } sig) protection.Add(("Definitions updated", sig.ToString("g")));
        if (s.DefenderQuickScan is { } scan) protection.Add(("Last quick scan", scan.ToString("g")));
        if (s.TamperProtection is { } tamper) protection.Add(("Tamper protection", OnOff(tamper)));
        var firewallOff = s.Firewall.Where(f => f.On == false).Select(f => f.Profile).ToList();
        protection.Add(("Windows Firewall", s.Firewall.All(f => f.On == null) ? "Unknown"
            : firewallOff.Count == 0 ? "On for all networks"
            : $"Off for: {string.Join(", ", firewallOff)}"));
        foreach (var other in s.OtherFirewalls) protection.Add(("Other firewall", other));

        var device = new List<(string, string)>();
        if (s.SecureBoot is { } secureBoot) device.Add(("Secure Boot", OnOff(secureBoot)));
        if (s.MemoryIntegrity is { } hvci) device.Add(("Memory integrity", OnOff(hvci)));
        if (s.Uac is { } uac) device.Add(("User Account Control", OnOff(uac)));
        if (s.SmartAppControl != null) device.Add(("Smart App Control", s.SmartAppControl));

        var updates = new List<(string, string)>
        {
            ("Latest installed", u.LatestInstalled is { } date
                ? $"{date:d}" + (u.LatestId != null ? $" ({u.LatestId})" : "")
                : "Not reported"),
            ("Restart needed", u.RestartPending ? "Yes, to finish installing updates" : "No"),
        };
        if (_os?.Build != null) updates.Add(("OS build", _os.Build));

        var cards = new List<Card> { new("Protection", protection) };
        if (device.Count > 0) cards.Add(new Card("Device security", device));
        cards.Add(new Card("Windows Update", updates));
        Show(SecurityCards, cards);
    }

    // ---- Formatting ----

    private static string PowerSummary(WindowsStatus.PowerState p)
    {
        var source = p.OnAc switch { true => "Plugged in", false => "On battery", _ => "Power source unknown" };
        if (p.Battery is not { } b) return source;
        return $"{source} · {b.Percent:0}%" + (b.Charging ? " charging" : "") + (b.TimeRemaining != null ? $" · {b.TimeRemaining} left" : "");
    }

    private static string NetworkSummary(WindowsStatus.NetworkState n)
    {
        var main = n.Adapters.FirstOrDefault(a => a.HasGateway) ?? n.Adapters.FirstOrDefault();
        var state = n.Connectivity ?? (n.Available == true ? "Connected" : n.Available == false ? "No connection" : "Unknown");
        return main == null ? state : $"{state} · {main.Kind}";
    }

    private static string OnOff(bool on) => on ? "On" : "Off";

    private static string Size(ulong bytes) => bytes switch
    {
        >= 1UL << 40 => $"{bytes / (double)(1UL << 40):0.##} TB",
        >= 1UL << 30 => $"{bytes / (double)(1UL << 30):0.#} GB",
        >= 1UL << 20 => $"{bytes / (double)(1UL << 20):0} MB",
        _ => $"{bytes / 1024.0:0} KB",
    };

    private static string Size(long bytes) => Size((ulong)Math.Max(0, bytes));

    private static string Rate(double bitsPerSecond) => bitsPerSecond switch
    {
        >= 1e9 => $"{bitsPerSecond / 1e9:0.#} Gbps",
        >= 1e6 => $"{bitsPerSecond / 1e6:0.#} Mbps",
        >= 1e3 => $"{bitsPerSecond / 1e3:0} Kbps",
        _ => $"{bitsPerSecond:0} bps",
    };

    private const int MaxDayCards = 7;

    // One card per calendar day since Windows started, newest first: the
    // part of the day it was on, split into awake and asleep. Sleeps that
    // cross midnight count toward each day for the hours in it.
    private static IEnumerable<Card> DayCards(DateTime started, DateTime now,
        List<WindowsStatus.SleepPeriod> sleeps, List<WindowsStatus.SignInSession>? signIns)
    {
        for (var day = now.Date; day >= started.Date && day > now.Date.AddDays(-MaxDayCards); day = day.AddDays(-1))
        {
            var from = day > started ? day : started;
            var to = day.AddDays(1) < now ? day.AddDays(1) : now;
            var asleep = AsleepWithin(sleeps, from, to);
            var rows = new List<(string, string)>
            {
                ("Counted", Span(from, to, now)),
                ("Awake for", Duration(to - from - asleep)),
                ("Asleep for", asleep > TimeSpan.Zero ? Duration(asleep) : "Not asleep"),
            };

            // The day as a timeline: awake stretches between its sleeps.
            var cursor = from;
            foreach (var p in sleeps.Where(p => p.End > from && p.Start < to))
            {
                var sleepFrom = p.Start > from ? p.Start : from;
                if (sleepFrom > cursor) rows.Add(("Awake", $"{Span(cursor, sleepFrom, now)} · {Duration(sleepFrom - cursor)}"));
                rows.Add((p.Hibernated ? "Hibernated" : "Asleep", $"{Period(p)} · {Duration(p.End - p.Start)}"));
                cursor = p.End < to ? p.End : to;
            }
            if (to > cursor) rows.Add(("Awake", $"{Span(cursor, to, now)} · {Duration(to - cursor)}"));

            // From the last sign-in that reaches into this day, until its
            // sign-out, midnight or now; counted within the day only.
            if (signIns?.LastOrDefault(s => s.SignedIn < to && (s.SignedOut ?? now) > from) is { } session)
            {
                var since = session.SignedIn > from ? session.SignedIn : from;
                var until = session.SignedOut is { } signedOut && signedOut < to ? signedOut : to;
                var end = until == now ? $"{until:t} (now)"
                    : until == session.SignedOut ? $"{until:t} (signed out)"
                    : $"{until:t}";
                var signedIn = session.SignedIn.Date == day ? $"{session.SignedIn:t}" : When(session.SignedIn);
                rows.Add(("Since sign-in", $"{signedIn} → {end} · " +
                    $"{Duration(until - since - AsleepWithin(sleeps, since, until))} awake"));
            }

            var title = day == now.Date ? "Today" : day == now.Date.AddDays(-1) ? "Yesterday" : day.ToString("dddd");
            yield return new Card($"{title}, {day.ToString(MonthDay)}", rows);
        }
    }

    private static TimeSpan AsleepWithin(List<WindowsStatus.SleepPeriod> sleeps, DateTime from, DateTime to) =>
        TimeSpan.FromTicks(sleeps.Where(p => p.End > from && p.Start < to)
            .Sum(p => ((p.End < to ? p.End : to) - (p.Start > from ? p.Start : from)).Ticks));

    // "12:00 AM → 1:03 AM", or "1:44 PM → 2:21 PM (now)".
    private static string Span(DateTime from, DateTime to, DateTime now) =>
        $"{from:t} → {(to == now ? $"{to:t} (now)" : $"{to:t}")}";

    // "1:45 PM · Sat, Oct 17": the time with its date always shown.
    private static string WithDate(DateTime time) => $"{time:t} · {time.ToString("ddd, " + MonthDay)}";

    // Windows' month/day order with a short month name: "Oct 17" or "17 Oct".
    private static string MonthDay =>
        System.Globalization.CultureInfo.CurrentCulture.DateTimeFormat.MonthDayPattern.Replace("MMMM", "MMM");

    // "1:45 PM" today, "10:41 PM yesterday", "Tue 9:12 AM" this week, or
    // the short date and time before that. Uses Windows' own time format.
    private static string When(DateTime time)
    {
        var days = (DateTime.Today - time.Date).Days;
        return days switch
        {
            0 => time.ToString("t"),
            1 => $"{time:t} yesterday",
            > 1 and < 7 => $"{time:ddd} {time:t}",
            _ => time.ToString("g"),
        };
    }

    // "1:03 AM → 1:44 PM", with the day on either end when it isn't today.
    private static string Period(WindowsStatus.SleepPeriod p) => $"{When(p.Start)} → {When(p.End)}";

    private static string Duration(TimeSpan span) =>
        span.TotalDays >= 1 ? $"{(int)span.TotalDays} d {span.Hours} h {span.Minutes} min"
        : span.TotalHours >= 1 ? $"{span.Hours} h {span.Minutes} min"
        : $"{span.Minutes} min";

    // ---- Cards ----

    private sealed record Card(string Title, IReadOnlyList<(string Label, string Value)> Rows,
        double? Percent = null, string? PercentText = null)
    {
        public static Card Message(string text) => new("", [("", text)]);
    }

    // The visuals of one card, kept so a refresh only changes text. Updating
    // in place (instead of rebuilding) keeps a screen reader's position and
    // any selection steady while values tick.
    private sealed class CardView
    {
        public required Border Root { get; init; }
        public required TextBlock Title { get; init; }
        public ProgressBar? Bar { get; init; }
        public TextBlock? BarText { get; init; }
        public required List<(TextBlock Label, TextBlock Value)> Cells { get; init; }

        public bool Fits(Card card) =>
            (Bar != null) == (card.Percent != null) && Cells.Count == card.Rows.Count;

        public void Update(Card card)
        {
            SetText(Title, card.Title);
            Title.Visibility = card.Title.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (Bar != null && card.Percent is { } percent)
            {
                Bar.Value = percent;
                Bar.Foreground = percent >= 90 ? Brushes.IndianRed : SystemColors.HighlightBrush;
                AutomationProperties.SetName(Bar, $"{card.Title}: {card.PercentText}");
                SetText(BarText!, card.PercentText ?? "");
            }
            for (var i = 0; i < Cells.Count; i++)
            {
                SetText(Cells[i].Label, card.Rows[i].Label);
                SetText(Cells[i].Value, card.Rows[i].Value);
            }
        }

        private static void SetText(TextBlock block, string text)
        {
            if (block.Text != text) block.Text = text;
        }

        public static CardView Create(Card card)
        {
            var panel = new StackPanel();
            var title = new TextBlock { FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
            panel.Children.Add(title);

            ProgressBar? bar = null;
            TextBlock? barText = null;
            if (card.Percent != null)
            {
                bar = new ProgressBar { Height = 10, Minimum = 0, Maximum = 100, Margin = new Thickness(0, 0, 0, 2) };
                barText = new TextBlock { FontSize = 11, Margin = new Thickness(0, 0, 0, 6) };
                panel.Children.Add(bar);
                panel.Children.Add(barText);
            }

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var cells = new List<(TextBlock, TextBlock)>();
            for (var i = 0; i < card.Rows.Count; i++)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var label = new TextBlock
                {
                    Foreground = SystemColors.GrayTextBrush,
                    Margin = new Thickness(0, 1, 12, 1),
                };
                var value = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 1) };
                Grid.SetRow(label, i);
                Grid.SetRow(value, i);
                Grid.SetColumn(value, 1);
                grid.Children.Add(label);
                grid.Children.Add(value);
                cells.Add((label, value));
            }
            panel.Children.Add(grid);

            var root = new Border
            {
                BorderBrush = SystemColors.ActiveBorderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 0, 0, 8),
                Child = panel,
            };
            var view = new CardView { Root = root, Title = title, Bar = bar, BarText = barText, Cells = cells };
            root.Tag = view;
            view.Update(card);
            return view;
        }
    }

    private static void Show(StackPanel host, IReadOnlyList<Card> cards)
    {
        while (host.Children.Count > cards.Count) host.Children.RemoveAt(host.Children.Count - 1);
        for (var i = 0; i < cards.Count; i++)
        {
            if (i < host.Children.Count && host.Children[i] is Border { Tag: CardView view } && view.Fits(cards[i]))
            {
                view.Update(cards[i]);
                continue;
            }
            var created = CardView.Create(cards[i]).Root;
            if (i < host.Children.Count)
            {
                host.Children.RemoveAt(i);
                host.Children.Insert(i, created);
            }
            else host.Children.Add(created);
        }
    }
}
