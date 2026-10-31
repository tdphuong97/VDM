using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace VDM.Core;

public sealed class MediaQuality
{
    /// <summary>Smaller side of the frame; null for a source that reports no resolution.</summary>
    public int? Height { get; set; }
    public string Label { get; set; } = "";
    public long? ApproxBytes { get; set; }
}

public sealed class MediaInfo
{
    public string Title { get; set; } = "";
    public string? Thumbnail { get; set; }
    public double? Duration { get; set; }
    public string? Uploader { get; set; }
    public string? Site { get; set; }
    public string? WebpageUrl { get; set; }
    public bool IsLive { get; set; }
    public bool HasVideo => Qualities.Count > 0;
    public List<MediaQuality> Qualities { get; set; } = new();
    public long? AudioApproxBytes { get; set; }
}

public sealed record MediaProgress(string? Status, long? Downloaded, long? Total, long? TotalEstimate,
    double? Speed, string? FormatId);

/// <summary>
/// Adds up yt-dlp's progress across the streams of one download (video, then audio). Each stream is
/// counted once by its format id, so a stream yt-dlp starts again does not count twice; the size worked
/// out before starting wins over yt-dlp's early guesses.
/// </summary>
public sealed class MediaProgressTally(long estimate)
{
    readonly Dictionary<string, (long Done, long Total)> _parts = new();

    public long Done => _parts.Values.Sum(p => p.Done);

    public long Total
    {
        get
        {
            var reported = _parts.Values.Sum(p => p.Total);
            var total = estimate > 0 ? estimate : reported;
            return Math.Max(total, Done);
        }
    }

    public void Apply(MediaProgress p)
    {
        var key = p.FormatId ?? "";
        _parts[key] = (p.Downloaded ?? 0, p.Total ?? p.TotalEstimate ?? 0);
    }
}

/// <summary>Where the bundled helper programs live; the installer puts them next to the app.</summary>
public sealed class ToolPaths(string folder)
{
    public string Folder { get; } = folder;
    public string YtDlp => Path.Combine(Folder, Exe("yt-dlp"));
    public string Ffmpeg => Path.Combine(Folder, Exe("ffmpeg"));
    public string Deno => Path.Combine(Folder, Exe("deno"));

    static string Exe(string name) => OperatingSystem.IsWindows() ? name + ".exe" : name;
}

public static class YtDlp
{
    public const string ProgressPrefix = "[vdm] ";

    static readonly string ProgressTemplate = "download:" + ProgressPrefix +
        "%(progress.status)s|%(progress.downloaded_bytes)s|%(progress.total_bytes)s|" +
        "%(progress.total_bytes_estimate)s|%(progress.speed)s|%(info.format_id)s";

    public static bool IsYouTube(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) &&
        (u.Host.EndsWith("youtube.com", StringComparison.OrdinalIgnoreCase) ||
         u.Host.EndsWith("youtu.be", StringComparison.OrdinalIgnoreCase) ||
         u.Host.EndsWith("youtube-nocookie.com", StringComparison.OrdinalIgnoreCase));

    /// <summary>Request options shared by probing and downloading: who we say we are, and the login.</summary>
    static void AddRequestArgs(List<string> args, string url, string? referrer, string? userAgent, string? cookieFile)
    {
        if (IsYouTube(url)) return; // yt-dlp chooses its own clients for YouTube; browser headers only hurt there
        if (!string.IsNullOrWhiteSpace(referrer)) { args.Add("--referer"); args.Add(referrer); }
        if (!string.IsNullOrWhiteSpace(userAgent)) { args.Add("--add-header"); args.Add("User-Agent:" + userAgent); }
        if (cookieFile is not null) { args.Add("--cookies"); args.Add(cookieFile); }
    }

    public static List<string> ProbeArgs(MediaRequest req, string? cookieFile)
    {
        var args = new List<string> { "-J", "--no-playlist", "--no-warnings", "--no-colors" };
        AddRequestArgs(args, req.Url, req.Referrer, req.UserAgent, cookieFile);
        args.Add("--");
        args.Add(req.Url);
        return args;
    }

