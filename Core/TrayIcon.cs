using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace VRCDeskDive.Core;

/// <summary>タスクトレイのアイコンとメニュー。モードに応じて色が変わる。</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly AppController _controller;
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _toggleItem;
    private IntPtr _hIcon;

    public TrayIcon(AppController controller, Action showWindow, Action exit)
    {
        _controller = controller;
        _toggleItem = new ToolStripMenuItem(string.Empty, null, (_, _) => controller.Toggle());

        var menu = new ContextMenuStrip();
        menu.Items.Add(_toggleItem);
        menu.Items.Add("ウィンドウを表示", null, (_, _) => showWindow());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("終了", null, (_, _) => exit());

        _icon = new NotifyIcon { ContextMenuStrip = menu, Visible = true };
        _icon.DoubleClick += (_, _) => showWindow();

        controller.ModeChanged += Update;
        Update();
    }

    public void ShowBalloon(string text) => _icon.ShowBalloonTip(3000, "VRCDeskDive", text, ToolTipIcon.Info);

    private void Update()
    {
        var desktop = _controller.Mode == ControlMode.Desktop;
        _toggleItem.Text = desktop ? "VR操作に戻す" : "デスクトップ操作に切り替え";
        _icon.Text = desktop ? "VRCDeskDive - デスクトップ操作中" : "VRCDeskDive - VR操作中";

        var old = _hIcon;
        _hIcon = CreateIcon(desktop);
        _icon.Icon = Icon.FromHandle(_hIcon);
        if (old != IntPtr.Zero) Native.DestroyIcon(old);
    }

    private static IntPtr CreateIcon(bool desktop)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using var fill = new SolidBrush(desktop ? Color.FromArgb(255, 159, 67) : Color.FromArgb(76, 141, 255));
            g.FillEllipse(fill, 1, 1, 30, 30);
            using var font = new Font("Segoe UI", 18, FontStyle.Bold, GraphicsUnit.Pixel);
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(desktop ? "D" : "V", font, Brushes.White, new RectangleF(0, 1, 32, 32), format);
        }
        return bmp.GetHicon();
    }

    public void Dispose()
    {
        _controller.ModeChanged -= Update;
        _icon.Visible = false;
        _icon.Dispose();
        if (_hIcon != IntPtr.Zero) Native.DestroyIcon(_hIcon);
    }
}
