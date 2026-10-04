using System.Threading;
using System.Windows;
using System.Windows.Interop;
using VRCDeskDive.Core;

namespace VRCDeskDive;

public partial class App : Application
{
    private Mutex? _mutex;
    private AppController? _controller;
    private HotkeyService? _hotkey;
    private TrayIcon? _tray;
    private MainWindow? _main;
    private ChatWindow? _chat;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _mutex = new Mutex(true, "VRCDeskDive.SingleInstance", out var created);
        if (!created)
        {
            MessageBox.Show("VRCDeskDive はすでに起動しています。タスクトレイを確認してください。", "VRCDeskDive");
            Shutdown();
            return;
        }

        _controller = new AppController();
        _main = new MainWindow(_controller);

        var hwnd = new WindowInteropHelper(_main).EnsureHandle();
        _hotkey = new HotkeyService(hwnd);
        _hotkey.Pressed += _controller.Toggle;
        _main.AttachHotkey(_hotkey);

        _tray = new TrayIcon(_controller, ShowMain, ExitApp);
        _main.HiddenToTray += () => _tray.ShowBalloon("タスクトレイで動作中です。終了はトレイアイコンの右クリックから。");

        _controller.Input.ChatRequested += () => Dispatcher.BeginInvoke(OpenChat);

        _main.Show();
    }

    private void ShowMain()
    {
        if (_main is null) return;
        _main.Show();
        if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
        _main.Activate();
    }

    private void OpenChat()
    {
        if (_controller is null) return;
        if (_chat is { IsLoaded: true })
        {
            _chat.Activate();
            return;
        }
        _chat = new ChatWindow(_controller.Osc, Native.GetForegroundWindow());
        _chat.Closed += (_, _) => _chat = null;
        _chat.Show();
    }

    private void ExitApp()
    {
        if (_main is not null)
        {
            _main.AllowClose = true;
            _main.Close();
        }
        _chat?.Close();
        _tray?.Dispose();
        _hotkey?.Dispose();
        _controller?.Dispose();
        _mutex?.ReleaseMutex();
        Shutdown();
    }
}
