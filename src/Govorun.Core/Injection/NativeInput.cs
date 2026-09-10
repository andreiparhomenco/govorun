using System.Runtime.InteropServices;

namespace Govorun.Core.Injection;

/// <summary>P/Invoke layer for SendInput-based keyboard emulation.</summary>
internal static class NativeInput
{
    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_UNICODE = 0x0004;
    internal const ushort VK_CONTROL = 0x11;
    internal const ushort VK_V = 0x56;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public KEYBDINPUT ki;
        // Pad to the size of the largest union member (MOUSEINPUT).
        private readonly long _padding;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, char[] lpClassName, int nMaxCount);

    internal static string GetForegroundWindowClass()
    {
        var hwnd = GetForegroundWindow();
        var buffer = new char[256];
        int len = GetClassName(hwnd, buffer, buffer.Length);
        return len > 0 ? new string(buffer, 0, len) : "";
    }

    private static INPUT Key(ushort vk, bool up) => new()
    {
        type = INPUT_KEYBOARD,
        ki = new KEYBDINPUT { wVk = vk, dwFlags = up ? KEYEVENTF_KEYUP : 0 },
    };

    private static INPUT UnicodeKey(char c, bool up) => new()
    {
        type = INPUT_KEYBOARD,
        ki = new KEYBDINPUT { wScan = c, dwFlags = KEYEVENTF_UNICODE | (up ? KEYEVENTF_KEYUP : 0) },
    };

    internal static void SendCtrlV()
    {
        var inputs = new[]
        {
            Key(VK_CONTROL, up: false),
            Key(VK_V, up: false),
            Key(VK_V, up: true),
            Key(VK_CONTROL, up: true),
        };
        uint result = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (result == 0)
        {
            Serilog.Log.Warning("SendInput (Ctrl+V) failed with error: {Error}", Marshal.GetLastWin32Error());
        }
    }

    internal static void TypeText(string text)
    {
        foreach (var c in text)
        {
            if (c == '\r') continue;
            if (c == '\n')
            {
                var enter = new[] { Key(0x0D, false), Key(0x0D, true) };
                if (SendInput(2, enter, Marshal.SizeOf<INPUT>()) == 0)
                    Serilog.Log.Warning("SendInput (Enter) failed with error: {Error}", Marshal.GetLastWin32Error());
            }
            else
            {
                var inputs = new[] { UnicodeKey(c, false), UnicodeKey(c, true) };
                if (SendInput(2, inputs, Marshal.SizeOf<INPUT>()) == 0)
                    Serilog.Log.Warning("SendInput (char) failed with error: {Error}", Marshal.GetLastWin32Error());
            }
            Thread.Sleep(2); // let slow targets (terminals, RDP) keep up
        }
    }
}
