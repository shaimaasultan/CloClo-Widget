using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace CloCloWidget;

// A fullscreen, effectively-invisible window that owns mouse input while
// picking is active — the first attempt at this (polling GetAsyncKeyState
// from a background timer, no real window under the cursor) couldn't
// change the system cursor at all (whichever real window was actually
// under the pointer controlled that, not us) and couldn't stop a click
// from also reaching whatever was underneath. A real topmost window
// covering the whole virtual screen fixes both: it genuinely owns
// WM_SETCURSOR and the click itself while it's up, confirmed by user
// report that neither worked with the polling version.
//
// Background is #01000000 (alpha 1/255), not fully transparent — a
// layered window at alpha 0 is invisible to hit-testing entirely, the
// same trick _avatarWindow already relies on elsewhere in this app.
public partial class ColorPickerOverlay : Window
{
    public event Action<System.Drawing.Color>? Previewed;
    public event Action<System.Drawing.Color>? Picked;
    public event Action? Cancelled;

    // A fullscreen topmost window that swallows every click is a trap if
    // it ever can't be dismissed: once it loses keyboard focus, Escape no
    // longer reaches it. So it also cancels itself the moment it's
    // deactivated (Alt+Tab, Win key, another app grabbing focus), and after
    // a minute regardless.
    private static readonly TimeSpan MaxPickTime = TimeSpan.FromMinutes(1);
    private readonly DispatcherTimer _timeout = new() { Interval = MaxPickTime };
    private bool _finished;

    public ColorPickerOverlay()
    {
        InitializeComponent();
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;

        MouseMove += (_, _) => Previewed?.Invoke(SampleAtCursor());
        MouseLeftButtonDown += (_, _) => Finish(picked: true);
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Finish(picked: false);
        };
        Deactivated += (_, _) => Finish(picked: false);
        _timeout.Tick += (_, _) => Finish(picked: false);
        Loaded += (_, _) =>
        {
            Activate();
            Focus(); // needs keyboard focus to actually see Escape
            _timeout.Start();
        };
        Closed += (_, _) => _timeout.Stop();
    }

    // Exactly once, whichever way it ends — closing also deactivates the
    // window, which must not turn a pick into a cancel after the fact.
    private void Finish(bool picked)
    {
        if (_finished) return;
        _finished = true;
        if (picked) Picked?.Invoke(SampleAtCursor());
        else Cancelled?.Invoke();
        Close();
    }

    private static System.Drawing.Color SampleAtCursor() =>
        ColorPicker.GetColorAt(System.Windows.Forms.Cursor.Position);
}
