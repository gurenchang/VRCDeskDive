using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using VRCDeskDive.Core;

namespace VRCDeskDive;

public partial class MainWindow : Window
{
    private readonly AppController _controller;
    private readonly bool _initialized;
    private bool _compact;

    public MainWindow(AppController controller)
    {
        _controller = controller;
        InitializeComponent();
        // 画面に収まらないときは ScrollViewer でスクロールさせる
        MaxHeight = SystemParameters.WorkArea.Height;

        var s = controller.Settings;
        SensitivitySlider.Value = s.MouseSensitivity;
        ClickLookCheck.IsChecked = s.ClickToMouseLook;
        ReleaseKeyCombo.ItemsSource = InputBridge.MouseLookReleaseKeys.Keys;
        if (!InputBridge.MouseLookReleaseKeys.ContainsKey(s.MouseLookReleaseKey)) s.MouseLookReleaseKey = "Alt";
        ReleaseKeyCombo.SelectedItem = s.MouseLookReleaseKey;
        ReleaseKeyCap.Text = s.MouseLookReleaseKey;
        SoundCheck.IsChecked = s.PlaySound;
        OscText.Text = controller.Osc.Target;
        TopmostButton.IsChecked = Topmost = s.AlwaysOnTop;
        CompactButton.IsChecked = s.CompactMode;
        ApplyCompact(s.CompactMode);
        UpdateSliderLabel();
        _initialized = true;

        LookPad.StickChanged += controller.Input.SetStick;
        LookPad.ResetRequested += controller.Input.ResetPitch;
        controller.ModeChanged += UpdateMode;
        controller.Vrc.Changed += () => Dispatcher.BeginInvoke(UpdateVrcStatus);
        controller.Input.Driver.StatusChanged += () => Dispatcher.BeginInvoke(UpdateDriverStatus);
        controller.Input.MouseLookChanged += () => Dispatcher.BeginInvoke(UpdateMode);
        UpdateMode();
        UpdateVrcStatus();
        UpdateDriverStatus();
    }

    // --- 縮小表示・最前面 ---

    private void TopmostButton_Click(object sender, RoutedEventArgs e)
    {
        Topmost = _controller.Settings.AlwaysOnTop = TopmostButton.IsChecked == true;
        _controller.Settings.Save();
    }

    private void CompactButton_Click(object sender, RoutedEventArgs e)
    {
        _controller.Settings.CompactMode = CompactButton.IsChecked == true;
        _controller.Settings.Save();
        ApplyCompact(_controller.Settings.CompactMode);
    }

    /// <summary>縮小表示では、モード切り替え・視点パッド・簡易状態だけを残す。</summary>
    private void ApplyCompact(bool compact)
    {
        _compact = compact;
        var full = compact ? Visibility.Collapsed : Visibility.Visible;

        LeftColumn.Width = compact ? new GridLength(1, GridUnitType.Star) : new GridLength(400);
        SpacerColumn.Width = new GridLength(compact ? 0 : 24);
        RightColumn.Width = compact ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        RightPanel.Visibility = full;
        Subtitle.Visibility = full;
        ModeLabel.Visibility = full;
        ModeHint.Visibility = full;
        StatusGrid.Visibility = full;
        CompactStatus.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;

        RootGrid.Margin = compact ? new Thickness(12, 8, 12, 12) : new Thickness(22, 18, 22, 22);
        TitleText.FontSize = compact ? 15 : 22;
        ModeCard.Padding = compact ? new Thickness(12, 10, 12, 12) : new Thickness(18, 14, 18, 14);
        ModeText.FontSize = compact ? 20 : 26;
        ModeText.Margin = compact ? new Thickness(0, 0, 0, 8) : new Thickness(0, 2, 0, 2);
        Width = compact ? 330 : 860;

        CompactButton.Content = compact ? "" : "";
        CompactButton.ToolTip = compact ? "元の大きさに戻す" : "縮小表示";
        if (_initialized) UpdateDriverStatus();
    }

    // --- 状態表示 ---