    public static List<string> DownloadArgs(DownloadItem item, ToolPaths tools, string? cookieFile, string pathFile, string? proxy = null)
    {
        var m = item.Media ?? new MediaOptions();
        var args = new List<string>
        {
            "--newline", "--no-colors", "--no-playlist", "--no-mtime", "--progress",
            "--progress-template", ProgressTemplate,
            "--print-to-file", "after_move:%(filepath)s", pathFile,
            "--ffmpeg-location", tools.Folder,
            "--concurrent-fragments", Math.Clamp(item.Connections, 1, 16).ToString(),
            "-P", item.Folder,
            "-P", "temp:" + TempFolder(item),
            "-o", "%(title).150B.%(ext)s",
        };
        if (m.AudioOnly)
        {
            var mp3 = !string.Equals(m.AudioFormat, "m4a", StringComparison.OrdinalIgnoreCase);
            args.AddRange(new[] { "-f", mp3 ? "ba/b" : "ba[ext=m4a]/ba/b", "-x", "--audio-format", mp3 ? "mp3" : "m4a" });
            if (mp3) args.AddRange(new[] { "--audio-quality", "0" });
            args.Add("--embed-metadata");
        }
        else
        {
            // H.264 and AAC first so the file plays everywhere; resolution still wins over codec.
            var sort = m.Height is { } h ? $"res:{h},vcodec:h264,acodec:aac" : "vcodec:h264,acodec:aac";
            // A video-only stream plus the best audio; a combined file only when the site has nothing else
            // (YouTube's old combined 360p carries weak audio and would end up with two audio tracks).
            args.AddRange(new[] { "-f", "bv+ba/b", "-S", sort, "--merge-output-format", "mp4" });
        }
        if (proxy is not null)
        {
            args.Add("--proxy");
            args.Add(proxy);
        }
        if (IsYouTube(item.Url))
        {
            // YouTube slows some connections down for the whole download. Cut into ~10 MB chunks fetched
            // side by side, one slow connection only costs one chunk.
            args.Add("--extractor-args");
            args.Add("youtube:formats=dashy");
        }
        AddRequestArgs(args, item.Url, item.Referrer, item.UserAgent, cookieFile);
        args.Add("--");
        args.Add(item.Url);
        return args;
    }

    /// <summary>yt-dlp keeps its partial streams here, so removing a download can clean them up.</summary>
    public static string TempFolder(DownloadItem item) => Path.Combine(item.Folder, ".vdm-" + item.Id);

    public static MediaProgress? ParseProgress(string line)
    {
        if (!line.StartsWith(ProgressPrefix, StringComparison.Ordinal)) return null;
        var f = line[ProgressPrefix.Length..].Split('|');
        if (f.Length < 6) return null;
        return new MediaProgress(Str(f[0]), Long(f[1]), Long(f[2]), Long(f[3]), Dbl(f[4]), Str(f[5]));

        static string? Str(string s) => s is "NA" or "None" or "" ? null : s;
        static double? Dbl(string s) =>
            double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
        static long? Long(string s) => Dbl(s) is { } d ? (long)d : null;
    }

    /// <summary>What the output line says yt-dlp is doing, in words a user understands.</summary>
    public static string? StageFor(string line, bool audioOnly)
    {
        if (line.StartsWith("[Merger]")) return "Đang ghép video và âm thanh";
        if (line.StartsWith("[ExtractAudio]")) return audioOnly ? "Đang chuyển sang " + (line.Contains(".m4a") ? "M4A" : "MP3") : "Đang xử lý âm thanh";
        if (line.StartsWith("[Metadata]")) return "Đang ghi thông tin bài";
        if (line.StartsWith("[Fixup") || line.StartsWith("[VideoConvertor]") || line.StartsWith("[VideoRemuxer]")) return "Đang hoàn thiện file";
        if (line.StartsWith("[download] Destination:")) return audioOnly ? "Đang tải âm thanh" : "Đang tải";
        if (line.StartsWith("[hlsnative]") || line.StartsWith("[dashsegments]")) return "Đang tải từng đoạn";
        return null;
    }

