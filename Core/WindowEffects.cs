using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace VRCDeskDive.Core;

/// <summary>
/// 角の丸い半透明ウィンドウの見た目を整える（背景ぼかし・角丸の領域）。
/// </summary>
internal static class WindowEffects
{
    private const int WCA_ACCENT_POLICY = 19;
    // Windows 10 の背景ぼかし。Acrylic(4) はウィンドウを動かすと引っかかることがあるので使わない
    private const int ACCENT_ENABLE_BLURBEHIND = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public int AccentState;
        public int AccentFlags;
        public uint GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int widthEllipse, int heightEllipse);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, bool redraw);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);

    /// <summary>ウィンドウの後ろをぼかす（すりガラスのような透明感）。失敗しても見た目が少し変わるだけ。</summary>
    public static void EnableBlurBehind(Window window)
    {
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        var policy = new AccentPolicy { AccentState = ACCENT_ENABLE_BLURBEHIND };
        var size = Marshal.SizeOf<AccentPolicy>();
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(policy, ptr, false);
            var data = new WindowCompositionAttributeData
            {
                Attribute = WCA_ACCENT_POLICY,
                Data = ptr,
                SizeOfData = size,
            };
            SetWindowCompositionAttribute(hwnd, ref data);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    /// <summary>
    /// ウィンドウの形を角丸にする。背景ぼかしは四角い範囲にかかるので、角のはみ出しをこれで切り落とす。
    /// サイズが変わるたびに呼ぶこと。
    /// </summary>
    public static void ApplyRoundedRegion(Window window, double radius)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero || window.ActualWidth <= 0) return;

        var dpi = VisualTreeHelper.GetDpi(window);
        var width = (int)Math.Round(window.ActualWidth * dpi.DpiScaleX);
        var height = (int)Math.Round(window.ActualHeight * dpi.DpiScaleY);
        var diameter = (int)Math.Round(radius * 2 * dpi.DpiScaleX);

        var region = CreateRoundRectRgn(0, 0, width + 1, height + 1, diameter, diameter);
        // 成功すると領域は Windows の持ち物になるので、失敗したときだけ自分で解放する
        if (SetWindowRgn(hwnd, region, true) == 0) DeleteObject(region);
    }
}
