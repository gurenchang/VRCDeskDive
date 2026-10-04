using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace VRCDeskDive.Core;

/// <summary>VRChat の起動状態と、前面にあるかどうかを監視する。</summary>
public sealed class VrcWatcher : IDisposable
{
    private const int ScanIntervalMs = 2000;

    private readonly Timer _timer;
    private volatile int[] _pids = [];
    // 0 なら初回の Tick ですぐに探す（以前は long.MinValue との引き算があふれて一度も探していなかった）
    private long _nextScan;
    private int _ticking;
    private volatile bool _isRunning;
    private volatile bool _isForeground;

    public bool IsRunning => _isRunning;

    /// <summary>直近の監視（200ms 間隔）での前面判定。UI の表示用。</summary>
    public bool IsForeground => _isForeground;

    /// <summary>今この瞬間に VRChat が前面か（入力フックなど、遅れが許されない判定用）。</summary>
    public bool IsForegroundNow => IsVrcWindow(Native.GetForegroundWindow());

    /// <summary>診断ログ用: 把握している VRChat のプロセス ID。</summary>
    public string DescribePids() => _pids.Length == 0 ? "none" : string.Join(",", _pids);

    /// <summary>ウィンドウが VRChat のものか（プロセス一覧は 2 秒ごとに更新したものを使う）。</summary>
    public bool IsVrcWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        Native.GetWindowThreadProcessId(hwnd, out var pid);
        return Array.IndexOf(_pids, (int)pid) >= 0;
    }

    /// <summary>状態が変わったとき（タイマースレッドから）通知される。</summary>
    public event Action? Changed;

    public VrcWatcher()
    {
        _timer = new Timer(Tick, null, 0, 200);
    }

    private void Tick(object? _)
    {
        if (Interlocked.Exchange(ref _ticking, 1) == 1) return;
        try
        {
            var now = Environment.TickCount64;
            if (now >= _nextScan)
            {
                _nextScan = now + ScanIntervalMs;
                var procs = Process.GetProcessesByName("VRChat");
                _pids = procs.Select(p => p.Id).ToArray();
                foreach (var p in procs) p.Dispose();
            }

            Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out var fgPid);
            var running = _pids.Length > 0;
            var foreground = running && _pids.Contains((int)fgPid);

            if (running != _isRunning || foreground != _isForeground)
            {
                _isRunning = running;
                _isForeground = foreground;
                Changed?.Invoke();
            }
        }
        finally
        {
            Volatile.Write(ref _ticking, 0);
        }
    }

    public void Dispose() => _timer.Dispose();
}
