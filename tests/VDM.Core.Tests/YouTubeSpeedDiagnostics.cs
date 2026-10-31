using System.Diagnostics;
using System.Text;
using Xunit.Abstractions;

namespace VDM.Core.Tests;

/// <summary>
/// Measures how fast yt-dlp brings a YouTube stream down: as one stream, and split into chunks fetched
/// side by side. Runs only when VDM_TOOLS (deps\tools) and VDM_DIAG_URL (a YouTube page) are set.
/// </summary>
public sealed class YouTubeSpeedDiagnostics(ITestOutputHelper output) : IDisposable
{
    static readonly string? Tools = Environment.GetEnvironmentVariable("VDM_TOOLS");
    static readonly string? Url = Environment.GetEnvironmentVariable("VDM_DIAG_URL");
    readonly string _dir = Directory.CreateTempSubdirectory("vdm-diag-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public async Task One_stream_against_chunks_side_by_side()
    {
        if (Tools is null || Url is null) return;
        await MeasureAsync("one stream (today)", "-f", "137");
        await MeasureAsync("dashy, 8 chunks at once", "-f", "137", "--extractor-args", "youtube:formats=dashy", "--concurrent-fragments", "8");
    }

    /// <summary>VDM_DIAG_URLS: two or more YouTube pages separated by |, downloaded at once for 40 s.</summary>
    [Fact]
    public async Task Downloads_running_at_once_share_the_line()
    {
        var urls = Environment.GetEnvironmentVariable("VDM_DIAG_URLS")?.Split('|');
        if (Tools is null || urls is null) return;
        var data = Directory.CreateDirectory(Path.Combine(_dir, "data")).FullName;
        using var m = new DownloadManager(data, new ToolPaths(Tools));
        m.Settings.DownloadFolder = Directory.CreateDirectory(Path.Combine(_dir, "dl")).FullName;
        m.Settings.Connections = 8;
        m.Settings.ShareBandwidth = Environment.GetEnvironmentVariable("VDM_DIAG_SHARE") != "0";
        var items = urls.Select((u, n) => YtDlp.IsYouTube(u)
            ? m.AddMedia(new MediaRequest { Url = u }, null, new MediaOptions { Height = 1080 }, null, start: true)
            : m.AddFile(new FileRequest { Url = u }, $"file{n}.bin", null, null, start: true)).ToList();
        var sw = Stopwatch.StartNew();
        var next = 2000L;
        while (sw.ElapsedMilliseconds < 40000)
        {
            m.Tick();
            await Task.Delay(500);
            if (sw.ElapsedMilliseconds < next) continue;
            next += 2000;
            output.WriteLine($"{sw.Elapsed.TotalSeconds,4:F0}s  " + string.Join("   ", items.Select(i => $"{i.Url[^11..]} {i.Speed / 1048576,6:F1} MB/s ({i.DownloadedBytes / 1048576,5:F0} MB)")) + "   | " + m.BalanceTrace);
        }
        m.PauseAll();
    }

    /// <summary>Where the seconds go between clicking "download" and the dialog showing the qualities.</summary>
    [Fact]
    public async Task Probe_timeline()
    {
        if (Tools is null || Url is null) return;
        var media = new MediaDownloader(new ToolPaths(Tools));
        for (var i = 0; i < 2; i++)
        {
            var sw = Stopwatch.StartNew();
            await RunTimedAsync("--version");
            output.WriteLine($"yt-dlp --version: {sw.ElapsedMilliseconds} ms");
        }
        for (var i = 0; i < 2; i++)
        {
            var sw = Stopwatch.StartNew();
            await media.ProbeAsync(new MediaRequest { Url = Url }, CancellationToken.None);
            output.WriteLine($"ProbeAsync as the app runs it: {sw.ElapsedMilliseconds} ms");
        }
        foreach (var line in await RunTimedAsync("-J", "-v", "--no-playlist", "--no-colors", "--", Url))
            output.WriteLine(line);
    }

    /// <summary>The probe of VDM_DIAG_URL while the VDM_DIAG_URLS downloads fill the line, as when a 4th video is caught.</summary>
    [Fact]
    public async Task Probe_while_downloads_run()
    {
        var urls = Environment.GetEnvironmentVariable("VDM_DIAG_URLS")?.Split('|');
        if (Tools is null || Url is null || urls is null) return;
        var data = Directory.CreateDirectory(Path.Combine(_dir, "data")).FullName;
        using var m = new DownloadManager(data, new ToolPaths(Tools));
        m.Settings.DownloadFolder = Directory.CreateDirectory(Path.Combine(_dir, "dl")).FullName;
        m.Settings.Connections = 8;
        var items = urls.Select(u => m.AddMedia(new MediaRequest { Url = u }, null, new MediaOptions { Height = 1080 }, null, start: true)).ToList();
        var sw = Stopwatch.StartNew();
        using var ticking = new CancellationTokenSource();
        var ticker = Task.Run(async () =>
        {
            while (!ticking.IsCancellationRequested) { m.Tick(); await Task.Delay(500); }
        });
        string State() => string.Join("  ", items.Select(i => $"{i.Url[^11..]} {i.Status} {i.Stage} {i.Speed / 1048576:F1} MB/s {i.DownloadedBytes / 1048576} MB"));
        for (var s = 0; s < 10; s++)
        {
            await Task.Delay(2000);
            output.WriteLine($"{sw.Elapsed.TotalSeconds,3:F0}s {State()} | {m.BalanceTrace}");
        }
        var probe = Stopwatch.StartNew();
        await m.Media.ProbeAsync(new MediaRequest { Url = Url }, CancellationToken.None);
        output.WriteLine($"probe with the line full: {probe.ElapsedMilliseconds} ms; {State()}");
        ticking.Cancel();
        await ticker;
        m.PauseAll();
    }

    /// <summary>The capture dialog's session starts asking at once, so the page (ready a moment later) waits less;
    /// and "Tải xuống" before the qualities came still adds the video.</summary>
    [Fact]
    public async Task Capture_session_asks_before_the_page_is_ready()
    {
        if (Tools is null || Url is null) return;
        using var host = new VDM.Core.Host.AppHost(new StubPlatform(), Path.Combine(_dir, "data"), Tools, "test");
        host.Manager.Settings.DownloadFolder = Directory.CreateDirectory(Path.Combine(_dir, "dl")).FullName;
        var none = System.Text.Json.JsonDocument.Parse("{}").RootElement;

        var session = new VDM.Core.Host.CaptureSession(host, new VDM.Core.Host.CaptureRequest { IsMedia = true, Media = new MediaRequest { Url = Url } },
            null, () => { }, _ => { });
        var sw = Stopwatch.StartNew();
        session.Begin();
        await Task.Delay(1500); // the window and its web view opening
        var asked = Stopwatch.StartNew();
        var info = (MediaInfo)(await session.HandleAsync("probeMedia", none))!;
        output.WriteLine($"qualities {sw.ElapsedMilliseconds} ms after the link came; the page waited {asked.ElapsedMilliseconds} ms");
        Assert.NotEmpty(info.Qualities);

        var closed = false;
        var early = new VDM.Core.Host.CaptureSession(host, new VDM.Core.Host.CaptureRequest { IsMedia = true, Media = new MediaRequest { Url = Url, Title = "From the page" } },
            null, () => closed = true, _ => { });
        early.Begin();
        var go = System.Text.Json.JsonDocument.Parse("""{"height":1080,"start":false,"folder":""}""").RootElement;
        await early.HandleAsync("confirmMedia", go);
        early.Cancel();
        Assert.True(closed);
        var item = Assert.Single(host.Manager.Items, i => i.Media?.Title == "From the page");
        Assert.Equal(1080, item.Media!.Height);
    }

    sealed class StubPlatform : VDM.Core.Host.IPlatform
    {
        public string Name => "windows";
        public string ExtensionChromium => "";
        public string ExtensionFirefox => "";
        public string FirefoxXpi => "";
        public void Post(Action action) => action();
        public void OpenFile(string path) { }
        public void Reveal(string path) { }
        public void OpenUrl(string url) { }
        public string? ClipboardUrl() => null;
        public void CopyText(string text) { }
        public Task<string?> ChooseFolderAsync(object? owner, string current) => Task.FromResult<string?>(null);
        public bool OpenInBrowser(string browser, string target) => false;
        public bool StartsWithSystem() => false;
        public void SetStartWithSystem(bool on) { }
        public void Notify(string title, string text, DownloadItem? item) { }
        public void Warn(string message) { }
        public void OpenCapture(VDM.Core.Host.CaptureRequest request) { }
        public void ShowMain(string? view) { }
        public void Quit() { }
        public bool CanInstallUpdates => false;
        public void InstallUpdate(string installer) { }
    }

    /// <summary>Runs yt-dlp; returns its stderr lines, each stamped with the ms since start.</summary>
    async Task<List<string>> RunTimedAsync(params string[] args)
    {
        var psi = new ProcessStartInfo(Path.Combine(Tools!, "yt-dlp.exe"))
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["PATH"] = Tools + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
        psi.Environment["PYTHONUTF8"] = "1";
        var lines = new List<string>();
        var sw = Stopwatch.StartNew();
        using var p = new Process { StartInfo = psi };
        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            var l = e.Data.Length > 160 ? e.Data[..160] : e.Data;
            lock (lines) lines.Add($"{sw.ElapsedMilliseconds,6} {l}");
        };
        p.Start();
        p.BeginErrorReadLine();
        await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        lock (lines) lines.Add($"{sw.ElapsedMilliseconds,6} (exit {p.ExitCode})");
        return lines;
    }

