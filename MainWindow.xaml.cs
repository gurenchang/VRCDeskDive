using System;
using System.ComponentModel;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using VRCDeskDive.Core;

namespace VRCDeskDive;

public partial class MainWindow : Window
{
    private const double CornerRadius = 16;
    private static readonly Duration AccentDuration = TimeSpan.FromMilliseconds(380);

    private readonly AppController _controller;
    private readonly bool _initialized;
    // モードで色が変わるアクセント（XAML からは DynamicResource で参照する）
    private readonly LinearGradientBrush _accent;
    private readonly LinearGradientBrush _accentTint;
    private bool _compact;

    public MainWindow(AppController controller)
    {
        _controller = controller;
        _accent = new LinearGradientBrush(Color("VrStartColor"), Color("VrEndColor"), new Point(0, 0), new Point(1, 1));
        _accentTint = new LinearGradientBrush(
            WithAlpha(Color("VrStartColor"), 0x30), WithAlpha(Color("VrEndColor"), 0x0A), new Point(0, 0), new Point(1, 1));
        Resources["AccentBrush"] = _accent;
        Resources["AccentTintBrush"] = _accentTint;

        InitializeComponent();
        // 画面に収まらないときは ScrollViewer でスクロールさせる
        MaxHeight = SystemParameters.WorkArea.Height;
        VersionText.Text = $"v{Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)}";

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

        SourceInitialized += (_, _) => WindowEffects.EnableBlurBehind(this);
        SizeChanged += (_, _) => UpdateShape();
        Loaded += (_, _) => PlayIntro();
    }

    private static Color Color(string key) => (Color)Application.Current.FindResource(key);

    private static Color WithAlpha(Color c, byte alpha) => System.Windows.Media.Color.FromArgb(alpha, c.R, c.G, c.B);

    // --- ウィンドウの見た目 ---

    /// <summary>角丸の形に合わせて、ウィンドウの領域（背景ぼかしの範囲）と中身の切り抜きを更新する。</summary>
    private void UpdateShape()
    {
        WindowEffects.ApplyRoundedRegion(this, CornerRadius);
        var inner = CornerRadius - WindowFrame.BorderThickness.Left;
        ClipHost.Clip = new RectangleGeometry(new Rect(ClipHost.RenderSize), inner, inner);
    }

