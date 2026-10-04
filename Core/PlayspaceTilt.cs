using System;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Valve.VR;

namespace VRCDeskDive.Core;

public enum TiltState
{
    /// <summary>SteamVR に接続していない</summary>
    Disconnected,
    /// <summary>プレイスペースを操作中（上下視点が使える）</summary>
    Active,
}

/// <summary>
/// SteamVR のプレイスペース（Standing 原点）を HMD を中心に上下へ回転させ、
/// VR モードのまま視点の上下を変える。OpenVR の呼び出しはすべて同じスレッドから行うこと。
/// </summary>
/// <remarks>
/// アプリから見た HMD 姿勢は Standing^-1 * Raw。視点を R だけ回したいので
/// Standing' = Standing * (R をピボット中心で適用したものの逆) とする。
/// 変更は Live 設定に書き込まれるため、元の値をファイルに退避して必ず復元する。
/// </remarks>
public sealed class PlayspaceTilt : IDisposable
{
    public const float MaxPitch = 80f;

    private const int MinCommitIntervalMs = 33;

    private static readonly string BackupPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VRCDeskDive", "playspace_backup.json");

    private readonly TrackedDevicePose_t[] _poses = new TrackedDevicePose_t[OpenVR.k_unMaxTrackedDeviceCount];
    private bool _initialized;
    private Matrix4x4 _original;
    private float _appliedPitch;
    private long _lastCommit;

    public TiltState State { get; private set; } = TiltState.Disconnected;
    public bool IsActive => State == TiltState.Active;

    /// <summary>状態が変わったとき（呼び出し元スレッドから）通知される。</summary>
    public event Action? StateChanged;

    /// <summary>前回異常終了してプレイスペースが傾いたままなら元に戻す。</summary>
    public void RecoverIfNeeded()
    {
        if (!File.Exists(BackupPath) || !EnsureInit()) return;
        var backup = LoadBackup();
        if (backup is { } m && Commit(m)) File.Delete(BackupPath);
        Shutdown();
    }

    /// <summary>デスクトップ操作の開始時に呼ぶ。SteamVR 未起動なら false。</summary>
    public bool Begin()
    {
        if (IsActive) return true;
        if (!EnsureInit()) return false;

        var setup = OpenVR.ChaperoneSetup;
        if (setup is null) return false;

        // 前回の退避があればそれが本来の値（現在値は傾いている可能性がある）
        if (LoadBackup() is { } backup)
        {
            _original = backup;
        }
        else
        {
            setup.RevertWorkingCopy();
            var m = new HmdMatrix34_t();
            if (!setup.GetWorkingStandingZeroPoseToRawTrackingPose(ref m)) return false;
            _original = ToMatrix(m);
            SaveBackup(_original);
        }

        _appliedPitch = 0;
        SetState(TiltState.Active);
        return true;
    }

    /// <summary>視点の上下角（度、上が正）を反映する。頻度は内部で間引く。</summary>
    public void Update(float pitch)
    {
        if (!IsActive) return;
        if (Math.Abs(pitch - _appliedPitch) < 0.05f) return;
        var now = Environment.TickCount64;
        if (now - _lastCommit < MinCommitIntervalMs) return;

        if (!TryGetHeadInOriginalSpace(out var head)) return;

        // ピボット = 頭の位置、回転軸 = 頭の右方向を水平に投影したもの
        var pivot = head.Translation;
        var right = new Vector3(head.M11, 0, head.M13);
        right = right.LengthSquared() < 1e-6f ? Vector3.UnitX : Vector3.Normalize(right);

        var inverseTilt =
            Matrix4x4.CreateTranslation(-pivot) *
            Matrix4x4.CreateFromAxisAngle(right, -pitch * MathF.PI / 180f) *
            Matrix4x4.CreateTranslation(pivot);

        if (Commit(inverseTilt * _original))
        {
            _appliedPitch = pitch;
            _lastCommit = now;
        }
    }

    /// <summary>デスクトップ操作の終了時に呼ぶ。プレイスペースを元に戻す。</summary>
    public void End()
    {
        if (!IsActive) return;
        if (Commit(_original)) File.Delete(BackupPath);
        _appliedPitch = 0;
        SetState(TiltState.Disconnected);
        Shutdown();
    }

