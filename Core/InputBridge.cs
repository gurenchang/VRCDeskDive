using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace VRCDeskDive.Core;

/// <summary>
/// デスクトップ操作中のキーボード・マウス入力を、VRChat の OSC 入力 (/input/*) と
/// SteamVR ドライバーの視点（左右・上下）に変換する。
/// フックは専用スレッドで登録し、OSC の送信とドライバーの更新は専用スレッドで約 250Hz で行う。
/// </summary>
public sealed class InputBridge : IDisposable
{
    private const int VK_TAB = 0x09;
    private const int VK_RETURN = 0x0D;
    private const int VK_CONTROL = 0x11;
    private const int VK_MENU = 0x12;
    private const int VK_SPACE = 0x20;
    private const int VK_A = 0x41;
    private const int VK_D = 0x44;
    private const int VK_S = 0x53;
    private const int VK_V = 0x56;
    private const int VK_W = 0x57;
    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;
    private const int VK_LSHIFT = 0xA0;
    private const int VK_RSHIFT = 0xA1;
    private const int VK_LCONTROL = 0xA2;
    private const int VK_RCONTROL = 0xA3;
    private const int VK_LMENU = 0xA4;
    private const int VK_RMENU = 0xA5;

    private static readonly HashSet<int> MappedKeys =
        [VK_W, VK_A, VK_S, VK_D, VK_SPACE, VK_LSHIFT, VK_RSHIFT, VK_V, VK_RETURN];

    // マウス 1px あたりの視点の角度（度、感度 1.0 のとき）
    private const float LookDegPerPx = 0.15f;
    // 上下の視点の上限（度）
    private const float MaxPitch = 85f;
    // パッドのスティックを最大まで倒したときの回転速度（度/秒、感度 1.0 のとき）
    private const float StickPitchSpeed = 90f;
    private const float StickYawSpeed = 120f;
    // OSC 送信とドライバー更新の間隔（ミリ秒）。マウスに追従させるため短めにする
    private const int LoopIntervalMs = 4;
    // 前面が操作対象から外れたとみなすまでの猶予（ミリ秒）
    private const int FocusGraceMs = 150;

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
    private Thread? _hookThread;
    private uint _hookThreadId;
    private volatile bool _enabled;
    private volatile bool _disposed;
    private volatile bool _middleDown;
    private volatile bool _leftDown;
    private volatile bool _mouseLook;
    private long _mouseLookSince;
    private volatile bool _resetPitch;
    private Native.POINT _anchor;
    private int _accumDx;
    private int _accumDy;
    private volatile float _stickX;
    private volatile float _stickY;

    /// <summary>HMD の姿勢を固定・回転させる SteamVR ドライバーとの接続。OSC スレッドからのみ更新する。</summary>
    public DriverLink Driver { get; } = new();

    /// <summary>Enter が押されたとき（チャットボックス入力を開く）。フックスレッドから呼ばれる。</summary>
    public event Action? ChatRequested;

    /// <summary>マウスルック中（デスクトップ版のように、マウスの動きがそのまま視点になる）。</summary>
    public bool IsMouseLook => _mouseLook;

    /// <summary>マウスルックの開始・解除（フックか OSC スレッドから）。</summary>
    public event Action? MouseLookChanged;

