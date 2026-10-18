using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Satsuma.Core;

/// <summary>
/// Global low-level mouse + keyboard hooks. Detects "the user is dragging something and tapped a modifier key",
/// which is the gesture that summons the wheel. Hooks never swallow input — Satsuma only observes.
/// </summary>
public sealed class InputHooks : IDisposable
{
    readonly Native.HookProc _mouseProc;
    readonly Native.HookProc _keyProc;
    IntPtr _mouseHook, _keyHook;

    Native.POINT _downPt;
    bool _leftDown;

    TriggerKey _heldKey = TriggerKey.None;
    long _heldSince;
    bool _tapSpoiled;

    const int TapMaxMs = 420;

    /// <summary>True while the left button is held and the pointer moved past the system drag threshold.</summary>
    public bool IsDragging { get; private set; }

    public event Action<TriggerKey>? ModifierTapped;
    public event Action? EscapePressed;
    public event Action? LeftButtonReleased;

    public InputHooks()
    {
        _mouseProc = MouseProc;
        _keyProc = KeyProc;
    }

    public void Install()
    {
        var mod = Native.GetModuleHandle(null);
        _mouseHook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _mouseProc, mod, 0);
        _keyHook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _keyProc, mod, 0);
    }

    IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var info = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);
            switch ((int)wParam)
            {
                case Native.WM_LBUTTONDOWN:
                    _leftDown = true;
                    IsDragging = false;
                    _downPt = info.pt;
                    break;
                case Native.WM_MOUSEMOVE:
                    if (_leftDown && !IsDragging)
                    {
                        int tx = Native.GetSystemMetrics(Native.SM_CXDRAG) * 2;
                        int ty = Native.GetSystemMetrics(Native.SM_CYDRAG) * 2;
                        if (Math.Abs(info.pt.X - _downPt.X) > tx || Math.Abs(info.pt.Y - _downPt.Y) > ty)
                            IsDragging = true;
                    }
                    break;
                case Native.WM_LBUTTONUP:
                    _leftDown = false;
                    IsDragging = false;
                    LeftButtonReleased?.Invoke();
                    break;
            }
        }
        return Native.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    static TriggerKey KeyFromVk(uint vk) => vk switch
    {
        0x10 or 0xA0 or 0xA1 => TriggerKey.Shift,
        0x11 or 0xA2 or 0xA3 => TriggerKey.Ctrl,
        0x12 or 0xA4 or 0xA5 => TriggerKey.Alt,
        _ => TriggerKey.None,
    };

    IntPtr KeyProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var info = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
            int msg = (int)wParam;
            bool down = msg is Native.WM_KEYDOWN or Native.WM_SYSKEYDOWN;
            bool up = msg is Native.WM_KEYUP or Native.WM_SYSKEYUP;
            // Ignore only the F24 pulse Satsuma itself sends to claim focus; remappers (AutoHotkey, PowerToys) still work.
            bool injected = info.vkCode == 0x87;
            var key = KeyFromVk(info.vkCode);

            if (!injected)
            {
                if (key != TriggerKey.None)
                {
                    if (down && _heldKey != key)
                    {
                        // A second modifier while one is held means it's a chord, not a tap.
                        if (_heldKey != TriggerKey.None) _tapSpoiled = true;
                        else { _heldKey = key; _heldSince = Stopwatch.GetTimestamp(); _tapSpoiled = false; }
                    }
                    else if (up && _heldKey == key)
                    {
                        var ms = Stopwatch.GetElapsedTime(_heldSince).TotalMilliseconds;
                        var spoiled = _tapSpoiled;
                        _heldKey = TriggerKey.None;
                        if (!spoiled && ms <= TapMaxMs) ModifierTapped?.Invoke(key);
                    }
                }
                else if (down)
                {
                    _tapSpoiled = true;
                    if (info.vkCode == Native.VK_ESCAPE) EscapePressed?.Invoke();
                }
            }
        }
        return Native.CallNextHookEx(_keyHook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_mouseHook != IntPtr.Zero) Native.UnhookWindowsHookEx(_mouseHook);
        if (_keyHook != IntPtr.Zero) Native.UnhookWindowsHookEx(_keyHook);
        _mouseHook = _keyHook = IntPtr.Zero;
    }
}