    /// <summary>SteamVR の終了通知を処理する。毎フレーム呼ぶ。</summary>
    public void PollEvents()
    {
        var system = _initialized ? OpenVR.System : null;
        if (system is null) return;

        var ev = new VREvent_t();
        var size = (uint)Marshal.SizeOf<VREvent_t>();
        while (system.PollNextEvent(ref ev, size))
        {
            if ((EVREventType)ev.eventType != EVREventType.VREvent_Quit) continue;

            // SteamVR が終了する前に元へ戻しておく
            if (IsActive && Commit(_original)) File.Delete(BackupPath);
            system.AcknowledgeQuit_Exiting();
            _appliedPitch = 0;
            SetState(TiltState.Disconnected);
            Shutdown();
            return;
        }
    }

    private bool TryGetHeadInOriginalSpace(out Matrix4x4 head)
    {
        head = default;
        var system = OpenVR.System;
        if (system is null) return false;

        system.GetDeviceToAbsoluteTrackingPose(ETrackingUniverseOrigin.TrackingUniverseRawAndUncalibrated, 0, _poses);
        var hmd = _poses[OpenVR.k_unTrackedDeviceIndex_Hmd];
        if (!hmd.bPoseIsValid) return false;
        if (!Matrix4x4.Invert(_original, out var rawToStanding)) return false;

        head = ToMatrix(hmd.mDeviceToAbsoluteTracking) * rawToStanding;
        return true;
    }

    private static bool Commit(Matrix4x4 standingToRaw)
    {
        var setup = OpenVR.ChaperoneSetup;
        if (setup is null) return false;
        setup.RevertWorkingCopy();
        var m = ToHmd(standingToRaw);
        setup.SetWorkingStandingZeroPoseToRawTrackingPose(ref m);
        return setup.CommitWorkingCopy(EChaperoneConfigFile.Live);
    }

    private bool EnsureInit()
    {
        if (_initialized) return true;
        var error = EVRInitError.None;
        // Background: SteamVR が起動していなければ失敗する（勝手に起動しない）
        OpenVR.Init(ref error, EVRApplicationType.VRApplication_Background);
        _initialized = error == EVRInitError.None;
        return _initialized;
    }

    private void Shutdown()
    {
        if (!_initialized) return;
        OpenVR.Shutdown();
        _initialized = false;
    }

    private void SetState(TiltState state)
    {
        if (State == state) return;
        State = state;
        StateChanged?.Invoke();
    }

    // HmdMatrix34_t は列ベクトル用の 3x4 行優先。System.Numerics は行ベクトル用なので転置して扱う。
    private static Matrix4x4 ToMatrix(HmdMatrix34_t m) => new(
        m.m0, m.m4, m.m8, 0,
        m.m1, m.m5, m.m9, 0,
        m.m2, m.m6, m.m10, 0,
        m.m3, m.m7, m.m11, 1);

    private static HmdMatrix34_t ToHmd(Matrix4x4 m) => new()
    {
        m0 = m.M11, m1 = m.M21, m2 = m.M31, m3 = m.M41,
        m4 = m.M12, m5 = m.M22, m6 = m.M32, m7 = m.M42,
        m8 = m.M13, m9 = m.M23, m10 = m.M33, m11 = m.M43,
    };

    private static void SaveBackup(Matrix4x4 m)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(BackupPath)!);
            float[] values = [m.M11, m.M12, m.M13, m.M21, m.M22, m.M23, m.M31, m.M32, m.M33, m.M41, m.M42, m.M43];
            File.WriteAllText(BackupPath, JsonSerializer.Serialize(values));
        }
        catch (Exception)
        {
        }
    }

    private static Matrix4x4? LoadBackup()
    {
        try
        {
            if (!File.Exists(BackupPath)) return null;
            var v = JsonSerializer.Deserialize<float[]>(File.ReadAllText(BackupPath));
            if (v is not { Length: 12 }) return null;
            return new Matrix4x4(v[0], v[1], v[2], 0, v[3], v[4], v[5], 0, v[6], v[7], v[8], 0, v[9], v[10], v[11], 1);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void Dispose()
    {
        End();
        Shutdown();
    }
}
