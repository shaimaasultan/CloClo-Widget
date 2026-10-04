using System;
using System.Linq;
using System.Runtime.InteropServices;

namespace CloCloWidget;

// Synthesized key presses via SendInput — the supported replacement for
// keybd_event. The whole sequence goes in one call, so Windows inserts it
// into the input stream atomically: real keystrokes can't land in the
// middle of it and turn Ctrl+C into something else.
public static class KeyboardInput
{
    public const ushort VK_CONTROL = 0x11;
    public const ushort VK_LWIN = 0x5B;
    public const ushort VK_C = 0x43;
    public const ushort VK_N = 0x4E;

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    // Presses the keys in order, then releases them in reverse — a chord.
    public static bool SendChord(params ushort[] keys)
    {
        var inputs = keys.Select(k => Key(k, up: false))
            .Concat(keys.Reverse().Select(k => Key(k, up: true)))
            .ToArray();
        return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>()) == inputs.Length;
    }

    private static INPUT Key(ushort vk, bool up) => new()
    {
        type = INPUT_KEYBOARD,
        u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = up ? KEYEVENTF_KEYUP : 0 } },
    };

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    // The union must be as large as its biggest member (MOUSEINPUT), or
    // cbSize is wrong and SendInput rejects every input.
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk, wScan;
        public uint dwFlags, time;
        public IntPtr dwExtraInfo;
    }
}
