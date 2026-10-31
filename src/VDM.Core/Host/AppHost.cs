using System.Text.Json;

namespace VDM.Core.Host;

/// <summary>The main window as the host sees it: whether its page is listening, and how to tell it things.</summary>
public interface IMainView
{
    /// <summary>The page is loaded and on screen, so pushing state to it is worth the work.</summary>
    bool Ready { get; }
    void Emit(string evt, object? data);
}

/// <summary>
/// Everything VDM does that is the same on every system: the download manager, the door the browser
/// extension knocks on, the commands the pages send, and the state the main window shows.
/// </summary>
public sealed class AppHost : IDisposable
{
    static readonly string[] MediaHosts =
    {
        "youtube.com", "youtu.be", "facebook.com", "fb.watch", "tiktok.com", "vimeo.com", "dailymotion.com",
        "instagram.com", "twitter.com", "x.com", "soundcloud.com", "bilibili.com", "bilibili.tv", "twitch.tv",
        "zingmp3.vn", "nhaccuatui.com", "reddit.com", "threads.net",
    };

    readonly string _browsersPath;
    readonly Dictionary<string, DateTimeOffset> _browsers;
    int _lastVersion = -1;
    DateTime _lastPush;
    object? _tools;
    (DateTime at, object? value) _folderCache;
    IMainView? _main;
    readonly string _dataFolder;
    readonly Updater _updater;
    readonly CancellationTokenSource _stopping = new();
    UpdateInfo? _update;
    string? _updateStage;   // "downloading", then "installing"
    int _updatePercent;
    string? _updateError;
    string? _updateNotified;

    public IPlatform Platform { get; }
    public DownloadManager Manager { get; }
    public LocalApi? Api { get; private set; }
    public string Version { get; }

    public AppHost(IPlatform platform, string dataFolder, string toolsFolder, string version)
    {
        Platform = platform;
        Version = version;
        _dataFolder = dataFolder;
        Directory.CreateDirectory(dataFolder);
        Manager = new DownloadManager(dataFolder, new ToolPaths(toolsFolder));
        _browsersPath = Path.Combine(dataFolder, "browsers.json");
        _browsers = JsonStore.Load<Dictionary<string, DateTimeOffset>>(_browsersPath) ?? new();
        // VDM_UPDATE_FEED points a test VDM at a feed of its own.
        _updater = new Updater(Manager.Http, Environment.GetEnvironmentVariable("VDM_UPDATE_FEED") is { Length: > 0 } feed ? feed : Updater.DefaultFeed);
    }

    public void AttachMain(IMainView main) => _main = main;

    public void Start()
    {
        // The installer may have set the start-up entry; the setting shows what the system will really do.
        try { Manager.Settings.StartWithWindows = Platform.StartsWithSystem(); } catch (Exception) { }
        try
        {
            Api = LocalApi.Start(ExtensionConfig);
            Api.FileCaptured += r => Platform.Post(() => OnFileCaptured(r));
            Api.MediaCaptured += r => Platform.Post(() => Platform.OpenCapture(new CaptureRequest { IsMedia = true, Media = r }));
            Api.ShowRequested += () => Platform.Post(() => Platform.ShowMain(null));
        }
        catch (InvalidOperationException ex)
        {
            Platform.Warn(ex.Message + "\nPhần mềm vẫn chạy, nhưng chưa bắt được link từ trình duyệt.");
        }

        Manager.Completed += item => Platform.Post(() => { Platform.Notify("Tải xong", item.FileName, item); PushState(force: true); });
        Manager.Failed += item => Platform.Post(() => { Platform.Notify("Tải không được", item.FileName + "\n" + item.Error, item); PushState(force: true); });

        Manager.EnsureCategoryFolders();
        Manager.StartQueued();
        _ = LoadToolsAsync(autoUpdate: true);
        _ = WatchForUpdatesAsync();
    }

    object ExtensionConfig()
    {
        var s = Manager.Settings;
        return new
        {
            app = "VDM",
            version = Version,
            capture = new { enabled = s.CaptureEnabled, all = s.CaptureAll, extensions = s.CaptureExtensions, minSizeKb = s.CaptureMinSizeKb },
        };
    }

    // ---------- capture ----------
    void OnFileCaptured(FileRequest r)
    {
        if (!Manager.Settings.AskBeforeDownload)
        {
            var item = Manager.AddFile(r, r.FileName, null, r.FileSize, start: true);
            Platform.Notify("Đã nhận file", item.FileName.Length > 0 ? item.FileName : r.Url, null);
            PushState(force: true);
            return;
        }
        Platform.OpenCapture(new CaptureRequest { File = r });
    }

    public static bool LooksLikeMedia(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
        var path = u.AbsolutePath.ToLowerInvariant();
        if (path.EndsWith(".m3u8") || path.EndsWith(".mpd")) return true;
        var host = u.Host.ToLowerInvariant();
        return MediaHosts.Any(h => host == h || host.EndsWith("." + h));
    }

