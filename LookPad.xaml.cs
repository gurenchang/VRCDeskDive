using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace VRCDeskDive;

/// <summary>
/// 右クリック＋ドラッグで仮想スティックとして使うパッド。
/// 押した位置が中心になり、そこからのずれ（-1〜1）を StickChanged で通知する。
/// </summary>
public partial class LookPad : UserControl
{
    private const double Radius = 55;
    private const double DeadZone = 0.08;

    private Point _origin;
    private bool _dragging;

    /// <summary>スティックの傾き (x: 右が正, y: 下が正)。離すと (0, 0)。</summary>
    public event Action<float, float>? StickChanged;

    public LookPad()
    {
        InitializeComponent();
        Ring.Width = Ring.Height = Radius * 2;
        IsEnabledChanged += (_, _) => UpdateHint();
    }

    private void UpdateHint()
    {
        Frame.Opacity = IsEnabled ? 1 : 0.5;
        HintTitle.Text = IsEnabled ? "ここで右クリック＋ドラッグ" : "デスクトップ操作中に使えます";
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        e.Handled = true;
        if (!CaptureMouse()) return;

        _dragging = true;
        _origin = e.GetPosition(StickLayer);
        Canvas.SetLeft(Ring, _origin.X - Radius);
        Canvas.SetTop(Ring, _origin.Y - Radius);
        Arm.X1 = _origin.X;
        Arm.Y1 = _origin.Y;
        MoveKnob(new Vector());
        StickLayer.Visibility = Visibility.Visible;
        Hint.Opacity = 0.25;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging) return;

        var offset = e.GetPosition(StickLayer) - _origin;
        if (offset.Length > Radius) offset *= Radius / offset.Length;
        MoveKnob(offset);

        var x = offset.X / Radius;
        var y = offset.Y / Radius;
        if (Math.Sqrt(x * x + y * y) < DeadZone) x = y = 0;
        StickChanged?.Invoke((float)x, (float)y);
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        e.Handled = true;
        ReleaseMouseCapture();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (!_dragging) return;
        _dragging = false;
        StickLayer.Visibility = Visibility.Collapsed;
        Hint.Opacity = 1;
        StickChanged?.Invoke(0, 0);
    }

    private void MoveKnob(Vector offset)
    {
        var p = _origin + offset;
        Canvas.SetLeft(Knob, p.X - Knob.Width / 2);
        Canvas.SetTop(Knob, p.Y - Knob.Height / 2);
        Arm.X2 = p.X;
        Arm.Y2 = p.Y;
    }
}
