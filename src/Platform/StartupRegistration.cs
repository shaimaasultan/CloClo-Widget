using System;
using System.IO;
using Microsoft.Win32;

namespace CloCloWidget;

// A per-user "start with Windows" toggle via the classic Run key — no admin
// rights needed, no installer, no shortcut file to manage. The registry
// itself is the source of truth (not settings.json), so the checkbox always
// reflects what's actually registered.
public static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "CloCloWidget";
    public const string SecurityWaitArgument = "--wait-for-security";

    private static string StartupCommand(string exe) => $"\"{exe}\" {SecurityWaitArgument}";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(ValueName) is string;
    }

    // Keeps an enabled "Start with Windows" pointing at the installed copy
    // that's actually running. After moving to Program Files, an entry left
    // pointing at the removed per-user copy would never start CloClo - and
    // that folder is writable by any program running as this user, which
    // could put its own CloCloWidget.exe there for Windows to start at every
    // sign-in. Only an installed copy (Program Files, or the per-user install
    // folder) ever repairs it, so a development build or a copy run from
    // elsewhere can't take the entry over; and it only does so when the
    // registered file is gone, or when the running copy is the Program Files
    // one and the entry points somewhere else.
    public static void RepairIfStale()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            var replacement = RepairedValue(exe, key?.GetValue(ValueName) as string, File.Exists,
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
            if (replacement != null) key!.SetValue(ValueName, replacement);
        }
        catch
        {
            // Best effort - the menu item still lets the user turn it off and on.
        }
    }

    // The decision alone, kept apart from the registry so it can be checked
    // on its own: the new Run value, or null to leave the entry as it is.
    internal static string? RepairedValue(string exe, string? registeredValue, Func<string, bool> fileExists,
        string programFiles, string localAppData)
    {
        if (registeredValue == null) return null; // not enabled
        var programFilesDir = Path.Combine(programFiles, "CloClo") + Path.DirectorySeparatorChar;
        var userDir = Path.Combine(localAppData, "Programs", "CloCloWidget") + Path.DirectorySeparatorChar;
        var inProgramFiles = exe.StartsWith(programFilesDir, StringComparison.OrdinalIgnoreCase);
        if (!inProgramFiles && !exe.StartsWith(userDir, StringComparison.OrdinalIgnoreCase)) return null;

        var registered = RegisteredExecutable(registeredValue);
        if (string.Equals(registeredValue, StartupCommand(exe), StringComparison.OrdinalIgnoreCase)) return null;
        return inProgramFiles || string.Equals(registered, exe, StringComparison.OrdinalIgnoreCase)
            || registered == null || !fileExists(registered)
            ? StartupCommand(exe) : null;
    }

    private static string? RegisteredExecutable(string command)
    {
        command = command.Trim();
        if (!command.StartsWith('"')) return command; // older unquoted entry
        var closingQuote = command.IndexOf('"', 1);
        if (closingQuote < 0) return null;
        var argument = command[(closingQuote + 1)..].Trim();
        if (argument.Length > 0 && argument != SecurityWaitArgument) return null;
        return command[1..closingQuote];
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
            ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);
        if (enabled)
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath)) return;
            key.SetValue(ValueName, StartupCommand(exePath));
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