    /// <summary>A capture dialog added a download: bring the list up to date.</summary>
    public void ShowListIfOpen()
    {
        if (_main is { Ready: true }) _main.Emit("show", "downloads");
        PushState(force: true);
    }

    // ---------- main window commands ----------
    public async Task<object?> HandleMainAsync(string cmd, JsonElement args)
    {
        var id = args.Str("id");
        switch (cmd)
        {
            case "ready":
                return new
                {
                    state = BuildState(),
                    settings = Manager.Settings,
                    tools = _tools,
                    version = Version,
                    platform = Platform.Name,
                    extension = new
                    {
                        chromium = Platform.ExtensionChromium,
                        firefox = Platform.ExtensionFirefox,
                        firefoxXpi = File.Exists(Platform.FirefoxXpi),
                    },
                };
            case "pause": Manager.Pause(id); break;
            case "resume": Manager.Start(id); break;
            case "restart": Manager.Restart(id); break;
            case "remove": await Manager.RemoveAsync(id, args.Bool("deleteFile")); break;
            case "pauseAll": Manager.PauseAll(); break;
            case "resumeAll": Manager.ResumeAll(); break;
            case "open":
                if (Manager.Find(id) is { } open) Platform.OpenFile(open.FullPath);
                break;
            case "reveal":
                if (Manager.Find(id) is { } reveal)
                    Platform.Reveal(reveal.Status == DownloadStatus.Completed ? reveal.FullPath : reveal.Folder);
                break;
            case "copyText": Platform.CopyText(args.Str("text")); break;
            case "openUrl": Platform.OpenUrl(args.Str("url")); break;
            case "clipboardUrl": return Platform.ClipboardUrl();
            case "addLink":
                var url = args.Str("url");
                if (LooksLikeMedia(url))
                    Platform.OpenCapture(new CaptureRequest { IsMedia = true, Media = new MediaRequest { Url = url, PageUrl = url } });
                else
                    Platform.OpenCapture(new CaptureRequest { File = new FileRequest { Url = url } });
                break;
            case "saveSettings":
                return SaveSettings(args.GetProperty("settings"));
            case "chooseFolder":
                return await Platform.ChooseFolderAsync(null, args.Str("current"));
            case "forgetFolder": Manager.ForgetFolder(args.Str("category")); break;
            case "checkUpdate":
                await CheckUpdateAsync();
                return UpdateState();
            case "installUpdate":
                _ = InstallUpdateAsync();
                return true;
            case "updateTools":
                var message = await Manager.Media.UpdateYtDlpAsync();
                Manager.Settings.ToolsCheckedAt = DateTimeOffset.Now;
                Manager.SaveSettings();
                await LoadToolsAsync(autoUpdate: false);
                return new { message = Translate(message), tools = _tools };
            case "browserPage":
                var browser = args.Str("browser");
                if (!Platform.OpenInBrowser(browser, args.Str("page")))
                    throw new InvalidOperationException($"Không tìm thấy {browser} trên máy này.");
                break;
            case "extensionFolder":
                Platform.Reveal(args.Str("browser") == "Firefox" ? Platform.ExtensionFirefox : Platform.ExtensionChromium);
                break;
            case "quit":
                Platform.Post(Platform.Quit);
                return true;
            case "installFirefox":
                if (!Platform.OpenInBrowser("Firefox", Platform.FirefoxXpi))
                    throw new InvalidOperationException("Không tìm thấy Firefox trên máy này.");
                break;
        }
        PushState(force: true);
        return true;
    }

    static string Translate(string ytdlp)
    {
        if (ytdlp.Contains("up to date", StringComparison.OrdinalIgnoreCase)) return "Công cụ tải video đã là bản mới nhất.";
        if (ytdlp.Contains("Updated yt-dlp", StringComparison.OrdinalIgnoreCase)) return "Đã cập nhật công cụ tải video.";
        return string.IsNullOrWhiteSpace(ytdlp) ? "Không kiểm tra được, hãy thử lại khi có mạng." : ytdlp;
    }

