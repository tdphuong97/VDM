using System.Net.Http;
using System.Windows;
using VDM.Core;

namespace VDM.App;

public partial class App : Application
{
    Mutex? _single;
    Controller? _controller;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var instance = AppPaths.DataOverride is null ? "" : "-" + (uint)AppPaths.Data.ToLowerInvariant().GetHashCode();
        _single = new Mutex(true, $@"Local\VDM-{Environment.UserName}{instance}", out var first);
        if (!first)
        {
            // Already running: bring that window forward instead of starting twice.
            AskRunningInstanceToShow();
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, ex) =>
        {
            MessageBox.Show("VDM gặp lỗi: " + ex.Exception.Message, "VDM", MessageBoxButton.OK, MessageBoxImage.Error);
            ex.Handled = true;
        };

        // Windows shutting down, or the installer replacing the app: save and leave.
        SessionEnding += (_, _) => _controller?.Exit();

        _controller = new Controller();
        _controller.Start(minimized: e.Args.Contains("--minimized"), updated: e.Args.Contains("--updated"));
    }

    static void AskRunningInstanceToShow()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        for (var port = LocalApi.FirstPort; port <= LocalApi.LastPort; port++)
        {
            try
            {
                var req = new HttpRequestMessage(HttpMethod.Post, $"http://localhost:{port}/show");
                req.Headers.Add("X-VDM", "1");
                if (http.Send(req).IsSuccessStatusCode) return;
            }
            catch (Exception) { }
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _controller?.Exit();
        _single?.Dispose();
        base.OnExit(e);
    }
}
