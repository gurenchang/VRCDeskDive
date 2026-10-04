using System;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;

namespace VRCDeskDive.Core;

public enum DriverStatus
{
    /// <summary>SteamVR が起動していない</summary>
    SteamVrNotRunning,
    /// <summary>SteamVR は起動しているがドライバーが読み込まれていない（未登録・再起動待ち）</summary>
    NotLoaded,
    /// <summary>ドライバーは動いているが HMD の姿勢をフックできなかった</summary>
    HookFailed,
    /// <summary>ドライバーは動いているが HMD の姿勢が届いていない</summary>
    WaitingForHmd,
    /// <summary>接続済み。視点の固定と上下操作が使える</summary>
    Connected,
}

/// <summary>
/// SteamVR ドライバーとの共有メモリ（driver/src/shared_state.h）を読み書きする。
/// OSC スレッドから毎 tick Update を呼ぶこと。
/// </summary>
public sealed class DriverLink : IDisposable
{
    private const string MapName = "Local\\VRCDeskDive.Driver";
    private const uint Magic = 0x44444356;
    private const uint Version = 1;
    private const int Size = 48;
    private const int StaleMs = 2000;

    // shared_state.h のオフセット
    private const int OffMagic = 0;
    private const int OffVersion = 4;
    private const int OffDriverHeartbeat = 8;
    private const int OffHookedCount = 16;
    private const int OffHmdPoseCount = 20;
    private const int OffAppHeartbeat = 24;
    private const int OffEnabled = 32;
    private const int OffPitch = 36;
    private const int OffLockPosition = 40;

    private MemoryMappedFile? _map;
    private MemoryMappedViewAccessor? _view;
    private long _nextOpenAttempt;
    private long _nextSteamVrCheck;
    private bool _steamVrRunning;
    private uint _lastPoseCount;
    private long _lastPoseChange;

    public DriverStatus Status { get; private set; } = DriverStatus.SteamVrNotRunning;

    /// <summary>状態が変わったとき（OSC スレッドから）通知される。</summary>
    public event Action? StatusChanged;

    public void Update(bool enabled, float pitchDeg, bool lockPosition)
    {
        var now = Environment.TickCount64;
        if (now >= _nextSteamVrCheck)
        {
            _nextSteamVrCheck = now + 2000;
            var procs = Process.GetProcessesByName("vrserver");
            _steamVrRunning = procs.Length > 0;
            foreach (var p in procs) p.Dispose();
        }

        if (!_steamVrRunning)
        {
            SetStatus(DriverStatus.SteamVrNotRunning);
            return;
        }
        if (!EnsureOpen(now))
        {
            SetStatus(DriverStatus.NotLoaded);
            return;
        }

        var view = _view!;
        // Environment.TickCount64 は GetTickCount64 と同じ時計なのでドライバー側と比較できる
        view.Write(OffAppHeartbeat, now);
        view.Write(OffEnabled, enabled ? 1 : 0);
        view.Write(OffPitch, pitchDeg);
        view.Write(OffLockPosition, lockPosition ? 1 : 0);

        var driverHeartbeat = view.ReadInt64(OffDriverHeartbeat);
        var poseCount = view.ReadUInt32(OffHmdPoseCount);
        if (poseCount != _lastPoseCount)
        {
            _lastPoseCount = poseCount;
            _lastPoseChange = now;
        }

        if (now - driverHeartbeat > StaleMs) SetStatus(DriverStatus.NotLoaded);
        else if (view.ReadUInt32(OffHookedCount) == 0) SetStatus(DriverStatus.HookFailed);
        else if (now - _lastPoseChange > StaleMs) SetStatus(DriverStatus.WaitingForHmd);
        else SetStatus(DriverStatus.Connected);
    }

    private bool EnsureOpen(long now)
    {
        if (_view is not null) return true;
        if (now < _nextOpenAttempt) return false;
        _nextOpenAttempt = now + 2000;

        try
        {
            _map = MemoryMappedFile.OpenExisting(MapName, MemoryMappedFileRights.ReadWrite);
            _view = _map.CreateViewAccessor(0, Size);
            if (_view.ReadUInt32(OffMagic) == Magic && _view.ReadUInt32(OffVersion) == Version) return true;
        }
        catch (Exception)
        {
            // ドライバーが未起動ならまだ存在しない
        }
        Close();
        return false;
    }

    private void SetStatus(DriverStatus status)
    {
        if (Status == status) return;
        Status = status;
        StatusChanged?.Invoke();
    }

    private void Close()
    {
        _view?.Dispose();
        _map?.Dispose();
        _view = null;
        _map = null;
    }

    public void Dispose()
    {
        // 終了時は確実に固定を解除しておく（ドライバー側もハートビート切れで解除する）
        try
        {
            _view?.Write(OffEnabled, 0);
        }
        catch (Exception)
        {
        }
        Close();
    }
}