    object SaveSettings(JsonElement json)
    {
        var incoming = json.Deserialize<AppSettings>(JsonStore.Options) ?? new AppSettings();
        var s = Manager.Settings;
        var startChanged = s.StartWithWindows != incoming.StartWithWindows;
        s.DownloadFolder = incoming.DownloadFolder;
        s.Connections = incoming.Connections;
        s.MaxParallel = incoming.MaxParallel;
        s.ShareBandwidth = incoming.ShareBandwidth;
        s.CaptureEnabled = incoming.CaptureEnabled;
        s.CaptureAll = incoming.CaptureAll;
        s.SortByType = incoming.SortByType;
        s.AskBeforeDownload = incoming.AskBeforeDownload;
        s.CaptureExtensions = incoming.CaptureExtensions;
        s.CaptureMinSizeKb = incoming.CaptureMinSizeKb;
        s.VideoHeight = incoming.VideoHeight;
        s.AudioFormat = incoming.AudioFormat;
        s.StartWithWindows = incoming.StartWithWindows;
        s.CloseToTray = incoming.CloseToTray;
        s.AutoUpdateTools = incoming.AutoUpdateTools;
        s.AutoCheckUpdates = incoming.AutoCheckUpdates;
        Manager.SaveSettings();
        if (startChanged)
            try { Platform.SetStartWithSystem(s.StartWithWindows); } catch (Exception) { }
        _folderCache = default;
        Manager.EnsureCategoryFolders();
        return s;
    }

    async Task LoadToolsAsync(bool autoUpdate)
    {
        var s = Manager.Settings;
        if (autoUpdate && s.AutoUpdateTools && File.Exists(Manager.Media.Tools.YtDlp) &&
            (s.ToolsCheckedAt is null || DateTimeOffset.Now - s.ToolsCheckedAt > TimeSpan.FromDays(7)))
        {
            try
            {
                await Manager.Media.UpdateYtDlpAsync();
                s.ToolsCheckedAt = DateTimeOffset.Now;
                Manager.SaveSettings();
            }
            catch (Exception) { }
        }
        var t = Manager.Media.Tools;
        var ytdlp = await Manager.Media.VersionAsync(t.YtDlp, "--version");
        var ffmpeg = await Manager.Media.VersionAsync(t.Ffmpeg, "-version");
        var deno = await Manager.Media.VersionAsync(t.Deno, "--version");
        _tools = new
        {
            ytdlp,
            ffmpeg = ShortFfmpeg(ffmpeg),
            deno = deno?.Split(' ').ElementAtOrDefault(1),
            checkedAt = s.ToolsCheckedAt,
        };
    }

    static string? ShortFfmpeg(string? line)
    {
        var v = line?.Split(' ').ElementAtOrDefault(2);
        if (v is null) return null;
        if (v.StartsWith("N-")) return "bản " + v.Split('-').Last();
        var dash = v.IndexOf('-');
        return dash > 0 ? v[..dash] : v;
    }

