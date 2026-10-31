using System.ComponentModel;
using System.Windows;

namespace VDM.App;

public partial class MainWindow : Window
{
    readonly Controller _controller;
    internal WebHost Host { get; }

    internal MainWindow(Controller controller)
    {
        _controller = controller;
        InitializeComponent();
        Host = new WebHost(Web, controller.Host.HandleMainAsync);
        SourceInitialized += (_, _) => Shell.TintTitleBar(this, 0xF1, 0xF6, 0xF6);
        Loaded += async (_, _) => await Host.LoadAsync("index.html");
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Closing to the tray keeps downloads running and the browser connection alive.
        if (!_controller.Exiting && _controller.Manager.Settings.CloseToTray)
        {
            e.Cancel = true;
            Hide();
            _controller.HintTray();
            return;
        }
        base.OnClosing(e);
        if (!_controller.Exiting) _controller.Exit();
    }

    public void Reveal(string? view = null)
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;  // bring forward even when another app has focus
        Topmost = false;
        Focus();
        if (view is not null) Host.Emit("show", view);
        _controller.Host.PushState(force: true);
    }
}