    private void UpdateMode()
    {
        var desktop = _controller.Mode == ControlMode.Desktop;
        var accent = (Brush)FindResource(desktop ? "DesktopBrush" : "VrBrush");
        ModeText.Text = desktop ? "デスクトップ操作" : "VR操作";
        ModeText.Foreground = accent;
        ModeCard.BorderBrush = accent;
        ModeHint.Text = (desktop, _controller.Input.IsMouseLook) switch
        {
            (true, true) => $"マウスルック中（{_controller.Settings.MouseLookReleaseKey}で解除）",
            (true, false) => "HMDを外して、キーボードとマウスで操作できます",
            _ => "いつもどおりHMDとコントローラーで操作します",
        };
        // 縮小表示でもマウスルック中だけは分かるようにする
        if (desktop && _controller.Input.IsMouseLook && _compact) ModeText.Text = "マウスルック中";
        ToggleButton.Content = desktop ? "VR操作に戻す" : "デスクトップ操作に切り替え";
        ToggleButton.Background = (Brush)FindResource(desktop ? "VrBrush" : "DesktopBrush");
        Title = desktop ? "VRCDeskDive - デスクトップ操作中" : "VRCDeskDive";
        LookPad.IsEnabled = desktop;
    }

    private void UpdateDriverStatus()
    {
        var status = _controller.Input.Driver.Status;
        var steamVr = status != DriverStatus.SteamVrNotRunning;
        SteamVrText.Text = steamVr ? "接続" : "未起動";
        SteamVrDot.Fill = steamVr ? Brushes.LimeGreen : Brushes.Gray;

        (DriverText.Text, DriverDot.Fill) = status switch
        {
            DriverStatus.Connected => ("接続", Brushes.LimeGreen),
            DriverStatus.WaitingForHmd => ("接続（HMDの姿勢待ち）", (Brush)Brushes.Gold),
            DriverStatus.HookFailed => ("エラー（HMDの姿勢を取得できません）", (Brush)Brushes.OrangeRed),
            DriverStatus.NotLoaded => ("未接続", (Brush)Brushes.Gold),
            DriverStatus.VersionMismatch => ("更新あり（SteamVRの再起動が必要）", (Brush)Brushes.Gold),
            _ => ("未接続", (Brush)Brushes.Gray),
        };
        CompactDriverDot.Fill = DriverDot.Fill;

        var registered = DriverInstaller.IsRegistered();
        var showRegister = !registered && DriverInstaller.IsBundled;
        RegisterButton.Visibility = showRegister ? Visibility.Visible : Visibility.Collapsed;

        string? hint = null;
        if (!DriverInstaller.IsBundled)
            hint = "ドライバーがビルドされていません（driver\\build.ps1）。視点の操作と水平固定は使えません。";
        else if (!registered)
            hint = "「SteamVRに登録」を押してから、SteamVRを再起動してください。";
        else if (status == DriverStatus.NotLoaded)
            hint = "登録済みです。SteamVRを再起動すると読み込まれます。";
        else if (status == DriverStatus.VersionMismatch)
            hint = "古いドライバーが読み込まれています。SteamVRを再起動すると新しいドライバーになります。";
        DriverHint.Text = hint ?? string.Empty;
        DriverHint.Visibility = hint is null || _compact ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RegisterButton_Click(object sender, RoutedEventArgs e)
    {
        if (!DriverInstaller.Register())
        {
            MessageBox.Show(this, "ドライバーを登録できませんでした。SteamVRがインストールされているか確認してください。", "VRCDeskDive");
            return;
        }
        UpdateDriverStatus();
        MessageBox.Show(this, "SteamVRにドライバーを登録しました。SteamVRを再起動すると有効になります。", "VRCDeskDive");
    }

    private void UpdateVrcStatus()
    {
        var vrc = _controller.Vrc;
        (VrcText.Text, VrcDot.Fill) = (vrc.IsRunning, vrc.IsForeground) switch
        {
            (true, true) => ("起動中（前面）", Brushes.LimeGreen),
            (true, false) => ("起動中", (Brush)Brushes.Gold),
            _ => ("未検出", (Brush)Brushes.Gray),
        };
        CompactVrcDot.Fill = VrcDot.Fill;
    }

    private void ToggleButton_Click(object sender, RoutedEventArgs e) => _controller.Toggle();

    // --- 設定 ---

    private void SensitivitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_initialized) return;
        _controller.Settings.MouseSensitivity = Math.Round(e.NewValue, 2);
        UpdateSliderLabel();
    }

    private void UpdateSliderLabel() => SensitivityText.Text = SensitivitySlider.Value.ToString("0.0");

    private void ClickLookCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_initialized) _controller.Settings.ClickToMouseLook = ClickLookCheck.IsChecked == true;
    }

    private void ReleaseKeyCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!_initialized || ReleaseKeyCombo.SelectedItem is not string key) return;
        _controller.Settings.MouseLookReleaseKey = key;
        ReleaseKeyCap.Text = key;
        UpdateMode();
    }

    private void SoundCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_initialized) _controller.Settings.PlaySound = SoundCheck.IsChecked == true;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        _controller.Settings.Save();
    }
}
