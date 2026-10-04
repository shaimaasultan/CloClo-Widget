using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Automation;
using Clipboard = System.Windows.Clipboard;
using DataObject = System.Windows.DataObject;
using IDataObject = System.Windows.IDataObject;

namespace CloCloWidget;

// Grabs whatever text is highlighted in another app. Asks the app directly
// through UI Automation first (TextPattern.GetSelection) — no keystrokes,
// no clipboard involved. Only for apps that don't expose their selection
// that way does it fall back to the classic utility trick: refocus the
// window, send Ctrl+C, read the clipboard, then put the clipboard back the
// way it was.
//
// The fallback is guarded, because a synthetic Ctrl+C is not harmless:
// - the target must still be the foreground window right before the keys
//   go out — never send them to whatever happened to take focus instead;
// - terminals are skipped entirely, since Ctrl+C there means "interrupt
//   the running command", not "copy".
public static class TextSelectionReader
{
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int GetWindowThreadProcessId(IntPtr hWnd, out int lpdwProcessId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, StringBuilder name, int maxCount);

    private static readonly TimeSpan UiAutomationTimeout = TimeSpan.FromMilliseconds(1500);

    private static readonly string[] TerminalWindowClasses =
    {
        "ConsoleWindowClass",              // classic conhost
        "CASCADIA_HOSTING_WINDOW_CLASS",   // Windows Terminal
        "PseudoConsoleWindow",
        "VirtualConsoleClass",             // ConEmu
        "mintty",                          // Git Bash, Cygwin, MSYS2
        "PuTTY",
    };

    private static readonly string[] TerminalProcessNames =
    {
        "WindowsTerminal", "conhost", "OpenConsole", "wezterm-gui", "alacritty", "kitty", "mintty", "putty", "ConEmu64", "ConEmu",
    };

    // Windows' documented clipboard-history opt-outs: content carrying these
    // formats stays out of Win+V history and cloud clipboard sync.
    private const string ExcludeFromMonitoringFormat = "ExcludeClipboardContentFromMonitorProcessing";
    private const string CanIncludeInHistoryFormat = "CanIncludeInClipboardHistory";
    private const string CanUploadToCloudFormat = "CanUploadToCloudClipboard";

    public static async Task<string?> ReadSelectedTextAsync(IntPtr targetWindow)
    {
        if (targetWindow == IntPtr.Zero || !IsWindow(targetWindow)) return null;

        SetForegroundWindow(targetWindow);
        await Task.Delay(120);
        if (GetForegroundWindow() != targetWindow) return null;
        // Never read out of a password box — not via UI Automation, and not
        // by sending it Ctrl+C either.
        if (await Task.Run(IsFocusedPasswordField)) return null;

        var automationTask = Task.Run(() => TryReadViaUiAutomation(targetWindow));
        if (await Task.WhenAny(automationTask, Task.Delay(UiAutomationTimeout)) == automationTask
            && !string.IsNullOrEmpty(automationTask.Result))
        {
            return automationTask.Result;
        }

        if (IsTerminal(targetWindow)) return null;
        // Re-checked: the UI Automation attempt took time, and the user may
        // have moved on to another window since.
        if (GetForegroundWindow() != targetWindow) return null;
        return await ReadViaClipboardAsync();
    }

    private static bool IsFocusedPasswordField()
    {
        try { return AutomationElement.FocusedElement?.Current.IsPassword == true; }
        catch { return false; }
    }

    private static string? TryReadViaUiAutomation(IntPtr targetWindow)
    {
        try
        {
            var focused = AutomationElement.FocusedElement;
            if (focused == null) return null;
            GetWindowThreadProcessId(targetWindow, out var targetPid);
            if (focused.Current.ProcessId != targetPid) return null;

            if (!focused.TryGetCurrentPattern(TextPattern.Pattern, out var pattern)) return null;
            var ranges = ((TextPattern)pattern).GetSelection();
            var text = string.Concat(ranges.Select(r => r.GetText(-1)));
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch
        {
            return null; // not every app implements TextPattern, or implements it well
        }
    }

    private static bool IsTerminal(IntPtr hwnd)
    {
        var className = new StringBuilder(256);
        GetClassName(hwnd, className, className.Capacity);
        if (TerminalWindowClasses.Contains(className.ToString(), StringComparer.OrdinalIgnoreCase)) return true;

        try
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            using var process = Process.GetProcessById(pid);
            return TerminalProcessNames.Contains(process.ProcessName, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static async Task<string?> ReadViaClipboardAsync()
    {
        var saved = SnapshotClipboard();
        var before = TryGetClipboardText();

        if (!KeyboardInput.SendChord(KeyboardInput.VK_CONTROL, KeyboardInput.VK_C)) return null;
        await Task.Delay(150);

        var after = TryGetClipboardText();
        var changed = !string.IsNullOrEmpty(after) && after != before;
        if (changed) RestoreClipboard(saved);
        return changed ? after : null;
    }

    // A copy of every format currently on the clipboard that can be read
    // back — the live IDataObject would be gone once Ctrl+C replaces it.
    private static DataObject? SnapshotClipboard()
    {
        try
        {
            var current = Clipboard.GetDataObject();
            if (current == null) return null;
            var copy = new DataObject();
            foreach (var format in current.GetFormats(autoConvert: false))
            {
                try
                {
                    var data = current.GetData(format, autoConvert: false);
                    if (data != null) copy.SetData(format, data, autoConvert: false);
                }
                catch
                {
                    // Some formats are delay-rendered or private — skip them.
                }
            }
            return copy.GetFormats(autoConvert: false).Length > 0 ? copy : null;
        }
        catch
        {
            return null;
        }
    }

    private static void RestoreClipboard(IDataObject? saved)
    {
        try
        {
            if (saved == null)
            {
                Clipboard.Clear();
                return;
            }
            // It's already in clipboard history from when it was first
            // copied — keep the restore from adding a duplicate entry there
            // or re-uploading it to the cloud clipboard.
            saved.SetData(ExcludeFromMonitoringFormat, new System.IO.MemoryStream(new byte[4]));
            saved.SetData(CanIncludeInHistoryFormat, new System.IO.MemoryStream(BitConverter.GetBytes(0)));
            saved.SetData(CanUploadToCloudFormat, new System.IO.MemoryStream(BitConverter.GetBytes(0)));
            Clipboard.SetDataObject(saved, copy: true);
        }
        catch
        {
            // Clipboard locked by another app — nothing more to do.
        }
    }

    private static string? TryGetClipboardText()
    {
        try { return Clipboard.ContainsText() ? Clipboard.GetText() : null; }
        catch { return null; }
    }
}
