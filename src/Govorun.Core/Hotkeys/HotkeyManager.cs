using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Govorun.Core.Hotkeys;

public enum HotkeyMode
{
    /// <summary>Double-tap Ctrl within 400 ms.</summary>
    DoubleCtrl,
    /// <summary>Ctrl+Space (the Space keystroke is swallowed).</summary>
    CtrlSpace,
    /// <summary>Ctrl+Win (the Win keystroke is swallowed so the Start menu stays closed).</summary>
    CtrlWin,
}

public enum ActivationMode
{
    /// <summary>Press once to start recording, press again to stop.</summary>
    Toggle,
    /// <summary>Record while the hotkey is held; release to stop (push-to-talk).</summary>
    PushToTalk,
}

/// <summary>
/// Global hotkey detection via a WH_KEYBOARD_LL hook. Must be created on a thread
/// with a message loop (the WPF UI thread). Raises <see cref="Pressed"/> when the
/// combo fires and <see cref="Released"/> when its keys are let go (push-to-talk).
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;
    private const int VK_LCONTROL = 0xA2;
    private const int VK_RCONTROL = 0xA3;
    private const int VK_SPACE = 0x20;
    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;
    private const int DoubleTapMs = 400;

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private readonly LowLevelKeyboardProc _proc; // kept alive so the GC doesn't collect the delegate
    private IntPtr _hook;
    private long _lastCtrlDownMs;
    private bool _otherKeySinceCtrl;
    private bool _comboHeld;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    // Written from the UI thread when settings change, read on the hook thread on every
    // keystroke — backed by volatile ints so the hook can't act on a stale mode.
    private volatile int _mode = (int)HotkeyMode.CtrlWin;
    private volatile int _activation = (int)ActivationMode.PushToTalk;

    public HotkeyMode Mode
    {
        get => (HotkeyMode)_mode;
        set => _mode = (int)value;
    }

    public ActivationMode Activation
    {
        get => (ActivationMode)_activation;
        set => _activation = (int)value;
    }

    /// <summary>Fired on the hook thread when the hotkey combo is pressed.</summary>
    public event Action? Pressed;

    /// <summary>
    /// Fired on the hook thread when the held combo is released.
    /// Only raised in <see cref="ActivationMode.PushToTalk"/>.
    /// </summary>
    public event Action? Released;

    public HotkeyManager()
    {
        _proc = HookCallback;
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, IntPtr.Zero, 0);
        if (_hook == IntPtr.Zero)
            throw new InvalidOperationException($"SetWindowsHookEx failed: {Marshal.GetLastWin32Error()}");
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            bool isDown = wParam == WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN;
            bool isUp = wParam == (IntPtr)WM_KEYUP || wParam == (IntPtr)WM_SYSKEYUP;
            bool isCtrl = info.vkCode is VK_LCONTROL or VK_RCONTROL;
            bool isWin = info.vkCode is VK_LWIN or VK_RWIN;

            if (isDown && HandleKeyDown(info.vkCode, isCtrl, isWin))
                return (IntPtr)1; // swallow

            if (isUp && _comboHeld && IsComboKey(info.vkCode, isCtrl, isWin))
            {
                _comboHeld = false;
                if (Activation == ActivationMode.PushToTalk)
                    Released?.Invoke();
                // Swallow the Win key-up too, otherwise the Start menu opens on release.
                if (Mode == HotkeyMode.CtrlWin && isWin)
                    return (IntPtr)1;
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    /// <summary>Returns true when the keystroke must be swallowed.</summary>
    private bool HandleKeyDown(uint vkCode, bool isCtrl, bool isWin)
    {
        switch (Mode)
        {
            case HotkeyMode.DoubleCtrl when isCtrl:
                long now = _clock.ElapsedMilliseconds;
                if (!_otherKeySinceCtrl && now - _lastCtrlDownMs <= DoubleTapMs && !_comboHeld)
                {
                    _lastCtrlDownMs = 0;
                    Fire();
                }
                else if (!_comboHeld)
                {
                    _lastCtrlDownMs = now;
                }
                _otherKeySinceCtrl = false;
                break;

            case HotkeyMode.DoubleCtrl:
                _otherKeySinceCtrl = true;
                break;

            case HotkeyMode.CtrlSpace when vkCode == VK_SPACE && IsCtrlDown():
                if (!_comboHeld) Fire();
                return true; // swallow Space

            case HotkeyMode.CtrlWin when isWin && IsCtrlDown():
                if (!_comboHeld) Fire();
                return true; // swallow Win so the Start menu stays closed
        }
        return false;
    }

    private void Fire()
    {
        _comboHeld = true;
        Pressed?.Invoke();
    }

    private bool IsComboKey(uint vkCode, bool isCtrl, bool isWin) => Mode switch
    {
        HotkeyMode.DoubleCtrl => isCtrl,
        HotkeyMode.CtrlSpace => isCtrl || vkCode == VK_SPACE,
        HotkeyMode.CtrlWin => isCtrl || isWin,
        _ => false,
    };

    private static bool IsCtrlDown() =>
        (GetAsyncKeyState(VK_LCONTROL) & 0x8000) != 0 || (GetAsyncKeyState(VK_RCONTROL) & 0x8000) != 0;

    public void Dispose()
    {
        Unhook();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Safety net: a leaked manager would otherwise keep a global keyboard hook
    /// installed for the rest of the process lifetime.
    /// </summary>
    ~HotkeyManager() => Unhook();

    private void Unhook()
    {
        if (_hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }
}
