using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace VRCDeskDive.Core;

/// <summary>
/// デスクトップ操作中のキーボード・マウス入力を VRChat の OSC 入力 (/input/*) に変換する。
/// フックは UI スレッドで登録し、OSC の送信は専用スレッドで約 60Hz で行う。
/// </summary>
public sealed class InputBridge : IDisposable
{
    private const int VK_RETURN = 0x0D;
    private const int VK_CONTROL = 0x11;
    private const int VK_MENU = 0x12;
    private const int VK_SPACE = 0x20;
    private const int VK_A = 0x41;
    private const int VK_D = 0x44;
    private const int VK_E = 0x45;
    private const int VK_Q = 0x51;
    private const int VK_S = 0x53;
    private const int VK_V = 0x56;
    private const int VK_W = 0x57;
    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;
    private const int VK_LSHIFT = 0xA0;
    private const int VK_RSHIFT = 0xA1;

    private static readonly HashSet<int> MappedKeys =
        [VK_W, VK_A, VK_S, VK_D, VK_Q, VK_E, VK_SPACE, VK_LSHIFT, VK_RSHIFT, VK_V, VK_RETURN];

    // マウス 1px/tick あたりの旋回量（感度 1.0 のとき）
    private const float MouseScale = 0.02f;
    // マウス 1px あたりの上下角（度、感度 1.0 のとき）
    private const float PitchScale = 0.15f;
    private const int TiltRetryMs = 3000;
    // スティックを最大まで倒したときの上下の回転速度（度/秒、感度 1.0 のとき）
    private const float StickPitchSpeed = 90f;

    private readonly OscClient _osc;
    private readonly VrcWatcher _vrc;
    private readonly AppSettings _settings;
    private readonly Native.LowLevelProc _keyboardProc;
    private readonly Native.LowLevelProc _mouseProc;
    private readonly HashSet<int> _down = [];
    private readonly object _lock = new();
    private readonly ConcurrentQueue<(string Address, int Value)> _events = new();
    private readonly Thread _loop;

    private IntPtr _keyboardHook;
    private IntPtr _mouseHook;
    private volatile bool _enabled;
    private volatile bool _disposed;
    private volatile bool _looking;
    private volatile bool _middleDown;
    private volatile bool _resetPitch;
    private Native.POINT _anchor;
    private int _accumDx;
    private int _accumDy;
    private volatile float _stickX;
    private volatile float _stickY;

    /// <summary>上下視点用のプレイスペース操作。OSC スレッドからのみ触る。</summary>
    public PlayspaceTilt Tilt { get; } = new();

    /// <summary>Enter が押されたとき（チャットボックス入力を開く）。フックスレッドから呼ばれる。</summary>
    public event Action? ChatRequested;

    public InputBridge(OscClient osc, VrcWatcher vrc, AppSettings settings)
    {
        _osc = osc;
        _vrc = vrc;
        _settings = settings;
        _keyboardProc = KeyboardProc;
        _mouseProc = MouseProc;
        _loop = new Thread(Loop) { IsBackground = true, Name = "VRCDeskDive OSC" };
        _loop.Start();
    }

