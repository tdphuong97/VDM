using Photino.NET;
using VDM.Core;

namespace VDM.Mac;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        var instance = MacPaths.DataOverride is null ? "" : "-" + (uint)MacPaths.Data.ToLowerInvariant().GetHashCode();
        using var single = new Mutex(true, $"VDM-{Environment.UserName}{instance}", out var first);
        if (!first)
        {
            AskRunningInstanceToShow();
            return;
        }

        var version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        var platform = new MacPlatform(version);
        CopyExtension();

        var main = new PhotinoWindow { LogVerbosity = 0 }
            .SetTitle("VDM")
            .SetUseOsDefaultSize(false)
            .SetSize(1180, 760)
            .SetMinSize(720, 520)
            .SetContextMenuEnabled(false)
            .SetDevToolsEnabled(Environment.GetEnvironmentVariable("VDM_DEVTOOLS") == "1")
            .Center();
        var host = new PhotinoHost(main, platform.Host.HandleMainAsync);
        platform.AttachMain(main, host);

        // Closing the window keeps VDM running in the Dock, so downloads and link catching carry on.
        main.RegisterWindowClosingHandler((_, _) =>
        {
            if (platform.Exiting || !platform.Host.Manager.Settings.CloseToTray) return false;
            main.SetMinimized(true);
            return true;
        });
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Shutdown(platform);

        main.RegisterWindowCreatedHandler((_, _) =>
        {
            platform.Host.Start();
            if (args.Contains("--minimized")) main.SetMinimized(true);
        });
        using var timer = new Timer(_ => platform.Post(platform.Host.Tick), null, 500, 500);

        // Load(Uri): the string overload treats anything but http(s) as a file path and drops app://.
        main.Load(new Uri(PhotinoHost.Url("index.html")));
        main.WaitForClose();
        Shutdown(platform);
    }

    static int _shutDown;

    static void Shutdown(MacPlatform platform)
    {
        if (Interlocked.Exchange(ref _shutDown, 1) == 1) return;
        platform.Exiting = true;
        platform.Host.Dispose();
    }

    /// <summary>Puts the extension where Chrome's folder picker can reach it, refreshed on every update.</summary>
    static void CopyExtension()
    {
        try
        {
            var from = MacPaths.BundledExtension;
            if (!Directory.Exists(from)) return;
            var to = MacPaths.Extension;
            if (Directory.Exists(to)) Directory.Delete(to, recursive: true);
            foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(to, Path.GetRelativePath(from, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, overwrite: true);
            }
        }
        catch (Exception) { }
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
}