    public static MediaInfo ParseInfo(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (Str(root, "_type") is "playlist" or "multi_video")
            throw new DownloadFailedException("Đây là danh sách phát. Hãy mở từng video rồi bấm tải.");

        var info = new MediaInfo
        {
            Title = Str(root, "title") ?? "video",
            Thumbnail = Str(root, "thumbnail"),
            Duration = Num(root, "duration"),
            Uploader = Str(root, "uploader") ?? Str(root, "channel"),
            Site = Str(root, "extractor_key"),
            WebpageUrl = Str(root, "webpage_url"),
            IsLive = root.TryGetProperty("is_live", out var live) && live.ValueKind == JsonValueKind.True,
        };

        // hls: an HLS stream, which yt-dlp ranks below https and DASH; exact: its size is stated, not guessed.
        var videos = new List<(int? res, bool h264, bool hasAudio, long? size, bool hls, bool exact)>();
        var audios = new List<(bool m4a, long? size)>();
        if (root.TryGetProperty("formats", out var formats) && formats.ValueKind == JsonValueKind.Array)
        {
            foreach (var f in formats.EnumerateArray())
            {
                var vcodec = Str(f, "vcodec");
                var acodec = Str(f, "acodec");
                var ext = Str(f, "ext");
                if (ext == "mhtml" || Str(f, "format_note") == "storyboard") continue;
                var size = Size(f, info.Duration);
                var height = (int?)Num(f, "height");
                var width = (int?)Num(f, "width");
                var isAudioOnly = vcodec == "none" && acodec is not null && acodec != "none";
                if (isAudioOnly)
                {
                    audios.Add((ext == "m4a", size));
                    continue;
                }
                if (vcodec == "none") continue;
                int? res = height is > 0 ? (width is > 0 ? Math.Min(width.Value, height.Value) : height) : null;
                videos.Add((res, vcodec?.StartsWith("avc1") == true, acodec is not null && acodec != "none", size,
                    Str(f, "protocol")?.Contains("m3u8") == true, Num(f, "filesize") is > 0 || Num(f, "filesize_approx") is > 0));
            }
        }
        else
        {
            // A single direct file: yt-dlp puts its fields on the root.
            var height = (int?)Num(root, "height");
            videos.Add((height, false, true, Size(root, info.Duration), false, true));
        }

        var bestAudio = audios.OrderByDescending(a => a.m4a).ThenByDescending(a => a.size ?? 0).FirstOrDefault();
        info.AudioApproxBytes = audios.Count > 0 ? bestAudio.size : videos.Where(v => v.hasAudio).Select(v => v.size).Min();

        foreach (var group in videos.Where(v => v.res is not null).GroupBy(v => v.res!.Value).OrderByDescending(g => g.Key))
        {
            // The stream yt-dlp will take: H.264 first (as DownloadArgs asks), then https/DASH over HLS, then
            // video-only over combined (DownloadArgs asks for bv+ba). An HLS stream has no stated size, and
            // its bitrate times the length can guess four times too big.
            var pick = group.OrderByDescending(v => v.h264).ThenBy(v => v.hls).ThenBy(v => v.hasAudio)
                .ThenByDescending(v => v.exact).ThenByDescending(v => v.size ?? 0).First();
            var bytes = pick.size is { } s ? s + (pick.hasAudio ? 0 : bestAudio.size ?? 0) : (long?)null;
            info.Qualities.Add(new MediaQuality { Height = group.Key, Label = Label(group.Key), ApproxBytes = bytes });
        }
        if (info.Qualities.Count == 0 && videos.Count > 0)
            info.Qualities.Add(new MediaQuality
            {
                Height = null,
                Label = "Chất lượng gốc",
                ApproxBytes = videos.Max(v => v.size),
            });
        return info;
    }

    static string Label(int res) => res switch
    {
        >= 2160 => $"{res}p 4K",
        >= 1440 => $"{res}p 2K",
        >= 1080 => $"{res}p Full HD",
        >= 720 => $"{res}p HD",
        _ => $"{res}p",
    };

    static long? Size(JsonElement f, double? duration)
    {
        if (Num(f, "filesize") is { } a and > 0) return (long)a;
        if (Num(f, "filesize_approx") is { } b and > 0) return (long)b;
        if (Num(f, "tbr") is { } tbr and > 0 && duration is > 0) return (long)(tbr * 125 * duration.Value);
        return null;
    }

