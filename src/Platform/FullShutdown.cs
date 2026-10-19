using System;
using System.Runtime.InteropServices;

namespace CloCloWidget;

// A full shutdown, the same as "shutdown /s": InitiateShutdown without
// SHUTDOWN_HYBRID, so Windows signs out and powers off without saving its
// kernel session for Fast Startup. The next start is a cold boot, as after a
// restart, whatever the Fast Startup setting is; that setting is not read or
// changed. Other apps are not forced closed: one with unsaved work can still
// hold the shutdown so the user can save or cancel it.
internal static class FullShutdown
{
    private const uint SHUTDOWN_POWEROFF = 0x00000008;
    private const uint SHTDN_REASON_FLAG_PLANNED = 0x80000000;
    private const uint SHTDN_REASON_MAJOR_APPLICATION = 0x00040000;
    private const uint SHTDN_REASON_MINOR_OTHER = 0x00000000;
    private const uint TOKEN_ADJUST_PRIVILEGES = 0x0020, TOKEN_QUERY = 0x0008;
    private const uint SE_PRIVILEGE_ENABLED = 0x00000002;
    private const int ERROR_NOT_ALL_ASSIGNED = 1300;

    // Null when Windows has started shutting down, otherwise why not.
    internal static string? Start()
    {
        if (!EnableShutdownPrivilege()) return "Windows did not allow CloClo to shut down.";
        // The comment and reason go into Windows' own record of the request
        // (User32 1074 in the System log), which names CloCloWidget.exe, so
        // Windows status can tell a voice shutdown apart; see
        // WindowsStatus.ReadShutdownRequests.
        var result = InitiateShutdown(null, WindowsStatus.VoiceCommand, 0, SHUTDOWN_POWEROFF,
            SHTDN_REASON_FLAG_PLANNED | SHTDN_REASON_MAJOR_APPLICATION | SHTDN_REASON_MINOR_OTHER);
        if (result != 0) return $"Windows did not start the shutdown (error {result}).";
        // Kept too, in case Windows doesn't log the request.
        PowerRequests.Record("voice");
        return null;
    }

    // Every interactive user holds the shutdown privilege, but it starts
    // disabled in the process token and must be switched on first.
    private static bool EnableShutdownPrivilege()
    {
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out var token)) return false;
        try
        {
            if (!LookupPrivilegeValue(null, "SeShutdownPrivilege", out var luid)) return false;
            var privileges = new TOKEN_PRIVILEGES { PrivilegeCount = 1, Luid = luid, Attributes = SE_PRIVILEGE_ENABLED };
            return AdjustTokenPrivileges(token, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero) &&
                   Marshal.GetLastWin32Error() != ERROR_NOT_ALL_ASSIGNED;
        }
        finally { CloseHandle(token); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_PRIVILEGES
    {
        public uint PrivilegeCount;
        public LUID Luid;
        public uint Attributes;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern uint InitiateShutdown(string? machineName, string? message, uint gracePeriod,
        uint shutdownFlags, uint reason);
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool LookupPrivilegeValue(string? systemName, string name, out LUID luid);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TOKEN_PRIVILEGES newState,
        uint bufferLength, IntPtr previousState, IntPtr returnLength);
    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
