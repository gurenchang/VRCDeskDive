using System;
using System.Media;

namespace VRCDeskDive.Core;

public enum ControlMode
{
    VR,
    Desktop,
}

/// <summary>アプリ全体の状態（現在のモード）と各サービスをまとめる。</summary>
public sealed class AppController : IDisposable
{
    public AppSettings Settings { get; }
    public OscClient Osc { get; }
    public VrcWatcher Vrc { get; }
    public InputBridge Input { get; }

    public ControlMode Mode { get; private set; } = ControlMode.VR;

    /// <summary>モードが変わったとき（UI スレッドから）通知される。</summary>
    public event Action? ModeChanged;

    public AppController()
    {
        Settings = AppSettings.Load();
        Osc = new OscClient(Settings.OscHost, Settings.OscPort);
        Vrc = new VrcWatcher();
        Input = new InputBridge(Osc, Vrc, Settings);
    }

    public void Toggle() => SetMode(Mode == ControlMode.VR ? ControlMode.Desktop : ControlMode.VR);

    public void SetMode(ControlMode mode)
    {
        if (Mode == mode) return;
        Mode = mode;
        Input.Enabled = mode == ControlMode.Desktop;
        if (Settings.PlaySound)
            (mode == ControlMode.Desktop ? SystemSounds.Asterisk : SystemSounds.Beep).Play();
        ModeChanged?.Invoke();
    }

    public void Dispose()
    {
        Input.Dispose();
        Vrc.Dispose();
        Osc.Dispose();
        Settings.Save();
    }
}
