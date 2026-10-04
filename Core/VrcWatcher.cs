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
    private int[] _pids = [];
    private long _lastScan = long.MinValue;
    private int _ticking;
    private volatile bool _isRunning;
    private volatile bool _isForeground;

    public bool IsRunning => _isRunning;
    public bool IsForeground => _isForeground;

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
            if (now - _lastScan >= ScanIntervalMs)
            {
                _lastScan = now;
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
