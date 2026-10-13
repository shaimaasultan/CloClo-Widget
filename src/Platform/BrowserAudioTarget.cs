using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace CloCloWidget;

// Picks the web browser whose audio "Transcribe playing audio" should capture
// on its own, leaving out notification sounds and other apps. Only fixed
// browser executables qualify; the result is a process id for Windows'
// per-app loopback capture and is never used to start, open, or signal
// anything.
internal static class BrowserAudioTarget
{
    private static readonly Dictionary<string, string> Browsers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["msedge.exe"] = "Microsoft Edge",
        ["chrome.exe"] = "Google Chrome",
        ["firefox.exe"] = "Firefox",
        ["brave.exe"] = "Brave",
        ["opera.exe"] = "Opera",
        ["vivaldi.exe"] = "Vivaldi",
    };

    internal sealed record Target(uint ProcessId, string Name);

    private readonly record struct ProcessEntry(uint Parent, string Exe);

    // Prefers the browser playing sound right now, then one that has played
    // sound on the default output device, then any running browser. Returns
    // the browser's main process: Chromium and Firefox play web audio from
    // child processes, which per-app capture includes from the main one.
    public static Target? Find()
    {
        var processes = Snapshot();
        Target? fallback = null;
        foreach (var (processId, active) in RenderSessions())
        {
            if (!processes.TryGetValue(processId, out var entry) || !Browsers.TryGetValue(entry.Exe, out var name)) continue;
            var target = new Target(MainProcess(processes, processId), name);
            if (active) return target;
            fallback ??= target;
        }
        if (fallback != null) return fallback;
        foreach (var (processId, entry) in processes)
        {
            if (Browsers.TryGetValue(entry.Exe, out var name) && MainProcess(processes, processId) == processId)
                return new Target(processId, name);
        }
        return null;
    }

    // Walks up while the parent is the same browser executable.
    private static uint MainProcess(Dictionary<uint, ProcessEntry> processes, uint processId)
    {
        var current = processId;
        for (var depth = 0; depth < 32; depth++)
        {
            if (!processes.TryGetValue(current, out var entry) ||
                !processes.TryGetValue(entry.Parent, out var parent) ||
                !parent.Exe.Equals(entry.Exe, StringComparison.OrdinalIgnoreCase)) break;
            current = entry.Parent;
        }
        return current;
    }

    private static List<(uint ProcessId, bool Active)> RenderSessions()
    {
        var sessions = new List<(uint, bool)>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            using var collection = device.AudioSessionManager.Sessions;
            for (var i = 0; i < collection.Count; i++)
            {
                using var session = collection[i];
                if (session.IsSystemSoundsSession) continue;
                sessions.Add((session.GetProcessID, session.State == AudioSessionState.AudioSessionStateActive));
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            // No output device: fall back to any running browser.
        }
        return sessions;
    }

    private static Dictionary<uint, ProcessEntry> Snapshot()
    {
        var processes = new Dictionary<uint, ProcessEntry>();
        var snapshot = CreateToolhelp32Snapshot(SnapProcess, 0);
        if (snapshot == InvalidHandle) return processes;
        try
        {
            var entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
            for (var more = Process32FirstW(snapshot, ref entry); more; more = Process32NextW(snapshot, ref entry))
                processes[entry.ProcessId] = new ProcessEntry(entry.ParentProcessId, entry.ExeFile);
        }
        finally
        {
            CloseHandle(snapshot);
        }
        return processes;
    }

    private const uint SnapProcess = 0x00000002;
    private static readonly IntPtr InvalidHandle = new(-1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