    /// <summary>デスクトップ操作の有効/無効。UI スレッドから設定すること。</summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value) return;
            _enabled = value;
            if (value)
            {
                InstallHooks();
            }
            else
            {
                RemoveHooks();
                ReleaseAll();
            }
        }
    }

    /// <summary>
    /// UI の仮想スティックの傾き（-1〜1、y は下が正）。
    /// ツールの画面上で操作するため、VRChat が前面かどうかに関係なく反映する。
    /// </summary>
    public void SetStick(float x, float y)
    {
        _stickX = x;
        _stickY = y;
    }

    private bool IsActive => _enabled && (_vrc.IsForeground || !_settings.OnlyWhenVrcFocused);

    private void InstallHooks()
    {
        var module = Native.GetModuleHandle(null);
        _keyboardHook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _keyboardProc, module, 0);
        _mouseHook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _mouseProc, module, 0);
    }

    private void RemoveHooks()
    {
        if (_keyboardHook != IntPtr.Zero) Native.UnhookWindowsHookEx(_keyboardHook);
        if (_mouseHook != IntPtr.Zero) Native.UnhookWindowsHookEx(_mouseHook);
        _keyboardHook = _mouseHook = IntPtr.Zero;
    }

    private IntPtr KeyboardProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && _enabled)
        {
            var info = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
            var vk = (int)info.vkCode;
            var msg = (int)wParam;
            var isDown = msg is Native.WM_KEYDOWN or Native.WM_SYSKEYDOWN;
            if ((info.flags & Native.LLKHF_INJECTED) == 0 && MappedKeys.Contains(vk) && HandleKey(vk, isDown))
                return 1;
        }
        return Native.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }

    /// <returns>true ならキーを握りつぶす（VRChat 本体に渡さない）</returns>
    private bool HandleKey(int vk, bool isDown)
    {
        var foreground = _vrc.IsForeground;
        if (isDown)
        {
            if (!IsActive || IsModifierHeld()) return false;
            bool first;
            lock (_lock) first = _down.Add(vk);
            if (first) OnPress(vk);
            return foreground;
        }

        bool wasDown;
        lock (_lock) wasDown = _down.Remove(vk);
        if (!wasDown) return false;
        OnRelease(vk);
        return foreground;
    }

    private static bool IsModifierHeld() =>
        IsPressed(VK_CONTROL) || IsPressed(VK_MENU) || IsPressed(VK_LWIN) || IsPressed(VK_RWIN);

    private static bool IsPressed(int vk) => (Native.GetAsyncKeyState(vk) & 0x8000) != 0;

    private void OnPress(int vk)
    {
        switch (vk)
        {
            case VK_SPACE: _events.Enqueue(("/input/Jump", 1)); break;
            case VK_LSHIFT or VK_RSHIFT: _events.Enqueue(("/input/Run", 1)); break;
            case VK_V: _events.Enqueue(("/input/Voice", 1)); break;
            case VK_RETURN: ChatRequested?.Invoke(); break;
        }
    }

    private void OnRelease(int vk)
    {
        switch (vk)
        {
            case VK_SPACE: _events.Enqueue(("/input/Jump", 0)); break;
            case VK_LSHIFT or VK_RSHIFT:
                bool otherShift;
                lock (_lock) otherShift = _down.Contains(VK_LSHIFT) || _down.Contains(VK_RSHIFT);
                if (!otherShift) _events.Enqueue(("/input/Run", 0));
                break;
            case VK_V: _events.Enqueue(("/input/Voice", 0)); break;
        }
    }

    private IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && _enabled)
        {
            var info = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);
            if ((info.flags & Native.LLMHF_INJECTED) == 0)
            {
                switch ((int)wParam)
                {
                    // 右ドラッグ中はカーソルを止めて、移動量だけを視点の操作に使う
                    case Native.WM_RBUTTONDOWN when _vrc.IsForeground:
                        _anchor = info.pt;
                        _looking = true;
                        return 1;
                    case Native.WM_RBUTTONUP when _looking:
                        _looking = false;
                        return 1;
                    case Native.WM_MOUSEMOVE when _looking:
                        Interlocked.Add(ref _accumDx, info.pt.X - _anchor.X);
                        Interlocked.Add(ref _accumDy, info.pt.Y - _anchor.Y);
                        return 1;
                    // ホイールクリックで上下の視点を正面に戻す
                    case Native.WM_MBUTTONDOWN when _vrc.IsForeground:
                        _middleDown = true;
                        _resetPitch = true;
                        return 1;
                    case Native.WM_MBUTTONUP when _middleDown:
                        _middleDown = false;
                        return 1;
                }
            }
        }
        return Native.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private void ReleaseAll()
    {
        lock (_lock) _down.Clear();
        _looking = false;
        _middleDown = false;
        Interlocked.Exchange(ref _accumDx, 0);
        Interlocked.Exchange(ref _accumDy, 0);
        _events.Enqueue(("/input/Jump", 0));
        _events.Enqueue(("/input/Run", 0));
        _events.Enqueue(("/input/Voice", 0));
    }

    private void Loop()
    {
        float lastVertical = 0, lastHorizontal = 0, lastLook = 0, mouse = 0, pitch = 0;
        long nextTiltAttempt = 0;
        var wasActive = false;
        var clock = Stopwatch.StartNew();

        Tilt.RecoverIfNeeded();

        while (!_disposed)
        {
            var dt = (float)clock.Elapsed.TotalSeconds;
            clock.Restart();

            var active = IsActive;
            if (wasActive && !active) ReleaseAll();
            wasActive = active;

            // 上下の視点はプレイスペースの回転で表現する
            var dy = Interlocked.Exchange(ref _accumDy, 0);
            if (_enabled)
            {
                if (!Tilt.IsActive && Environment.TickCount64 >= nextTiltAttempt && !Tilt.Begin())
                    nextTiltAttempt = Environment.TickCount64 + TiltRetryMs;
                if (_resetPitch)
                {
                    _resetPitch = false;
                    pitch = 0;
                }
                var sign = _settings.InvertPitch ? 1f : -1f;
                var pitchDelta = active ? dy * PitchScale : 0f;
                // スティックは傾けている間、一定の速さで回し続ける
                pitchDelta += Curve(_stickY) * StickPitchSpeed * dt;
                if (pitchDelta != 0)
                {
                    pitch = Math.Clamp(pitch + sign * pitchDelta * (float)_settings.PitchSensitivity,
                        -PlayspaceTilt.MaxPitch, PlayspaceTilt.MaxPitch);
                }
                Tilt.Update(pitch);
            }
            else if (Tilt.IsActive)
            {
                Tilt.End();
                pitch = 0;
            }
            Tilt.PollEvents();

            float vertical = 0, horizontal = 0, turn = 0;
            var dx = Interlocked.Exchange(ref _accumDx, 0);
            if (active)
            {
                lock (_lock)
                {
                    vertical = Axis(VK_W, VK_S);
                    horizontal = Axis(VK_D, VK_A);
                    turn = Axis(VK_E, VK_Q);
                }
                var target = dx * MouseScale * (float)_settings.MouseSensitivity;
                mouse += (target - mouse) * 0.5f;
            }
            else
            {
                mouse = 0;
            }

            var stick = _enabled ? Curve(_stickX) : 0f;
            var look = Math.Clamp(mouse + turn * (float)_settings.KeyTurnSpeed + stick, -1f, 1f);
            if (Math.Abs(look) < 0.01f) look = 0;

            while (_events.TryDequeue(out var e)) _osc.Send(e.Address, e.Value);
            if (vertical != lastVertical) _osc.Send("/input/Vertical", lastVertical = vertical);
            if (horizontal != lastHorizontal) _osc.Send("/input/Horizontal", lastHorizontal = horizontal);
            if (look != lastLook) _osc.Send("/input/LookHorizontal", lastLook = look);

            Thread.Sleep(16);
        }

        Tilt.Dispose();
    }

    // 中心付近を細かく操作できるよう、傾きを二乗カーブにする
    private static float Curve(float v) => v * Math.Abs(v);

    private float Axis(int positive, int negative) =>
        (_down.Contains(positive) ? 1f : 0f) - (_down.Contains(negative) ? 1f : 0f);

    public void Dispose()
    {
        RemoveHooks();
        _disposed = true;
        // ループ終了時にプレイスペースを元へ戻すので待つ
        _loop.Join(2000);
        _osc.Send("/input/Vertical", 0f);
        _osc.Send("/input/Horizontal", 0f);
        _osc.Send("/input/LookHorizontal", 0f);
        _osc.Send("/input/Run", 0);
        _osc.Send("/input/Jump", 0);
        _osc.Send("/input/Voice", 0);
    }
}