    /// <summary>起動時に、少し下からふわっと現れる。</summary>
    private void PlayIntro()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        WindowFrame.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
        FrameShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(12, 0, TimeSpan.FromMilliseconds(320)) { EasingFunction = ease });
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

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
        SpacerColumn.Width = new GridLength(compact ? 0 : 20);
        RightColumn.Width = compact ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        RightPanel.Visibility = full;
        Subtitle.Visibility = full;
        ModeHint.Visibility = full;
        StatusGrid.Visibility = full;
        VersionBadge.Visibility = full;
        CompactStatus.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;

        RootGrid.Margin = compact ? new Thickness(14, 0, 14, 14) : new Thickness(22, 0, 22, 22);
        TitleText.FontSize = compact ? 18 : 21;
        ModeCard.Padding = compact ? new Thickness(16, 12, 16, 14) : new Thickness(20, 16, 20, 16);
        ModeText.FontSize = compact ? 22 : 30;
        ModeText.Margin = compact ? new Thickness(0, 0, 0, 10) : new Thickness(0, 0, 0, 2);
        LookPad.Height = compact ? 140 : 170;
        Width = compact ? 340 : 880;

        CompactButton.Content = compact ? "" : "";
        CompactButton.ToolTip = compact ? "元の大きさに戻す" : "縮小表示";
        if (_initialized) UpdateDriverStatus();
    }

    // --- 状態表示 ---

    private void UpdateMode()
    {
        var desktop = _controller.Mode == ControlMode.Desktop;
        var mouseLook = desktop && _controller.Input.IsMouseLook;

        // アクセント色を今のモードの色へなめらかに変える
        var (start, end) = desktop
            ? (Color("DesktopStartColor"), Color("DesktopEndColor"))
            : (Color("VrStartColor"), Color("VrEndColor"));
        AnimateStop(_accent.GradientStops[0], start);
        AnimateStop(_accent.GradientStops[1], end);
        AnimateStop(_accentTint.GradientStops[0], WithAlpha(start, 0x30));
        AnimateStop(_accentTint.GradientStops[1], WithAlpha(end, 0x0A));
        ModeGlow.BeginAnimation(DropShadowEffect.ColorProperty, new ColorAnimation(end, AccentDuration));

        ModeLabel.Text = mouseLook ? "MOUSE LOOK" : desktop ? "DESKTOP MODE" : "VR MODE";
        ModeText.Text = mouseLook && _compact ? "マウスルック中" : desktop ? "デスクトップ操作" : "VR操作";
        ModeHint.Text = mouseLook
            ? $"マウスルック中（{_controller.Settings.MouseLookReleaseKey}で解除）"
            : desktop
                ? "HMDを外して、キーボードとマウスで操作できます"
                : "いつもどおりHMDとコントローラーで操作します";

        // ボタンは切り替え先のモードの色で光らせる
        var targetGradient = (Brush)FindResource(desktop ? "VrGradient" : "DesktopGradient");
        ToggleButton.Content = desktop ? "VR操作に戻す" : "デスクトップ操作に切り替え";
        ToggleButton.Background = targetGradient;
        ToggleButton.Effect = new DropShadowEffect
        {
            Color = desktop ? Color("VrEndColor") : Color("DesktopEndColor"),
            BlurRadius = 22,
            ShadowDepth = 0,
            Opacity = 0.55,
        };

        Title = desktop ? "VRCDeskDive - デスクトップ操作中" : "VRCDeskDive";
        LookPad.IsEnabled = desktop;
    }

    private static void AnimateStop(GradientStop stop, Color to) =>
        stop.BeginAnimation(GradientStop.ColorProperty, new ColorAnimation(to, AccentDuration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
        });

    /// <summary>状態ランプの色を変える。灰色以外は同じ色でほんのり光らせる。</summary>
    private void SetDot(System.Windows.Shapes.Ellipse dot, string brushKey)
    {
        var brush = (SolidColorBrush)FindResource(brushKey);
        dot.Fill = brush;
        dot.Effect = brushKey == "IdleBrush"
            ? null
            : new DropShadowEffect { Color = brush.Color, BlurRadius = 10, ShadowDepth = 0, Opacity = 0.9 };
    }

    private void UpdateDriverStatus()
    {
        var status = _controller.Input.Driver.Status;
        var steamVr = status != DriverStatus.SteamVrNotRunning;
        SteamVrText.Text = steamVr ? "接続" : "未起動";
        SetDot(SteamVrDot, steamVr ? "OkBrush" : "IdleBrush");

        var (text, brush) = status switch
        {
            DriverStatus.Connected => ("接続", "OkBrush"),
            DriverStatus.WaitingForHmd => ("接続（HMDの姿勢待ち）", "WarnBrush"),
            DriverStatus.HookFailed => ("エラー（HMDの姿勢を取得できません）", "ErrorBrush"),
            DriverStatus.NotLoaded => ("未接続", "WarnBrush"),
            DriverStatus.VersionMismatch => ("更新あり（SteamVRの再起動が必要）", "WarnBrush"),
            _ => ("未接続", "IdleBrush"),
        };
        DriverText.Text = text;
        SetDot(DriverDot, brush);
        SetDot(CompactDriverDot, brush);

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
        DriverHint.Visibility = hint is null ? Visibility.Collapsed : Visibility.Visible;
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
        var (text, brush) = (vrc.IsRunning, vrc.IsForeground) switch
        {
            (true, true) => ("起動中（前面）", "OkBrush"),
            (true, false) => ("起動中", "WarnBrush"),
            _ => ("未検出", "IdleBrush"),
        };
        VrcText.Text = text;
        SetDot(VrcDot, brush);
        SetDot(CompactVrcDot, brush);
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
