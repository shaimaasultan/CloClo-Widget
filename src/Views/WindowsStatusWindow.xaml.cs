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
    private WindowsStatus.PowerHistory? _history;
    private bool? _fastStartup;
    private WindowsStatus.StartKind? _start;
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
    private Task ReadSleeps() => Read("sleeps", () => (WindowsStatus.ReadPowerHistory(DateTime.Today.AddDays(-1)),
        WindowsStatus.ReadFastStartup(), WindowsStatus.ReadSignIns(), WindowsStatus.ReadLastStart()), read =>
    {
        (_history, _fastStartup, _signIns, _start) = read;
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
            pc.Add(("Started", When(DateTime.Now - uptime.Value) + (_start is { } how ? $" · {StartLabel(how)}" : "")));
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
        if (_history == null && _live?.Asleep is { } asleep && uptime != null)
        {
            pc.Add(("Asleep or shut down for", Duration(asleep)));
            pc.Add(("Awake for", Duration(uptime.Value - asleep)));
        }
        if (_os?.Installed is { } installed) pc.Add(("Windows installed", installed.ToString("d")));

        var cards = new List<Card>();
        if (glance.Count > 0) cards.Add(new Card("At a glance", glance));
        if (pc.Count > 0) cards.Add(new Card("This PC", pc));
        if (_history != null) cards.AddRange(PowerHistoryCards(_history, _fastStartup, DateTime.Now));
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

    // Yesterday's and today's power history as cards: whether Fast Startup
    // is on, then one card per kind of action (sleep, hibernate, shutdown
    // and restart, sign-out, sign-in), each row showing what the user did
    // next to what Windows logged, in its own words, then the day totals.
    private static IEnumerable<Card> PowerHistoryCards(WindowsStatus.PowerHistory history, bool? fastStartup, DateTime now)
    {
        var since = now.Date.AddDays(-1);
        var periods = history.Periods;
        const string none = "None yesterday or today";

        yield return fastStartup is { } on
            ? new Card("Fast Startup", [
                ("What it does", on
                    ? "Shut down saves the Windows core to disk and starts from it, so Windows logs each shutdown as a hibernation (S4). Restart always starts Windows in full."
                    : "Shut down turns Windows off in full, so Windows doesn't log it as a hibernation."),
                ("Change it", "Control Panel → Power Options → Choose what the power buttons do (needs an administrator)"),
            ], Check: (on ? "Fast Startup is on" : "Fast Startup is off", on))
            : new Card("Fast Startup", [("", "Couldn't read the Fast Startup setting")]);

        IReadOnlyList<(string, string)> Rows(IEnumerable<(string, string)> rows, string empty) =>
            rows.ToList() is { Count: > 0 } list ? list : [("", empty)];
        IEnumerable<(string, string)> PeriodRows(params WindowsStatus.SleepKind[] kinds) =>
            periods.Where(p => kinds.Contains(p.Kind)).Select(p => (Period(p),
                $"{KindName(p)} · {Duration(p.End - p.Start)}\nWhat you did: {Action(p)}\nWindows logged: {WindowsWords(p)}"));

        yield return new Card("Sleep", Rows(PeriodRows(WindowsStatus.SleepKind.Sleep), none));
        var hibernations = PeriodRows(WindowsStatus.SleepKind.Hibernate, WindowsStatus.SleepKind.FastStartupShutdown,
            WindowsStatus.SleepKind.FastStartupRestart).ToList();
        if (periods.Any(p => p.Kind is WindowsStatus.SleepKind.FastStartupShutdown or WindowsStatus.SleepKind.FastStartupRestart))
            hibernations.Insert(0, ("", "Windows logs a Fast Startup shutdown as a hibernation, so both are here."));
        yield return new Card("Hibernate", Rows(hibernations, none));
        var fullStops = PeriodRows(WindowsStatus.SleepKind.Shutdown, WindowsStatus.SleepKind.Restart,
            WindowsStatus.SleepKind.Unexpected).ToList();
        if (fullStops.Count > 0) yield return new Card("Shutdown and restart", fullStops);

        // A period starting or ending within half an hour of a sign-out or
        // sign-in is what came next or before.
        var near = TimeSpan.FromMinutes(30);
        var signOuts = new List<(DateTime At, string Row, string Value)>();
        foreach (var s in history.SignIns)
        {
            if (s.SignedOut is { } signedOut && signedOut >= since)
            {
                var next = periods.FirstOrDefault(p => p.Start >= signedOut.AddSeconds(-5) && p.Start - signedOut < near);
                var then = next == null ? null
                    : next.SignedOutBefore == signedOut
                        ? $"Then, at {next.Start:T}: {KindName(next)} — {Lower(Action(next, withSignOut: false))}"
                        : $"Signed out as part of the {Noun(next)} — {Lower(Action(next))}";
                signOuts.Add((signedOut, When(signedOut),
                    "Signed out" + (then != null ? $"\n{then}" : "") +
                    $"\nWindows logged: {Quote(s.SignOutMessage)} (Winlogon 7002)"));
            }
            // A session the PC's stop ended, without a sign-out.
            else if (s.SignedOut == null && periods.FirstOrDefault(p => p.Start > s.SignedIn
                && p.Kind is WindowsStatus.SleepKind.Shutdown or WindowsStatus.SleepKind.Restart or WindowsStatus.SleepKind.Unexpected) is { } stop
                && !history.SignIns.Any(o => o.SignedIn > s.SignedIn && o.SignedIn < stop.Start))
            {
                signOuts.Add((stop.Start, When(stop.Start),
                    $"No sign-out logged: the session ended with the {Noun(stop)}\nWindows logged no Winlogon 7002"));
            }
        }
        yield return new Card("Sign-out", Rows(signOuts.OrderBy(o => o.At).Select(o => (o.Row, o.Value)), none));

        yield return new Card("Sign-in", Rows(history.SignIns.Where(s => s.SignedIn >= since).Select(s =>
        {
            var before = periods.LastOrDefault(p => p.End <= s.SignedIn.AddSeconds(5) && s.SignedIn - p.End < near);
            var after = before == null ? "" : $"\nAfter the {Noun(before)} ({Period(before)})";
            var still = s.SignedOut == null && s == history.SignIns[^1] ? " · still signed in" : "";
            return (When(s.SignedIn), $"Signed in{still}{after}\nWindows logged: {Quote(s.SignInMessage)} (Winlogon 7001)");
        }), none));

        // The charger's changes, with changes less than a minute apart
        // grouped: a burst of them is the connection cutting out, not the
        // user plugging and unplugging.
        var bursts = new List<List<WindowsStatus.ChargerChange>>();
        foreach (var change in history.Charger)
        {
            if (bursts.Count > 0 && change.Time - bursts[^1][^1].Time < TimeSpan.FromMinutes(1)) bursts[^1].Add(change);
            else bursts.Add([change]);
        }
        var charger = new List<(string, string)>();
        // Cut-outs less than half an hour apart make one row listing their
        // times; an unplugging or plugging in on its own gets its own row.
        var stretches = new List<List<List<WindowsStatus.ChargerChange>>>();
        foreach (var burst in bursts.Where(b => b[^1].Time >= since))
        {
            if (burst.Count > 1 && stretches.Count > 0 && stretches[^1][^1].Count > 1
                && burst[0].Time - stretches[^1][^1][^1].Time < TimeSpan.FromMinutes(30))
                stretches[^1].Add(burst);
            else stretches.Add([burst]);
        }
        foreach (var stretch in stretches)
        {
            var (first, last) = (stretch[0][0], stretch[^1][^1]);
            if (stretch is [[var change]])
            {
                // How long it was unplugged, for a plug-in after an unplugging.
                var before = history.Charger.LastOrDefault(c => c.Time < change.Time);
                var battery = change.PluggedIn && before is { PluggedIn: false } ? $" after {Duration(change.Time - before.Time)} on battery" : "";
                charger.Add((When(change.Time), $"{(change.PluggedIn ? "Plugged in" : "Unplugged")}{battery}{Charge(change)}" +
                    $"\nWindows logged: {Quote(change.Message)} (Kernel-Power 105, AcOnline={(change.PluggedIn ? "true" : "false")})"));
                continue;
            }
            var changes = stretch.SelectMany(b => b).ToList();
            var cuts = changes.Count(c => !c.PluggedIn);
            var times = string.Join(", ", stretch.Select(b => b.Count(c => !c.PluggedIn) is var n && n > 1 ? $"{b[0].Time:t} (×{n})" : $"{b[0].Time:t}"));
            charger.Add(($"{When(first.Time)} → {When(last.Time)}",
                $"Charger cut out {cuts} {(cuts == 1 ? "time" : "times")}, each for a few seconds; ended {(last.PluggedIn ? "plugged in" : "unplugged")}{Charge(last)}" +
                $"\nAt {times}" +
                "\nWhat you did: probably nothing; a loose plug, cable or charger cuts out like this" +
                $"\nWindows logged: {Quote(first.Message)} ×{changes.Count} (Kernel-Power 105, AcOnline switching between false and true)"));
        }
        yield return new Card("Charger", Rows(charger, "No changes yesterday or today"));

        // Each day from midnight (or until now): awake, each kind of time off
        // and the time signed in, counted within the day only.
        var totals = new List<(string, string)>();
        for (var day = since; day <= now.Date; day = day.AddDays(1))
        {
            var to = day.AddDays(1) < now ? day.AddDays(1) : now;
            var lines = new List<string> { to == day.AddDays(1) ? "Counted the whole day" : $"Counted {Span(day, to, now)}", $"Awake {Duration(to - day - AsleepWithin(periods, day, to))}" };
            foreach (var off in OffKinds)
            {
                var kinds = periods.Where(p => off.Kinds.Contains(p.Kind) && p.End > day && p.Start < to).ToList();
                if (kinds.Count > 0)
                    lines.Add($"{off.Label} {Duration(AsleepWithin(kinds, day, to))} ({kinds.Count}×)");
            }
            // A session without a sign-out ended at the next full stop or the
            // next sign-in, whichever came first; the last one is still on.
            var signedIn = TimeSpan.FromTicks(history.SignIns.Sum(s =>
            {
                var stop = periods.FirstOrDefault(p => p.Start > s.SignedIn
                    && p.Kind is WindowsStatus.SleepKind.Shutdown or WindowsStatus.SleepKind.Restart or WindowsStatus.SleepKind.Unexpected)?.Start;
                var next = history.SignIns.FirstOrDefault(o => o.SignedIn > s.SignedIn)?.SignedIn;
                var end = s.SignedOut ?? (stop < next || next == null ? stop : next) ?? now;
                var from = s.SignedIn > day ? s.SignedIn : day;
                var until = end < to ? end : to;
                return until > from ? (until - from - AsleepWithin(periods, from, until)).Ticks : 0;
            }));
            lines.Add($"Signed in and awake {Duration(signedIn)}");
            // Unplugged stretches clipped to the day, and how many were the
            // charger cutting out (plugged back in within a minute).
            var onBattery = TimeSpan.Zero;
            var dropouts = 0;
            for (var i = 0; i < history.Charger.Count; i++)
            {
                if (history.Charger[i].PluggedIn) continue;
                var off = history.Charger[i].Time;
                var back = i + 1 < history.Charger.Count ? history.Charger[i + 1].Time : now;
                if (back - off < TimeSpan.FromMinutes(1) && off >= day && off < to) dropouts++;
                var from = off > day ? off : day;
                var until = back < to ? back : to;
                if (until > from) onBattery += until - from;
            }
            if (onBattery > TimeSpan.Zero || dropouts > 0)
                lines.Add($"On battery {(onBattery < TimeSpan.FromMinutes(1) ? $"{onBattery.TotalSeconds:0} s" : Duration(onBattery))}" +
                    (dropouts > 0 ? $" · charger cut out {dropouts} {(dropouts == 1 ? "time" : "times")}" : ""));
            var title = day == now.Date ? "Today" : "Yesterday";
            totals.Add(($"{title}, {day.ToString(MonthDay)}", string.Join("\n", lines)));
        }
        yield return new Card("Totals", totals);
    }

    private static string KindName(WindowsStatus.SleepPeriod p) => p.Kind switch
    {
        WindowsStatus.SleepKind.Sleep => "Asleep",
        WindowsStatus.SleepKind.Hibernate => "Hibernated",
        WindowsStatus.SleepKind.FastStartupShutdown => "Fast Startup shutdown",
        WindowsStatus.SleepKind.FastStartupRestart => "Fast Startup restart → shutdown",
        WindowsStatus.SleepKind.Shutdown => "Full shutdown",
        WindowsStatus.SleepKind.Restart => "Restart",
        _ => "Unexpected shutdown",
    };

    // What the user did, as far as the log tells: who asked for a
    // shutdown, the reason Windows gives for a hibernation, and a sign-out
    // just before.
    private static string Action(WindowsStatus.SleepPeriod p, bool withSignOut = true)
    {
        var did = p.Kind switch
        {
            WindowsStatus.SleepKind.FastStartupShutdown or WindowsStatus.SleepKind.Shutdown => p.RequestedBy switch
            {
                "Start" => "clicked Shut down on Start",
                "sign-in screen" => "clicked Shut down on the sign-in screen",
                null when p.SignedOutBefore != null => "clicked Shut down on the sign-in screen",
                null => "shut down",
                var by => $"shut down by {by}",
            },
            WindowsStatus.SleepKind.FastStartupRestart or WindowsStatus.SleepKind.Restart => p.RequestedBy switch
            {
                "Start" => "clicked Restart on Start",
                "sign-in screen" => "clicked Restart on the sign-in screen",
                "Windows Update" => "Windows Update restarted the PC",
                null => "restarted",
                var by => $"restarted by {by}",
            },
            WindowsStatus.SleepKind.Unexpected =>
                $"nothing logged: the PC stopped without shutting down (a crash, power loss or holding the power button) at about {p.Start:t}, the last thing Windows logged",
            _ => p.Cause switch
            {
                "power button or lid" => "pressed the power button or closed the lid",
                "Start menu or an app" => p.Kind == WindowsStatus.SleepKind.Hibernate
                    ? "chose Hibernate on Start, or an app hibernated the PC"
                    : "chose Sleep on Start, or an app put the PC to sleep",
                null => p.Kind == WindowsStatus.SleepKind.Hibernate ? "hibernated" : "slept",
                var cause => $"Windows gives the reason as {cause}",
            },
        };
        if (withSignOut && p.SignedOutBefore is { } signedOut) did = $"signed out at {signedOut:T}, then {did}";
        return char.ToUpper(did[0]) + did[1..];
    }

    // What Windows logged going down and starting up, quoted, with the
    // power states and boot type it gives explained.
    private static string WindowsWords(WindowsStatus.SleepPeriod p)
    {
        var parts = new List<string>();
        if (p.DownMessage != null) parts.Add(Quote(p.DownMessage));
        if (p.TargetState != null && p.EffectiveState != null)
            parts.Add($"asked for {PowerState(p.TargetState)}, entered {PowerState(p.EffectiveState)}");
        if (p.UpMessage != null)
            parts.Add($"on start: {Quote(p.UpMessage)}" + (p.BootType switch
            {
                "0" or "0x0" => " (a full start)",
                "1" or "0x1" => " (a Fast Startup boot)",
                "2" or "0x2" => " (a resume from hibernation)",
                _ => "",
            }));
        return parts.Count > 0 ? string.Join(" · ", parts) : "nothing for this";
    }

    // Kernel-Power's system power states: 6 is S5, 5 is S4, 2 to 4 are S1 to S3.
    private static string PowerState(string state) => state switch
    {
        "6" => "S5 (shut down)",
        "5" => "S4 (hibernate)",
        "2" or "3" or "4" => $"S{int.Parse(state) - 1} (sleep)",
        _ => $"state {state}",
    };

    // " · 99% charged", from the charge Windows logged with the change.
    private static string Charge(WindowsStatus.ChargerChange change) =>
        change is { Charge: { } charge, FullCharge: { } full } ? $" · {Math.Min(100, 100.0 * charge / full):0}% charged" : "";

    // For "after the …": "hibernation", "Fast Startup shutdown".
    private static string Noun(WindowsStatus.SleepPeriod p) => p.Kind switch
    {
        WindowsStatus.SleepKind.Sleep => "sleep",
        WindowsStatus.SleepKind.Hibernate => "hibernation",
        _ => Lower(KindName(p)),
    };

    // "Hibernated" → "hibernated", keeping "Fast Startup" a name.
    private static string Lower(string text) =>
        text.StartsWith("Fast Startup", StringComparison.Ordinal) ? text : char.ToLower(text[0]) + text[1..];

    private static string Quote(string? message) => message == null ? "(no message)" : $"“{message}”";

    // How Windows came to start, shown after its start time:
    // "10:41 PM · restart".
    internal static string StartLabel(WindowsStatus.StartKind kind) => kind switch
    {
        WindowsStatus.StartKind.Restart => "restart",
        WindowsStatus.StartKind.Shutdown => "shut down",
        _ => "unexpected shutdown",
    };

    // The kinds of time off, each with its label in the totals ("Hibernated
    // for") and its word in the uptime card ("12 h 41 min hibernated").
    internal static readonly (string Label, string Word, WindowsStatus.SleepKind[] Kinds)[] OffKinds =
    [
        ("Asleep for", "asleep", [WindowsStatus.SleepKind.Sleep]),
        ("Hibernated for", "hibernated", [WindowsStatus.SleepKind.Hibernate]),
        ("Fast Startup shutdown for", "shut down", [WindowsStatus.SleepKind.FastStartupShutdown, WindowsStatus.SleepKind.FastStartupRestart]),
        ("Off (full shutdown, restart or crash) for", "off",
            [WindowsStatus.SleepKind.Shutdown, WindowsStatus.SleepKind.Restart, WindowsStatus.SleepKind.Unexpected]),
    ];

    internal static TimeSpan AsleepWithin(IEnumerable<WindowsStatus.SleepPeriod> sleeps, DateTime from, DateTime to) =>
        TimeSpan.FromTicks(sleeps.Where(p => p.End > from && p.Start < to)
            .Sum(p => ((p.End < to ? p.End : to) - (p.Start > from ? p.Start : from)).Ticks));

    // "12:00 AM → 1:03 AM", or "1:44 PM → 2:21 PM (now)".
    private static string Span(DateTime from, DateTime to, DateTime now) =>
        $"{from:t} → {(to == now ? $"{to:t} (now)" : $"{to:t}")}";

    // "1:45 PM · Sat, Oct 17": the time with its date always shown.
    internal static string WithDate(DateTime time) => $"{time:t} · {time.ToString("ddd, " + MonthDay)}";

    // Windows' month/day order with a short month name: "Oct 17" or "17 Oct".
    private static string MonthDay =>
        System.Globalization.CultureInfo.CurrentCulture.DateTimeFormat.MonthDayPattern.Replace("MMMM", "MMM");

    // "1:45 PM" today, "10:41 PM yesterday", "Tue 9:12 AM" this week, or
    // the short date and time before that. Uses Windows' own time format.
    internal static string When(DateTime time)
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

    internal static string Duration(TimeSpan span) =>
        span.TotalDays >= 1 ? $"{(int)span.TotalDays} d {span.Hours} h {span.Minutes} min"
        : span.TotalHours >= 1 ? $"{span.Hours} h {span.Minutes} min"
        : $"{span.Minutes} min";

    // ---- Cards ----

    private sealed record Card(string Title, IReadOnlyList<(string Label, string Value)> Rows,
        double? Percent = null, string? PercentText = null, (string Text, bool Checked)? Check = null)
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
        public CheckBox? Check { get; init; }
        public required List<(TextBlock Label, TextBlock Value)> Cells { get; init; }

        public bool Fits(Card card) =>
            (Bar != null) == (card.Percent != null) && (Check != null) == (card.Check != null) && Cells.Count == card.Rows.Count;

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
            if (Check != null && card.Check is { } check)
            {
                if (!Equals(Check.Content, check.Text)) Check.Content = check.Text;
                Check.IsChecked = check.Checked;
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

            // Shows a setting; it isn't changed here (that needs an administrator).
            CheckBox? check = null;
            if (card.Check != null)
            {
                check = new CheckBox { IsHitTestVisible = false, Focusable = false, Margin = new Thickness(0, 0, 0, 6) };
                panel.Children.Add(check);
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
            var view = new CardView { Root = root, Title = title, Bar = bar, BarText = barText, Check = check, Cells = cells };
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