    private void SetMouseLook(bool value, string reason)
    {
        if (_mouseLook == value) return;
        _mouseLook = value;
        DebugLog.Write($"mouse look {(value ? "ON" : "OFF")} ({reason})");
        MouseLookChanged?.Invoke();
    }

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
            DebugLog.Write($"desktop mode {(value ? "ON" : "OFF")} clickToMouseLook={_settings.ClickToMouseLook}");
            if (value)
            {
                InstallHooks();
            }
            else
            {
                RemoveHooks();
                ReleaseAll("desktop mode off");
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

    /// <summary>上下の視点を水平に戻す（ホイール押し・パッドの中クリック）。</summary>
    public void ResetPitch() => _resetPitch = true;

    /// <summary>VRCDeskDive のメインウィンドウ。前面にあるときも VRChat と同じく操作を受け付ける。</summary>
    public IntPtr OwnWindow { get; set; }

    private bool IsOwnWindowForeground =>
        OwnWindow != IntPtr.Zero && Native.GetForegroundWindow() == OwnWindow;

    /// <summary>キー入力を VRChat の操作として扱う対象（VRChat かメインウィンドウ）が前面にあるか。</summary>
    private bool IsInputTargetForeground => _vrc.IsForegroundNow || IsOwnWindowForeground;

    private bool IsActive => _enabled && IsInputTargetForeground;

    /// <summary>
    /// 入力フックは専用スレッドで動かす。UI スレッドで動かすと、画面の処理が一瞬詰まっただけで
    /// Windows がフックを（通知なしに）外してしまい、マウス操作が効かなくなることがあるため。
    /// </summary>
    private void InstallHooks()
    {
        if (_hookThread is not null) return;
        using var ready = new ManualResetEventSlim();
        _hookThread = new Thread(() =>
        {
            _hookThreadId = Native.GetCurrentThreadId();
            var module = Native.GetModuleHandle(null);
            _keyboardHook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _keyboardProc, module, 0);
            var keyboardError = Marshal.GetLastWin32Error();
            _mouseHook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _mouseProc, module, 0);
            var mouseError = Marshal.GetLastWin32Error();
            DebugLog.Write($"hooks installed: keyboard={_keyboardHook} (err {keyboardError}) mouse={_mouseHook} (err {mouseError})");
            ready.Set();

            // 低レベルフックはこのスレッドのメッセージループ中に呼ばれる
            while (Native.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                Native.TranslateMessage(ref msg);
                Native.DispatchMessage(ref msg);
            }

            if (_keyboardHook != IntPtr.Zero) Native.UnhookWindowsHookEx(_keyboardHook);
            if (_mouseHook != IntPtr.Zero) Native.UnhookWindowsHookEx(_mouseHook);
            _keyboardHook = _mouseHook = IntPtr.Zero;
            DebugLog.Write("hooks removed");
        })
        {
            IsBackground = true,
            Name = "VRCDeskDive Input Hooks",
            Priority = ThreadPriority.Highest,
        };
        _hookThread.Start();
        ready.Wait(2000);
    }

    private void RemoveHooks()
    {
        if (_hookThread is null) return;
        Native.PostThreadMessage(_hookThreadId, Native.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _hookThread.Join(2000);
        _hookThread = null;
    }

    private IntPtr KeyboardProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && _enabled)
        {
            var info = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
            var vk = (int)info.vkCode;
            var msg = (int)wParam;
            var isDown = msg is Native.WM_KEYDOWN or Native.WM_SYSKEYDOWN;
            if ((info.flags & Native.LLKHF_INJECTED) == 0)
            {
                // 解除キーはそのまま VRChat / Windows にも渡す（Alt+Tab などを邪魔しない）
                if (isDown && _mouseLook && IsMouseLookReleaseKey(vk)) SetMouseLook(false, $"release key vk=0x{vk:X2}");
                if (MappedKeys.Contains(vk) && HandleKey(vk, isDown)) return 1;
            }
        }
        return Native.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }

    /// <returns>true ならキーを握りつぶす（VRChat 本体に渡さない）</returns>
    private bool HandleKey(int vk, bool isDown)
    {
        // 前面が VRChat かメインウィンドウなら、キーはそちらに渡さず OSC だけに使う
        // （メインウィンドウに渡すと Space でボタンが押されるなどしてしまう）
        if (isDown)
        {
            if (!IsActive || IsModifierHeld()) return false;
            bool first;
            lock (_lock) first = _down.Add(vk);
            if (first) OnPress(vk);
            return true;
        }

        bool wasDown;
        lock (_lock) wasDown = _down.Remove(vk);
        if (!wasDown) return false;
        OnRelease(vk);
        return IsInputTargetForeground;
    }

    /// <summary>マウスルックを解除するキーとして選べるもの（設定の値 → 仮想キー）。</summary>
    public static readonly IReadOnlyDictionary<string, int[]> MouseLookReleaseKeys = new Dictionary<string, int[]>
    {
        ["Alt"] = [VK_LMENU, VK_RMENU],
        ["Tab"] = [VK_TAB],
        ["Ctrl"] = [VK_LCONTROL, VK_RCONTROL],
    };

    private bool IsMouseLookReleaseKey(int vk) =>
        MouseLookReleaseKeys.TryGetValue(_settings.MouseLookReleaseKey, out var keys)
            ? Array.IndexOf(keys, vk) >= 0
            : vk is VK_LMENU or VK_RMENU;

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
                    // VRChat を左クリックするとマウスルック開始（解除キーか、VRChat から離れると解除）
                    case Native.WM_LBUTTONDOWN when _mouseLook || _settings.ClickToMouseLook:
                    {
                        var foreground = _vrc.IsForegroundNow;
                        var atVrc = IsVrcClientAreaAt(info.pt);
                        DebugLog.Write($"left down: vrcForeground={foreground} vrcClientUnderCursor={atVrc} mouseLook={_mouseLook} vrcPids={_vrc.DescribePids()}");
                        // VRChat の画面の中をクリックしたときだけ（ほかのウィンドウやタイトルバーは除く）
                        if (!atVrc) break;
                        if (!_mouseLook) _anchor = info.pt;
                        _mouseLookSince = Environment.TickCount64;
                        SetMouseLook(true, foreground ? "click on foreground VRChat" : "click activating VRChat");
                        // まだ前面でなければ、このクリックは VRChat を前面にするためにそのまま渡す
                        if (!foreground) break;
                        _leftDown = true;
                        return 1;
                    }
                    case Native.WM_LBUTTONUP when _leftDown:
                        _leftDown = false;
                        return 1;
                    // マウスルック中はカーソルを止めて、移動量だけを視点の操作に使う
                    case Native.WM_MOUSEMOVE when _mouseLook:
                        Interlocked.Add(ref _accumDx, info.pt.X - _anchor.X);
                        Interlocked.Add(ref _accumDy, info.pt.Y - _anchor.Y);
                        return 1;
                    // ホイールクリックで上下の視点を水平に戻す
                    case Native.WM_MBUTTONDOWN when _vrc.IsForegroundNow:
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

    /// <summary>
    /// カーソルの下が VRChat のウィンドウの描画領域か。
    /// 手前に別のウィンドウが重なっていれば false、タイトルバーや枠（ウィンドウの移動・リサイズ）も false。
    /// </summary>
    private bool IsVrcClientAreaAt(Native.POINT pt)
    {
        var root = Native.GetAncestor(Native.WindowFromPoint(pt), Native.GA_ROOT);
        if (!_vrc.IsVrcWindow(root) || !Native.GetClientRect(root, out var client)) return false;
        var origin = new Native.POINT();
        if (!Native.ClientToScreen(root, ref origin)) return false;
        return pt.X >= origin.X && pt.X < origin.X + client.Right
            && pt.Y >= origin.Y && pt.Y < origin.Y + client.Bottom;
    }

    private void ReleaseAll(string reason)
    {
        DebugLog.Write($"release all ({reason})");
        lock (_lock) _down.Clear();
        _middleDown = false;
        _leftDown = false;
        SetMouseLook(false, reason);
        Interlocked.Exchange(ref _accumDx, 0);
        Interlocked.Exchange(ref _accumDy, 0);
        _events.Enqueue(("/input/Jump", 0));
        _events.Enqueue(("/input/Run", 0));
        _events.Enqueue(("/input/Voice", 0));
    }

    private void Loop()
    {
        float lastVertical = 0, lastHorizontal = 0, pitch = 0, yaw = 0;
        var wasActive = false;
        long inactiveSince = 0, vrcLostSince = 0;
        var clock = Stopwatch.StartNew();
        // Sleep を短い間隔で回せるよう、このプロセスのタイマー分解能を 1ms にする
        Native.timeBeginPeriod(1);

        while (!_disposed)
        {
            var dt = Math.Max((float)clock.Elapsed.TotalSeconds, 0.0005f);
            clock.Restart();
            var (stickX, stickY) = CurveStick(_stickX, _stickY);

            // ウィンドウを切り替える瞬間は前面が一瞬「なし」になることがあるので、
            // 操作対象から外れた状態が FocusGraceMs 続いたときだけ「離れた」とみなす
            var now = Environment.TickCount64;
            var active = IsActive;
            if (active)
            {
                inactiveSince = 0;
                wasActive = true;
            }
            else if (wasActive)
            {
                if (inactiveSince == 0) inactiveSince = now;
                if (now - inactiveSince >= FocusGraceMs)
                {
                    wasActive = false;
                    ReleaseAll($"focus left input target, foreground=0x{Native.GetForegroundWindow():X}");
                }
            }

            // マウスルックは VRChat から離れたら解除（開始直後はクリックで前面になるのを待つ）
            if (!_mouseLook || _vrc.IsForegroundNow || now - _mouseLookSince < 500)
            {
                vrcLostSince = 0;
            }
            else
            {
                if (vrcLostSince == 0) vrcLostSince = now;
                if (now - vrcLostSince >= FocusGraceMs)
                    SetMouseLook(false, $"VRChat lost focus, foreground=0x{Native.GetForegroundWindow():X}");
            }

            // 視点はドライバーが HMD の姿勢に反映する。マウスの移動量がそのまま角度になる（デスクトップ版と同じ感覚）
            var dx = Interlocked.Exchange(ref _accumDx, 0);
            var dy = Interlocked.Exchange(ref _accumDy, 0);
            if (_enabled)
            {
                if (_resetPitch)
                {
                    _resetPitch = false;
                    pitch = 0;
                }
                var sensitivity = (float)_settings.MouseSensitivity;
                // スティックは傾けている間、一定の速さで回し続ける
                var yawDelta = (active ? dx * LookDegPerPx : 0f) + stickX * StickYawSpeed * dt;
                var pitchDelta = (active ? dy * LookDegPerPx : 0f) + stickY * StickPitchSpeed * dt;
                yaw = NormalizeAngle(yaw + yawDelta * sensitivity);
                // 画面の Y は下が正なので、上下角（上が正）には逆向きに足す
                pitch = Math.Clamp(pitch - pitchDelta * sensitivity, -MaxPitch, MaxPitch);
            }
            else
            {
                // 次にデスクトップ操作へ切り替えたときは、切り替えた時の向きの水平から始める
                pitch = 0;
                yaw = 0;
                _resetPitch = false;
            }
            // 切り替えた時の頭の位置は常に保つ（HMD を机に置いても視点の高さが変わらない）
            Driver.Update(_enabled, yaw, pitch, lockPosition: true);

            float vertical = 0, horizontal = 0;
            if (active)
            {
                lock (_lock)
                {
                    vertical = Axis(VK_W, VK_S);
                    horizontal = Axis(VK_D, VK_A);
                }
            }

            while (_events.TryDequeue(out var e)) _osc.Send(e.Address, e.Value);
            if (vertical != lastVertical) _osc.Send("/input/Vertical", lastVertical = vertical);
            if (horizontal != lastHorizontal) _osc.Send("/input/Horizontal", lastHorizontal = horizontal);

            DebugLog.Flush();
            Thread.Sleep(LoopIntervalMs);
        }

        Native.timeEndPeriod(1);
        Driver.Dispose();
    }

    private static float NormalizeAngle(float deg)
    {
        deg %= 360f;
        if (deg > 180f) deg -= 360f;
        if (deg < -180f) deg += 360f;
        return deg;
    }

    // 中心付近を細かく操作できるよう、傾きを二乗カーブにする
    // 縦横別々に二乗すると斜めに倒したときの弱い方向が潰れるので、倒した量全体に掛けて方向を保つ
    private static (float X, float Y) CurveStick(float x, float y)
    {
        var magnitude = Math.Max(Math.Abs(x), Math.Abs(y));
        return (x * magnitude, y * magnitude);
    }

    private float Axis(int positive, int negative) =>
        (_down.Contains(positive) ? 1f : 0f) - (_down.Contains(negative) ? 1f : 0f);

    public void Dispose()
    {
        RemoveHooks();
        _disposed = true;
        // ループ終了時にドライバーの固定を解除するので待つ
        _loop.Join(2000);
        _osc.Send("/input/Vertical", 0f);
        _osc.Send("/input/Horizontal", 0f);
        _osc.Send("/input/Run", 0);
        _osc.Send("/input/Jump", 0);
        _osc.Send("/input/Voice", 0);
    }
}
