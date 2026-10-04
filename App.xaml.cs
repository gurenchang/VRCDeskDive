using System.Threading;
using System.Windows;
using System.Windows.Interop;
using VRCDeskDive.Core;

namespace VRCDeskDive;

public partial class App : Application
{
    private Mutex? _mutex;
    private AppController? _controller;
    private MainWindow? _main;
    private ChatWindow? _chat;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _mutex = new Mutex(true, "VRCDeskDive.SingleInstance", out var created);
        if (!created)
        {
            MessageBox.Show("VRCDeskDive はすでに起動しています。", "VRCDeskDive");
            Shutdown();
            return;
        }

        _controller = new AppController();
        _main = new MainWindow(_controller);
        _controller.Input.OwnWindow = new WindowInteropHelper(_main).EnsureHandle();
        _controller.Input.ChatRequested += () => Dispatcher.BeginInvoke(OpenChat);

        // メインウィンドウを閉じたらアプリを終了する
        _main.Closed += (_, _) => ExitApp();
        _main.Show();
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
        _chat?.Close();
        _controller?.Dispose();
        _mutex?.ReleaseMutex();
        Shutdown();
    }
}