    [Fact]
    public async Task Lists_the_formats_beside_the_sizes_vdm_shows()
    {
        if (Tools is null || Url is null) return;
        var psi = new ProcessStartInfo(Path.Combine(Tools, "yt-dlp.exe"))
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, CreateNoWindow = true,
        };
        foreach (var a in new[] { "-J", "--no-playlist", "--", Url }) psi.ArgumentList.Add(a);
        psi.Environment["PATH"] = Tools + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
        using var p = Process.Start(psi)!;
        var json = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        var root = System.Text.Json.JsonDocument.Parse(json).RootElement;
        if (root.TryGetProperty("entries", out var entries)) root = entries[0]; // a ytsearch1: query
        string? S(System.Text.Json.JsonElement e, string n) => e.TryGetProperty(n, out var v) ? v.ToString() : null;
        output.WriteLine($"{S(root, "id")} | {S(root, "title")} | {S(root, "duration")} s");
        foreach (var f in root.GetProperty("formats").EnumerateArray())
            if (S(f, "vcodec") is { } vc && vc != "none" && S(f, "ext") != "mhtml" || S(f, "format_id") == "140")
                output.WriteLine($"fmt {S(f, "format_id"),-5} {S(f, "protocol"),-12} {S(f, "vcodec"),-14} {S(f, "acodec"),-11} h={S(f, "height"),-5} size={S(f, "filesize")} tbr={S(f, "tbr")}");
        foreach (var q in YtDlp.ParseInfo(root.GetRawText()).Qualities)
            output.WriteLine($"vdm {q.Label,-14} {q.ApproxBytes / 1048576.0:F1} MB");
    }

    [Fact]
    public async Task Whole_video_through_the_app_reports_honest_sizes()
    {
        if (Tools is null || Url is null) return;
        var media = new MediaDownloader(new ToolPaths(Tools));
        var info = await media.ProbeAsync(new MediaRequest { Url = Url }, CancellationToken.None);
        var estimate = info.Qualities.First(q => q.Height == 1080).ApproxBytes!.Value;
        var item = new DownloadItem
        {
            Kind = DownloadKind.Media, Url = Url, Folder = _dir, FileName = "video.mp4", Connections = 16, TotalBytes = estimate,
            Media = new MediaOptions { Height = 1080, EstimatedBytes = estimate },
        };
        long maxTotal = 0;
        var sw = Stopwatch.StartNew();
        var run = media.RunAsync(item, CancellationToken.None);
        while (!run.IsCompleted)
        {
            await Task.Delay(500);
            lock (item.Sync) maxTotal = Math.Max(maxTotal, item.TotalBytes);
        }
        await run;
        var size = new FileInfo(item.FullPath).Length;
        output.WriteLine($"estimate {estimate:N0}, final file {size:N0}, largest total shown {maxTotal:N0}, " +
                         $"{size / 1048576.0 / sw.Elapsed.TotalSeconds:F1} MB/s over {sw.Elapsed.TotalSeconds:F0} s");
        Assert.True(maxTotal < size * 1.15, "the total shown must not balloon past the real size");
    }

    async Task MeasureAsync(string label, params string[] extra)
    {
        var folder = Directory.CreateDirectory(Path.Combine(_dir, label.Split(' ')[0])).FullName;
        var psi = new ProcessStartInfo(Path.Combine(Tools!, "yt-dlp.exe"))
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, CreateNoWindow = true, WorkingDirectory = folder,
        };
        foreach (var a in extra.Concat(new[] { "--newline", "--no-playlist", "--progress-template",
                     "download:[vdm] %(progress.downloaded_bytes)s|%(progress.speed)s|%(info.protocol)s", "-o", "probe.%(ext)s", "--", Url! }))
            psi.ArgumentList.Add(a);
        psi.Environment["PATH"] = Tools + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");

        long downloaded = 0;
        string? protocol = null;
        var lastLines = new List<string>();
        using var p = new Process { StartInfo = psi };
        p.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            if (e.Data.StartsWith("[vdm] "))
            {
                var f = e.Data[6..].Split('|');
                if (long.TryParse(f[0].Split('.')[0], out var d)) downloaded = Math.Max(downloaded, d);
                protocol = f.ElementAtOrDefault(2);
            }
            else lock (lastLines) lastLines.Add(e.Data);
        };
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (lastLines) lastLines.Add(e.Data); };
        var sw = Stopwatch.StartNew();
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        long startedAt = -1, bytesAtStart = 0;
        while (sw.Elapsed < TimeSpan.FromSeconds(40) && !p.HasExited)
        {
            await Task.Delay(250);
            if (startedAt < 0 && downloaded > 0) { startedAt = sw.ElapsedMilliseconds; bytesAtStart = downloaded; }
            if (startedAt >= 0 && sw.ElapsedMilliseconds - startedAt > 15000) break;
        }
        var seconds = (sw.ElapsedMilliseconds - Math.Max(0, startedAt)) / 1000.0;
        try { p.Kill(entireProcessTree: true); } catch { }
        await p.WaitForExitAsync();
        output.WriteLine($"{label}: protocol={protocol}, {(downloaded - bytesAtStart) / 1048576.0:F1} MB in {seconds:F1} s = {(downloaded - bytesAtStart) / 1048576.0 / seconds:F2} MB/s");
        lock (lastLines)
            foreach (var l in lastLines.Where(l => l.Contains("ERROR") || l.Contains("WARNING") || l.Contains("format")).Take(5))
                output.WriteLine("   " + l);
    }
}
