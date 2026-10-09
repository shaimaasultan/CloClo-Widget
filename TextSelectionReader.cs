using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using Clipboard = System.Windows.Clipboard;
using DataObject = System.Windows.DataObject;
using IDataObject = System.Windows.IDataObject;

namespace CloCloWidget;

// Grabs whatever text is highlighted in another app. Asks the app directly
// through UI Automation first. If the app does not expose its selection,
// refocus it, send Ctrl+C, read the copied text and restore the clipboard.
// The fallback never sends Ctrl+C to a terminal or a password field.
public static class TextSelectionReader
{
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] private static extern int GetWindowThreadProcessId(IntPtr hWnd, out int lpdwProcessId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, StringBuilder name, int maxCount);

    private static readonly TimeSpan UiAutomationTimeout = TimeSpan.FromMilliseconds(1500);
    // UI Automation providers can hang inside a synchronous COM call. A
    // timeout cannot interrupt that call, so serialize the work and avoid
    // accumulating additional stuck worker threads on repeated requests.
    private static readonly SemaphoreSlim UiAutomationGate = new(1, 1);

    private static readonly string[] TerminalWindowClasses =
    {
        "ConsoleWindowClass", "CASCADIA_HOSTING_WINDOW_CLASS", "PseudoConsoleWindow",
        "VirtualConsoleClass", "ConEmu", "mintty", "PuTTY",
    };

    private static readonly string[] TerminalProcessNames =
    {
        "WindowsTerminal", "conhost", "OpenConsole", "wezterm-gui", "alacritty",
        "kitty", "mintty", "putty", "ConEmu64", "ConEmu",
    };

    private const string ExcludeFromMonitoringFormat = "ExcludeClipboardContentFromMonitorProcessing";
    private const string CanIncludeInHistoryFormat = "CanIncludeInClipboardHistory";
    private const string CanUploadToCloudFormat = "CanUploadToCloudClipboard";

    public static async Task<string?> ReadSelectedTextAsync(IntPtr targetWindow)
    {
        if (targetWindow == IntPtr.Zero || !IsWindow(targetWindow)) return null;

        SetForegroundWindow(targetWindow);
        await Task.Delay(120);
        if (GetForegroundWindow() != targetWindow) return null;
        using var timeout = new CancellationTokenSource(UiAutomationTimeout);
        try
        {
            var automationTask = Task.Run(async () =>
            {
                await UiAutomationGate.WaitAsync(timeout.Token);
                try
                {
                    timeout.Token.ThrowIfCancellationRequested();
                    if (IsFocusedPasswordField()) return null;
                    return TryReadViaUiAutomation(targetWindow);
                }
                finally
                {
                    UiAutomationGate.Release();
                }
            });
            if (await Task.WhenAny(automationTask, Task.Delay(UiAutomationTimeout)) != automationTask)
            {
                timeout.Cancel();
                return null;
            }
            var text = await automationTask;
            if (!string.IsNullOrEmpty(text)) return text;
        }
        catch (OperationCanceledException)
        {
            return null;
        }

        if (IsTerminal(targetWindow) || GetForegroundWindow() != targetWindow) return null;
        if (await Task.Run(IsFocusedPasswordField)) return null;
        return await ReadViaClipboardAsync(targetWindow);
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

    private static async Task<string?> ReadViaClipboardAsync(IntPtr targetWindow)
    {
        var saved = SnapshotClipboard();

        // Focus can change while the clipboard snapshot is being built.
        if (GetForegroundWindow() != targetWindow) return null;
        var before = GetClipboardSequenceNumber();
        if (!KeyboardInput.SendChord(KeyboardInput.VK_CONTROL, KeyboardInput.VK_C)) return null;
        await Task.Delay(150);

        var changed = GetClipboardSequenceNumber() != before;
        if (!changed) return null;
        var text = TryGetClipboardText();
        RestoreClipboard(saved);
        return GetForegroundWindow() == targetWindow && !string.IsNullOrEmpty(text) ? text : null;
    }

    // The live IDataObject can become invalid when Ctrl+C replaces it.
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
                    // Some formats are delayed or private and cannot be copied.
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
            // Keep the restored content out of clipboard history and sync.
            saved.SetData(ExcludeFromMonitoringFormat, new System.IO.MemoryStream(new byte[4]));
            saved.SetData(CanIncludeInHistoryFormat, new System.IO.MemoryStream(BitConverter.GetBytes(0)));
            saved.SetData(CanUploadToCloudFormat, new System.IO.MemoryStream(BitConverter.GetBytes(0)));
            Clipboard.SetDataObject(saved, copy: true);
        }
        catch
        {
            // Another app may temporarily own the clipboard.
        }
    }

    private static string? TryGetClipboardText()
    {
        try { return Clipboard.ContainsText() ? Clipboard.GetText() : null; }
        catch { return null; }
    }
}
