using System.Diagnostics;
using Photino.NET;
using VDM.Core;
using VDM.Core.Host;

namespace VDM.Mac;

/// <summary>VDM on macOS: Finder through `open`, the clipboard through pbcopy, a LaunchAgent for login.</summary>
sealed class MacPlatform : IPlatform
{
    const string AgentLabel = "vn.vdm.app";
    static readonly string AgentPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents", AgentLabel + ".plist");

    // Browser name → the program inside its app bundle.
    static readonly Dictionary<string, string> BrowserApps = new()
    {
        ["Chrome"] = "Google Chrome.app/Contents/MacOS/Google Chrome",
        ["Edge"] = "Microsoft Edge.app/Contents/MacOS/Microsoft Edge",
        ["Cốc Cốc"] = "CocCoc.app/Contents/MacOS/CocCoc",
        ["Firefox"] = "Firefox.app/Contents/MacOS/firefox",
    };

    PhotinoWindow? _main;
    PhotinoHost? _mainHost;

    public AppHost Host { get; }
    public bool Exiting { get; set; }

    public MacPlatform(string version)
    {
        Host = new AppHost(this, MacPaths.Data, MacPaths.Tools, version);
    }

    public void AttachMain(PhotinoWindow window, PhotinoHost host)
    {
        _main = window;
        _mainHost = host;
        Host.AttachMain(new MainView(host));
    }

    sealed class MainView(PhotinoHost host) : IMainView
    {
        public bool Ready => host.Ready;
        public void Emit(string evt, object? data) => host.Emit(evt, data);
    }

    public string Name => "mac";
    public string ExtensionChromium => Path.Combine(MacPaths.Extension, "chromium");
    public string ExtensionFirefox => Path.Combine(MacPaths.Extension, "firefox");
    public string FirefoxXpi => Path.Combine(MacPaths.Extension, "vdm-firefox.xpi");

    public void Post(Action action)
    {
        if (_main is null) { action(); return; }
        try { _main.Invoke(action); } catch (Exception) { }
    }

    static void Run(string file, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            Process.Start(psi)?.Dispose();
        }
        catch (Exception) { }
    }

    static string Capture(string file, string? stdin = null)
    {
        try
        {
            var psi = new ProcessStartInfo(file)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardInput = stdin is not null,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
            };
            using var p = Process.Start(psi)!;
            if (stdin is not null)
            {
                using (var w = new StreamWriter(p.StandardInput.BaseStream, new System.Text.UTF8Encoding(false))) w.Write(stdin);
            }
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(3000);
            return output;
        }
        catch (Exception)
        {
            return "";
        }
    }

    public void OpenFile(string path)
    {
        if (File.Exists(path)) Run("open", path);
        else Reveal(path);
    }

    public void Reveal(string path)
    {
        if (File.Exists(path)) Run("open", "-R", path);
        else
        {
            var dir = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
            if (dir is not null && Directory.Exists(dir)) Run("open", dir);
        }
    }

    public void OpenUrl(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == "http" || u.Scheme == "https"))
            Run("open", u.AbsoluteUri);
    }

    public string? ClipboardUrl()
    {
        var text = Capture("pbpaste").Trim();
        return text.Length is > 0 and < 4096 && Uri.TryCreate(text, UriKind.Absolute, out var u)
               && (u.Scheme == "http" || u.Scheme == "https") ? text : null;
    }

    public void CopyText(string text) => Capture("pbcopy", text);

    public Task<string?> ChooseFolderAsync(object? owner, string current)
    {
        var window = owner as PhotinoWindow ?? _main;
        var tcs = new TaskCompletionSource<string?>();
        if (window is null) { tcs.SetResult(null); return tcs.Task; }
        window.Invoke(() =>
        {
            try
            {
                var start = Directory.Exists(current) ? current : KnownFolders.Downloads;
                var picked = window.ShowOpenFolder("Chọn thư mục lưu file", start, false);
                tcs.TrySetResult(picked is { Length: > 0 } ? picked[0] : null);
            }
            catch (Exception)
            {
                tcs.TrySetResult(null);
            }
        });
        return tcs.Task;
    }

    static string? BrowserProgram(string browser)
    {
        if (!BrowserApps.TryGetValue(browser, out var inside)) return null;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var apps in new[] { "/Applications", Path.Combine(home, "Applications") })
        {
            var exe = Path.Combine(apps, inside);
            if (File.Exists(exe)) return exe;
        }
        return null;
    }

    public bool OpenInBrowser(string browser, string target)
    {
        var exe = BrowserProgram(browser);
        if (exe is null) return false;
        if (File.Exists(target))
        {
            // A file such as the Firefox add-on: let the browser's app open it.
            var app = exe[..(exe.IndexOf(".app/", StringComparison.Ordinal) + 4)];
            Run("open", "-a", app, target);
        }
        else
        {
            // chrome://extensions and the like: the browser passes it to the running copy.
            Run(exe, target);
        }
        return true;
    }

    public bool StartsWithSystem() => File.Exists(AgentPath);

    public void SetStartWithSystem(bool on)
    {
        if (!on)
        {
            if (File.Exists(AgentPath)) File.Delete(AgentPath);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(AgentPath)!);
        var exe = System.Security.SecurityElement.Escape(Environment.ProcessPath ?? "");
        File.WriteAllText(AgentPath, $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
              <key>Label</key><string>{AgentLabel}</string>
              <key>ProgramArguments</key><array><string>{exe}</string><string>--minimized</string></array>
              <key>RunAtLoad</key><true/>
            </dict>
            </plist>
            """);
    }

    public void Notify(string title, string text, DownloadItem? item)
    {
        static string Quote(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", " ") + "\"";
        if (text.Length > 200) text = text[..200];
        Run("osascript", "-e", $"display notification {Quote(text)} with title {Quote(title)}");
    }

    public void Warn(string message) =>
        _main?.ShowMessage("VDM", message, PhotinoDialogButtons.Ok, PhotinoDialogIcon.Warning);

    public void ShowMain(string? view)
    {
        if (_main is null) return;
        _main.Invoke(() =>
        {
            _main.SetMinimized(false);
            _main.SetTopMost(true); // bring forward even when another app has focus
            _main.SetTopMost(false);
        });
        if (view is not null) _mainHost?.Emit("show", view);
        Host.PushState(force: true);
    }

    public void Quit()
    {
        Exiting = true;
        _main?.Invoke(_main.Close);
    }

    // Built from source on the Mac: a new version is a new build, so VDM only opens the release page.
    public bool CanInstallUpdates => false;
    public void InstallUpdate(string installer) => throw new NotSupportedException();

    public void OpenCapture(CaptureRequest request)
    {
        if (_main is null) return;
        _main.Invoke(() => CaptureDialog.Open(this, _main, request));
    }
}
