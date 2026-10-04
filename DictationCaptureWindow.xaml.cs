using System;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Media = System.Windows.Media;

namespace CloCloWidget;

// Delegates the actual listening to Windows' own Voice Typing (Win+H)
// instead of calling WinRT's SpeechRecognizer directly — Voice Typing turned
// out to reliably capture real audio on this machine while our own
// SpeechRecognizer-based attempt (see SpeechToText.cs) did not, tracked down
// to the Windows input device that held the "Default Device" role not being
// the physical microphone. Voice Typing has no way to hand recognized text
// back to a caller programmatically; it only types into whatever control
// currently has keyboard focus — so this window exists purely to be that
// focused text target.
public partial class DictationCaptureWindow : Window
{
    private const ushort VK_H = 0x48;

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int GetWindowThreadProcessId(IntPtr hWnd, out int lpdwProcessId);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    private const uint WM_CLOSE = 0x0010;
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    private const string EnglishLanguageTag = "en-US";
    private const string ArabicLanguageTag = "ar-EG";
    private static readonly Media.Brush ActiveLangBrush = new Media.SolidColorBrush(Media.Color.FromRgb(0xc9, 0xa2, 0x4b));
    private static readonly Media.Brush InactiveLangBrush = new Media.SolidColorBrush(Media.Color.FromRgb(0x3c, 0x2f, 0x1a));

    private string _currentLanguageTag = EnglishLanguageTag;
    // Switching languages changes the input language (see below) — put
    // back whatever the user had once this window is done.
    private readonly CultureInfo _originalInputLanguage = InputLanguageManager.Current.CurrentInputLanguage;

    public string CapturedText { get; private set; } = "";

    public DictationCaptureWindow()
    {
        InitializeComponent();
        UpdateLanguageHighlight();
        Closed += (_, _) =>
        {
            try { InputLanguageManager.Current.CurrentInputLanguage = _originalInputLanguage; }
            catch { /* that layout was removed meanwhile */ }
        };
        Loaded += async (_, _) =>
        {
            CaptureBox.Focus();
            // Give the window's focus a moment to fully settle before
            // triggering Voice Typing — sending Win+H immediately after
            // Loaded risked it targeting a still-transitioning focus state
            // during testing.
            await Task.Delay(300);
            InputLanguageManager.Current.CurrentInputLanguage = CultureInfo.GetCultureInfo(_currentLanguageTag);
            OpenVoiceTyping();
        };
    }

    // There's no public API to tell Voice Typing which language to listen
    // in — the only lever available is Windows' own active input language,
    // which it appears to follow. A session already listening doesn't
    // retroactively pick up a change to that, so switching languages closes
    // and reopens it rather than just flipping CurrentInputLanguage.
    private async void English_Click(object sender, RoutedEventArgs e) => await SwitchLanguageAsync(EnglishLanguageTag);
    private async void Arabic_Click(object sender, RoutedEventArgs e) => await SwitchLanguageAsync(ArabicLanguageTag);

    private async Task SwitchLanguageAsync(string cultureTag)
    {
        if (cultureTag == _currentLanguageTag) return;
        _currentLanguageTag = cultureTag;
        UpdateLanguageHighlight();

        CloseVoiceTyping();
        CaptureBox.Focus();
        InputLanguageManager.Current.CurrentInputLanguage = CultureInfo.GetCultureInfo(cultureTag);
        await Task.Delay(300);
        OpenVoiceTyping();
    }

    private void UpdateLanguageHighlight()
    {
        EnglishButton.Background = _currentLanguageTag == EnglishLanguageTag ? ActiveLangBrush : InactiveLangBrush;
        ArabicButton.Background = _currentLanguageTag == ArabicLanguageTag ? ActiveLangBrush : InactiveLangBrush;
    }

    private static void OpenVoiceTyping() => KeyboardInput.SendChord(KeyboardInput.VK_LWIN, VK_H);

    // Neither re-sending Win+H (it TOGGLES, so it can reopen a session that
    // already auto-closed on its own) nor sending Escape (Voice Typing's
    // floating bar never actually takes keyboard focus away from this
    // window's own textbox, so Escape has nothing to land on) reliably
    // closes it — confirmed both leaving it open during testing. Its
    // floating toolbar turned out to just be a normal top-level window
    // (class "Windows.UI.Core.CoreWindow", hosted by TextInputHost.exe),
    // so closing it directly with WM_CLOSE is what actually works.
    private static void CloseVoiceTyping()
    {
        IntPtr target = IntPtr.Zero;
        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd)) return true;
            var className = new StringBuilder(256);
            GetClassName(hWnd, className, className.Capacity);
            if (className.ToString() != "Windows.UI.Core.CoreWindow") return true;

            GetWindowThreadProcessId(hWnd, out var pid);
            try
            {
                if (Process.GetProcessById(pid).ProcessName == "TextInputHost")
                {
                    target = hWnd;
                    return false; // found it, stop enumerating
                }
            }
            catch { /* process exited between enumeration and lookup */ }
            return true;
        }, IntPtr.Zero);

        if (target != IntPtr.Zero) PostMessage(target, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        CapturedText = CaptureBox.Text.Trim();
        CloseVoiceTyping();
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        CloseVoiceTyping();
        DialogResult = false;
        Close();
    }
}
