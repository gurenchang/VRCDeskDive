using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using VRCDeskDive.Core;

namespace VRCDeskDive;

/// <summary>VRChat のチャットボックス (/chatbox/input) に送る文字を入力する小窓。</summary>
public partial class ChatWindow : Window
{
    private readonly OscClient _osc;
    private readonly IntPtr _returnTo;
    private bool _typing;
    private bool _returnFocus;
    private bool _closing;

    public ChatWindow(OscClient osc, IntPtr returnTo)
    {
        _osc = osc;
        _returnTo = returnTo;
        InitializeComponent();
        // メインウィンドウと同じ、角丸＋背景ぼかしの見た目にする
        SourceInitialized += (_, _) => WindowEffects.EnableBlurBehind(this);
        SizeChanged += (_, _) => WindowEffects.ApplyRoundedRegion(this, 16);
        Loaded += (_, _) =>
        {
            Native.ForceForeground(new WindowInteropHelper(this).Handle);
            Activate();
            Input.Focus();
        };
        // 他の場所をクリックしたら閉じる（閉じている最中の Close は例外になるので避ける）
        Closing += (_, _) => _closing = true;
        Deactivated += (_, _) =>
        {
            if (!_closing) Close();
        };
    }

    private void Input_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                var text = Input.Text.Trim();
                if (text.Length > 0) _osc.Send("/chatbox/input", text, true, true);
                CloseAndReturn();
                e.Handled = true;
                break;
            case Key.Escape:
                CloseAndReturn();
                e.Handled = true;
                break;
        }
    }

    private void Input_TextChanged(object sender, TextChangedEventArgs e)
    {
        CountText.Text = $"{Input.Text.Length}/144";
        var typing = Input.Text.Length > 0;
        if (typing == _typing) return;
        _typing = typing;
        _osc.Send("/chatbox/typing", typing);
    }

    private void CloseAndReturn()
    {
        _returnFocus = true;
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (_typing) _osc.Send("/chatbox/typing", false);
        // Enter / Esc で閉じたときは VRChat にフォーカスを戻す
        if (_returnFocus && _returnTo != IntPtr.Zero) Native.SetForegroundWindow(_returnTo);
    }
}