    // ---------- updating VDM itself ----------
    /// <summary>Looks for a newer VDM a minute after starting, then once a day while VDM runs.</summary>
    async Task WatchForUpdatesAsync()
    {
        // The installer of the last update is done with by now.
        try { Directory.Delete(Path.Combine(_dataFolder, "updates"), recursive: true); } catch (Exception) { }
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(1), _stopping.Token);
            while (true)
            {
                var s = Manager.Settings;
                if (s.AutoCheckUpdates && (s.UpdateCheckedAt is null || DateTimeOffset.Now - s.UpdateCheckedAt > TimeSpan.FromHours(20)))
                    try { await CheckUpdateAsync(); } catch (UpdateException) { /* no network: try again later */ }
                await Task.Delay(TimeSpan.FromHours(1), _stopping.Token);
            }
        }
        catch (OperationCanceledException) { }
    }

    async Task CheckUpdateAsync()
    {
        var found = await _updater.CheckAsync(Version, Platform.Name, _stopping.Token);
        // Set at once, so "Kiểm tra bản mới" answers with what it found; saved on the UI thread.
        _update = found;
        _updateError = null;
        Manager.Settings.UpdateCheckedAt = DateTimeOffset.Now;
        Platform.Post(() =>
        {
            Manager.SaveSettings();
            if (found is not null && _updateNotified != found.Version)
            {
                _updateNotified = found.Version;
                Platform.Notify("Có bản VDM mới", $"Bản {found.Version} đã có. Mở VDM và bấm Cập nhật.", null);
            }
            PushState(force: true);
        });
    }

    /// <summary>Downloads the new installer, checks it, and hands over to it; VDM closes and comes back updated.</summary>
    async Task InstallUpdateAsync()
    {
        var info = _update;
        if (info is null || _updateStage is not null) return;
        if (info.Package is null || !Platform.CanInstallUpdates)
        {
            if (info.Page is not null) Platform.OpenUrl(info.Page);
            return;
        }
        _updateStage = "downloading";
        _updatePercent = 0;
        _updateError = null;
        PushState(force: true);
        try
        {
            var path = await _updater.DownloadAsync(info, Path.Combine(_dataFolder, "updates"), p =>
            {
                var percent = (int)(p * 100);
                if (percent == _updatePercent) return;
                _updatePercent = percent;
                Platform.Post(() => PushState(force: true));
            }, _stopping.Token);
            Platform.Post(() =>
            {
                _updateStage = "installing";
                PushState(force: true);
                Platform.InstallUpdate(path);
            });
        }
        catch (UpdateException ex)
        {
            Platform.Post(() =>
            {
                _updateStage = null;
                _updateError = ex.Message;
                PushState(force: true);
            });
        }
        catch (OperationCanceledException) { }
    }

    object UpdateState() => new
    {
        current = Version,
        available = _update is null ? null : new
        {
            version = _update.Version,
            notes = _update.Notes,
            page = _update.Page,
            size = _update.Package?.Size,
            canInstall = _update.Package is not null && Platform.CanInstallUpdates,
        },
        stage = _updateStage,
        percent = _updatePercent,
        error = _updateError,
        checkedAt = Manager.Settings.UpdateCheckedAt,
    };

    // ---------- state for the main window ----------
    /// <summary>Call about twice a second on the UI thread.</summary>
    public void Tick()
    {
        Manager.Tick();
        if (Api is not null)
        {
            var changed = false;
            foreach (var (name, at) in Api.BrowsersSeen)
                if (!_browsers.TryGetValue(name, out var old) || at - old > TimeSpan.FromMinutes(1))
                {
                    _browsers[name] = at;
                    changed = true;
                }
            if (changed)
            {
                try { JsonStore.Save(_browsersPath, _browsers); } catch (IOException) { }
                PushState(force: true);
                return;
            }
        }
        PushState(force: false);
    }

    public void PushState(bool force)
    {
        if (_main is not { Ready: true }) return;
        if (!force && Manager.Version == _lastVersion && DateTime.UtcNow - _lastPush < TimeSpan.FromSeconds(10)) return;
        _lastVersion = Manager.Version;
        _lastPush = DateTime.UtcNow;
        _main.Emit("state", BuildState());
    }

    object BuildState() => new
    {
        items = Manager.Items.Select(ItemDto).ToList(),
        folder = FolderInfo(),
        remembered = Manager.Settings.RememberedFolders,
        update = UpdateState(),
        browsers = _browsers.Select(b => new { name = b.Key, lastSeen = b.Value }).ToList(),
    };

    object? FolderInfo()
    {
        if (DateTime.UtcNow - _folderCache.at < TimeSpan.FromSeconds(5)) return _folderCache.value;
        object? value = null;
        try
        {
            var folder = Path.GetFullPath(Manager.Settings.DownloadFolder);
            // The volume holding the folder: the longest mount point that is a prefix of its path.
            var d = DriveInfo.GetDrives()
                .Where(x => x.IsReady && folder.StartsWith(x.RootDirectory.FullName, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(x => x.RootDirectory.FullName.Length)
                .FirstOrDefault();
            if (d is not null)
            {
                var drive = OperatingSystem.IsWindows() ? d.Name.TrimEnd('\\') : "Macintosh HD";
                value = new { path = folder, drive, freeBytes = d.AvailableFreeSpace, totalBytes = d.TotalSize };
            }
        }
        catch (Exception) { }
        _folderCache = (DateTime.UtcNow, value);
        return value;
    }

    static object ItemDto(DownloadItem i)
    {
        lock (i.Sync)
        {
            var name = i.FileName.Length > 0 ? i.FileName
                : i.Media?.Title ?? (Uri.TryCreate(i.Url, UriKind.Absolute, out var u) ? FileNames.FromUrl(u) : null) ?? i.Url;
            var segments = i.Kind == DownloadKind.File && i.Status != DownloadStatus.Completed && i.Segments.Count > 1
                ? i.Segments.Take(64).Select(s => new[] { s.Start, s.End, s.Downloaded, (long)s.Speed }).ToList()
                : null;
            return new
            {
                id = i.Id,
                kind = i.Kind == DownloadKind.Media ? "media" : "file",
                name,
                folder = i.Folder,
                url = i.Url,
                pageUrl = i.PageUrl,
                referrer = i.Referrer,
                total = i.TotalBytes,
                done = i.DownloadedBytes,
                speed = Math.Round(i.Speed),
                status = i.Status.ToString().ToLowerInvariant(),
                error = i.Error,
                stage = i.Stage,
                addedAt = i.AddedAt,
                completedAt = i.CompletedAt,
                resumable = i.Kind == DownloadKind.Media || i.ResumeSupported,
                connections = i.Kind == DownloadKind.File ? i.Connections : 1,
                category = FileNames.Category(name),
                media = i.Media is null ? null : new
                {
                    audioOnly = i.Media.AudioOnly,
                    height = i.Media.Height,
                    audioFormat = i.Media.AudioFormat,
                    title = i.Media.Title,
                    thumbnail = i.Media.Thumbnail,
                    duration = i.Media.Duration,
                    site = i.Media.Site,
                },
                segments,
            };
        }
    }

    public void Dispose()
    {
        _stopping.Cancel();
        Api?.Dispose();
        Manager.Dispose();
    }
}
