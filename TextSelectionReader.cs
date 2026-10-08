using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;

namespace CloCloWidget;

// Grabs whatever text is highlighted in another app. Asks the app directly
// through UI Automation (TextPattern.GetSelection). It never sends
// keystrokes or reads/writes the clipboard. Apps that don't expose their
// selection through UI Automation simply return no text.
public static class TextSelectionReader
{
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int GetWindowThreadProcessId(IntPtr hWnd, out int lpdwProcessId);

    private static readonly TimeSpan UiAutomationTimeout = TimeSpan.FromMilliseconds(1500);
    // UI Automation providers can hang inside a synchronous COM call. A
    // timeout cannot interrupt that call, so serialize the work and avoid
    // accumulating additional stuck worker threads on repeated requests.
    private static readonly SemaphoreSlim UiAutomationGate = new(1, 1);

    public static async Task<string?> ReadSelectedTextAsync(IntPtr targetWindow)
    {
        if (targetWindow == IntPtr.Zero || !IsWindow(targetWindow)) return null;

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
            return await automationTask;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
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
}
