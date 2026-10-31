using System.Windows;
using Microsoft.Web.WebView2.Wpf;
using VDM.Core.Host;

namespace VDM.App;

/// <summary>The small window that pops up when the browser hands VDM a link.</summary>
sealed class CaptureWindow : Window
{
    readonly WebView2 _web = new();
    readonly CaptureSession _session;
    readonly WebHost _host;
    double _chrome = -1;

    public CaptureWindow(Controller controller, CaptureRequest request)
    {
        _session = new CaptureSession(controller.Host, request, this, Close, FitHeight);
        _session.Begin();
        Title = _session.Title;
        Width = 560;
        Height = request.IsMedia ? 600 : 400;
        ResizeMode = ResizeMode.CanMinimize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;
        Background = System.Windows.Media.Brushes.White;
        Icon = controller.WindowIcon;
        Content = _web;
        _host = new WebHost(_web, _session.HandleAsync);
        SourceInitialized += (_, _) => Shell.TintTitleBar(this, 0xFF, 0xFF, 0xFF);
        Loaded += async (_, _) => await _host.LoadAsync("capture.html");
        Closed += (_, _) => _session.Cancel();
    }

    void FitHeight(int content)
    {
        if (content <= 0) return;
        if (_chrome < 0) _chrome = ActualHeight - _web.ActualHeight;
        var area = SystemParameters.WorkArea;
        var target = Math.Clamp(content + _chrome, 240, area.Height - 40);
        if (Math.Abs(target - Height) < 2) return;
        Height = target;
        Top = area.Top + (area.Height - target) / 2;
    }
}