    static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static double? Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    /// <summary>Writes the browser's cookies in the Netscape format yt-dlp reads.</summary>
    public static string CookieFileText(IEnumerable<BrowserCookie> cookies)
    {
        var sb = new StringBuilder("# Netscape HTTP Cookie File\n");
        foreach (var c in cookies)
        {
            var domain = c.HostOnly ? c.Domain.TrimStart('.') : (c.Domain.StartsWith('.') ? c.Domain : "." + c.Domain);
            var expiry = c.ExpirationDate is { } e ? ((long)e).ToString(CultureInfo.InvariantCulture) : "0";
            sb.Append(domain).Append('\t')
              .Append(c.HostOnly ? "FALSE" : "TRUE").Append('\t')
              .Append(string.IsNullOrEmpty(c.Path) ? "/" : c.Path).Append('\t')
              .Append(c.Secure ? "TRUE" : "FALSE").Append('\t')
              .Append(expiry).Append('\t')
              .Append(c.Name).Append('\t')
              .Append(c.Value).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>Turns yt-dlp's last ERROR line into a sentence for the user.</summary>
    public static string FriendlyError(string? error)
    {
        if (string.IsNullOrWhiteSpace(error)) return "Không tải được video.";
        var e = error.StartsWith("ERROR:") ? error[6..].Trim() : error.Trim();
        if (e.Contains("confirm your age", StringComparison.OrdinalIgnoreCase) || e.Contains("age-restricted", StringComparison.OrdinalIgnoreCase))
            return "Video giới hạn độ tuổi, YouTube yêu cầu đăng nhập.";
        if (e.Contains("Private video", StringComparison.OrdinalIgnoreCase))
            return "Video riêng tư, không tải được.";
        if (e.Contains("Video unavailable", StringComparison.OrdinalIgnoreCase)
            || (e.Contains("not available", StringComparison.OrdinalIgnoreCase) && e.Contains("video", StringComparison.OrdinalIgnoreCase)))
            return "Video không còn hoặc bị chặn ở Việt Nam.";
        if (e.Contains("members-only", StringComparison.OrdinalIgnoreCase) || e.Contains("Join this channel", StringComparison.OrdinalIgnoreCase))
            return "Video chỉ dành cho hội viên của kênh.";
        if (e.Contains("DRM", StringComparison.Ordinal))
            return "Video có khóa bản quyền (DRM), không phần mềm nào tải được.";
        if (e.Contains("Unsupported URL", StringComparison.OrdinalIgnoreCase))
            return "Trang này chưa được hỗ trợ tải video.";
        if (e.Contains("HTTP Error 403", StringComparison.OrdinalIgnoreCase))
            return "Máy chủ video từ chối (HTTP 403). Hãy cập nhật công cụ video trong Cài đặt rồi thử lại.";
        if (e.Contains("Sign in to confirm you", StringComparison.OrdinalIgnoreCase))
            return "YouTube tạm chặn máy này vì tải nhiều. Đợi một lúc rồi thử lại.";
        return e.Length > 220 ? e[..220] + "…" : e;
    }
}

/// <summary>Runs yt-dlp for one item, turning its output into progress on the item.</summary>
public sealed class MediaDownloader(ToolPaths tools)
{
    public ToolPaths Tools { get; } = tools;

    ProcessStartInfo StartInfo(IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(Tools.YtDlp)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Tools.Folder,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["PYTHONUTF8"] = "1";
        // yt-dlp finds deno (needed for YouTube) and ffmpeg on PATH.
        psi.Environment["PATH"] = Tools.Folder + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
        return psi;
    }

    string? WriteCookies(IEnumerable<BrowserCookie>? cookies, string url)
    {
        if (cookies is null || YtDlp.IsYouTube(url)) return null;
        var list = cookies.ToList();
        if (list.Count == 0) return null;
        var path = Path.Combine(Path.GetTempPath(), $"vdm-cookies-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, YtDlp.CookieFileText(list));
        return path;
    }

    public async Task<MediaInfo> ProbeAsync(MediaRequest req, CancellationToken ct)
    {
        if (!File.Exists(Tools.YtDlp))
            throw new DownloadFailedException("Thiếu công cụ tải video (yt-dlp). Hãy cài lại phần mềm.");
        var cookieFile = WriteCookies(req.Cookies, req.Url);
        try
        {
            using var p = Process.Start(StartInfo(YtDlp.ProbeArgs(req, cookieFile)))!;
            using var reg = ct.Register(() => Kill(p));
            var stdout = p.StandardOutput.ReadToEndAsync(ct);
            var stderr = p.StandardError.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);
            var output = await stdout;
            var errors = await stderr;
            if (p.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
            {
                var last = errors.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith("ERROR:"));
                throw new DownloadFailedException(YtDlp.FriendlyError(last ?? errors.Trim()));
            }
            return YtDlp.ParseInfo(output);
        }
        finally
        {
            if (cookieFile is not null) TryDelete(cookieFile);
        }
    }

    /// <summary>Downloads into item.Folder; on success item.FileName and TotalBytes describe the final file.</summary>
    /// <param name="throttle">The download's share of the line, applied through a local proxy; null for none.</param>
    public async Task RunAsync(DownloadItem item, CancellationToken ct, Throttle? throttle = null)
    {
        if (!File.Exists(Tools.YtDlp))
            throw new DownloadFailedException("Thiếu công cụ tải video (yt-dlp). Hãy cài lại phần mềm.");
        Directory.CreateDirectory(item.Folder);
        var temp = Directory.CreateDirectory(YtDlp.TempFolder(item));
        temp.Attributes |= FileAttributes.Hidden;
        var pathFile = Path.Combine(Path.GetTempPath(), $"vdm-path-{item.Id}-{Guid.NewGuid():N}.txt");
        var cookieFile = WriteCookies(item.Media?.Cookies, item.Url);
        var audioOnly = item.Media?.AudioOnly == true;
        var tally = new MediaProgressTally(item.Media?.EstimatedBytes ?? -1);
        string? lastError = null;
        var gate = new object();
        using var log = OpenLog(item);

        void OnLine(string? line)
        {
            if (string.IsNullOrEmpty(line)) return;
            lock (gate) Handle(line);
        }

        void Handle(string line)
        {
            if (line.StartsWith("ERROR:")) lastError = line;
            if (YtDlp.ParseProgress(line) is { } pr)
            {
                tally.Apply(pr);
                lock (item.Sync)
                {
                    item.MediaDownloadedBytes = tally.Done;
                    item.TotalBytes = tally.Total;
                    if (item.Stage is null || item.Stage.StartsWith("Đang lấy")) item.Stage = audioOnly ? "Đang tải âm thanh" : "Đang tải";
                }
                return;
            }
            log?.WriteLine($"{DateTime.Now:HH:mm:ss} {line}");
            if (YtDlp.StageFor(line, audioOnly) is { } stage)
                lock (item.Sync) item.Stage = stage;
        }

        lock (item.Sync) item.Stage = "Đang lấy thông tin video";
        try
        {
            using var proxy = throttle is null ? null : new ThrottlingProxy(throttle);
            using var p = new Process { StartInfo = StartInfo(YtDlp.DownloadArgs(item, Tools, cookieFile, pathFile, proxy?.Url)) };
            p.OutputDataReceived += (_, e) => OnLine(e.Data);
            p.ErrorDataReceived += (_, e) => OnLine(e.Data);
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            using (ct.Register(() => Kill(p)))
            {
                try
                {
                    await p.WaitForExitAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    await p.WaitForExitAsync(CancellationToken.None);
                    throw;
                }
            }
            ct.ThrowIfCancellationRequested();

            var finalPath = File.Exists(pathFile)
                ? (await File.ReadAllLinesAsync(pathFile, Encoding.UTF8, CancellationToken.None)).LastOrDefault(l => l.Trim().Length > 0)?.Trim()
                : null;
            if (p.ExitCode != 0 || finalPath is null || !File.Exists(finalPath))
                throw new DownloadFailedException(YtDlp.FriendlyError(lastError));

            try { Directory.Delete(YtDlp.TempFolder(item), recursive: true); } catch { }
            var size = new FileInfo(finalPath).Length;
            lock (item.Sync)
            {
                item.Folder = Path.GetDirectoryName(finalPath)!;
                item.FileName = Path.GetFileName(finalPath);
                item.TotalBytes = size;
                item.MediaDownloadedBytes = size;
                item.Stage = null;
            }
        }
        finally
        {
            TryDelete(pathFile);
            if (cookieFile is not null) TryDelete(cookieFile);
        }
    }

    /// <summary>Where each media download keeps what yt-dlp said, for looking into a slow or failed one.</summary>
    public string? LogFolder { get; set; }

    StreamWriter? OpenLog(DownloadItem item)
    {
        if (LogFolder is null) return null;
        try
        {
            Directory.CreateDirectory(LogFolder);
            var path = Path.Combine(LogFolder, item.Id + ".log");
            if (File.Exists(path) && new FileInfo(path).Length > 1024 * 1024) File.Delete(path);
            var w = new StreamWriter(path, append: true, new UTF8Encoding(false)) { AutoFlush = true };
            w.WriteLine($"--- {DateTime.Now:yyyy-MM-dd HH:mm:ss} {item.Url}");
            return w;
        }
        catch (IOException)
        {
            return null;
        }
    }

    public async Task<string?> VersionAsync(string exe, string arg, CancellationToken ct = default)
    {
        if (!File.Exists(exe)) return null;
        try
        {
            var psi = StartInfo(new[] { arg });
            psi.FileName = exe;
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEndAsync(ct);
            var errors = p.StandardError.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);
            await errors;
            return (await output).Split('\n').FirstOrDefault()?.Trim();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Runs "yt-dlp -U"; YouTube changes often and an old yt-dlp is the usual cause of failures.</summary>
    public async Task<string> UpdateYtDlpAsync(CancellationToken ct = default)
    {
        using var p = Process.Start(StartInfo(new[] { "-U" }))!;
        var output = await p.StandardOutput.ReadToEndAsync(ct);
        var errors = await p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        var text = (output + "\n" + errors).Trim();
        return text.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? "";
    }

    static void Kill(Process p)
    {
        // yt-dlp.exe unpacks itself into a child Python process, which in turn runs ffmpeg.
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
    }

    static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }
}
