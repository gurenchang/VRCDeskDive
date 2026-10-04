using System;
using System.Collections.Generic;
using System.Windows.Input;
using System.Windows.Interop;

namespace VRCDeskDive.Core;

/// <summary>グローバルホットキー（RegisterHotKey）の登録と通知。</summary>
public sealed class HotkeyService : IDisposable
{
    private const int HotkeyId = 0xD1;

    private readonly IntPtr _hwnd;
    private readonly HwndSource _source;

    public bool IsRegistered { get; private set; }

    public event Action? Pressed;

    public HotkeyService(IntPtr hwnd)
    {
        _hwnd = hwnd;
        _source = HwndSource.FromHwnd(hwnd);
        _source.AddHook(WndProc);
    }

    public bool Register(string gesture)
    {
        Unregister();
        if (!TryParse(gesture, out var mods, out var vk)) return false;
        IsRegistered = Native.RegisterHotKey(_hwnd, HotkeyId, mods | Native.MOD_NOREPEAT, vk);
        return IsRegistered;
    }

    public void Unregister()
    {
        if (!IsRegistered) return;
        Native.UnregisterHotKey(_hwnd, HotkeyId);
        IsRegistered = false;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            Pressed?.Invoke();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public static string Format(ModifierKeys modifiers, Key key)
    {
        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(key.ToString());
        return string.Join("+", parts);
    }

    public static bool TryParse(string gesture, out uint modifiers, out uint vk)
    {
        modifiers = 0;
        vk = 0;
        Key? key = null;
        foreach (var raw in gesture.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl": modifiers |= Native.MOD_CONTROL; break;
                case "alt": modifiers |= Native.MOD_ALT; break;
                case "shift": modifiers |= Native.MOD_SHIFT; break;
                case "win": modifiers |= Native.MOD_WIN; break;
                default:
                    if (!Enum.TryParse<Key>(raw, true, out var parsed)) return false;
                    key = parsed;
                    break;
            }
        }
        if (key is null) return false;
        vk = (uint)KeyInterop.VirtualKeyFromKey(key.Value);
        return vk != 0;
    }

    public void Dispose()
    {
        Unregister();
        _source.RemoveHook(WndProc);
    }
}
