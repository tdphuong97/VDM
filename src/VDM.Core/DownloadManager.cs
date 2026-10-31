using System.Diagnostics;

namespace VDM.Core;

/// <summary>
/// Owns the download list: the queue, how many run at once, speeds, and saving everything to disk
/// so a closed app (or a crashed one) picks up where it stopped.
/// </summary>
public sealed class DownloadManager : IDisposable
{
    readonly string _itemsPath;
    readonly string _settingsPath;
    readonly HttpClient _http = HttpDownloader.CreateClient();
    readonly List<DownloadItem> _items;
    readonly Dictionary<string, (Task Task, CancellationTokenSource Cts)> _running = new();
    readonly HashSet<string> _removing = new();
    readonly Dictionary<string, Throttle> _throttles = new();
    readonly BandwidthBalancer _balancer = new();
    readonly Dictionary<string, (long Passed, double Speed)> _measured = new();
    readonly object _gate = new();
    readonly Stopwatch _clock = Stopwatch.StartNew();
    long _lastTickMs;
    long _lastSaveMs;
    bool _dirty;

    public AppSettings Settings { get; }
    public MediaDownloader Media { get; }
    public HttpClient Http => _http;
    /// <summary>Bumped on every change the UI should redraw for.</summary>
    public int Version { get; private set; }

    public event Action<DownloadItem>? Completed;
    public event Action<DownloadItem>? Failed;

    public DownloadManager(string dataFolder, ToolPaths tools)
    {
        _itemsPath = Path.Combine(dataFolder, "downloads.json");
        _settingsPath = Path.Combine(dataFolder, "settings.json");
        Settings = JsonStore.Load<AppSettings>(_settingsPath) ?? new AppSettings();
        Settings.Normalize();
        Media = new MediaDownloader(tools) { LogFolder = Path.Combine(dataFolder, "logs") };

        _items = JsonStore.Load<List<DownloadItem>>(_itemsPath) ?? new List<DownloadItem>();
        foreach (var item in _items)
        {
            // Whatever was running when the app closed carries on.
            if (item.Status == DownloadStatus.Downloading) item.Status = DownloadStatus.Queued;
            item.Stage = null;
            if (item.Status == DownloadStatus.Completed) ForgetLogin(item); // lists saved before 1.3.1 kept them
        }
    }

    /// <summary>
    /// A finished download no longer needs the browser's login: its cookies leave the saved list. They are
    /// kept only while a download may still resume.
    /// </summary>
    static void ForgetLogin(DownloadItem item)
    {
        item.CookieHeader = null;
        if (item.Media is not null) item.Media.Cookies = null;
    }

    public List<DownloadItem> Items
    {
        get { lock (_gate) return _items.ToList(); }
    }

    public DownloadItem? Find(string id)
    {
        lock (_gate) return _items.FirstOrDefault(i => i.Id == id);
    }

    ISet<string> TakenNames(string folder)
    {
        lock (_gate)
            return _items.Where(i => i.Status != DownloadStatus.Completed &&
                                     string.Equals(i.Folder, folder, StringComparison.OrdinalIgnoreCase))
                         .Select(i => i.FileName).Where(n => n.Length > 0)
                         .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Where a file of this name goes when the user did not pick a folder.</summary>
    public string FolderFor(string? fileName) =>
        string.IsNullOrWhiteSpace(fileName) ? Settings.DownloadFolder : CategoryFolder(FileNames.Category(fileName));

    public string MediaFolder(bool audioOnly) => CategoryFolder(audioOnly ? "audio" : "video");

    /// <summary>The folder the user asked VDM to remember for this type, while it exists; else the type folder.</summary>
    public string CategoryFolder(string category) =>
        Settings.RememberedFolders.TryGetValue(category, out var kept) && Directory.Exists(kept) ? kept : TypeFolder(category);

    string TypeFolder(string category)
    {
        var root = Settings.DownloadFolder;
        return Settings.SortByType && FileNames.CategoryFolders.TryGetValue(category, out var sub) ? Path.Combine(root, sub) : root;
    }

    /// <summary>Next time a file of this type goes to <paramref name="folder"/>; picking the type folder itself forgets.</summary>
    public void RememberFolder(string category, string folder)
    {
        folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        if (string.Equals(folder, Path.TrimEndingDirectorySeparator(TypeFolder(category)), StringComparison.OrdinalIgnoreCase))
        {
            ForgetFolder(category);
            return;
        }
        // A new dictionary each time: downloads read it from their own threads.
        Settings.RememberedFolders = new(Settings.RememberedFolders) { [category] = folder };
        SaveSettings();
    }

    /// <summary>Files of this type go back to their type folder, which is returned.</summary>
    public string ForgetFolder(string category)
    {
        if (Settings.RememberedFolders.ContainsKey(category))
        {
            var rest = new Dictionary<string, string>(Settings.RememberedFolders);
            rest.Remove(category);
            Settings.RememberedFolders = rest;
            SaveSettings();
        }
        return TypeFolder(category);
    }

    /// <summary>Starts what was waiting when the app last closed.</summary>
    public void StartQueued() => Pump();

    /// <summary>
    /// Creates Compressed, Programs, Documents, Video, Music and Picture in the download folder, renaming
    /// the folders VDM 1.1.0 made (Compresses, Musics…) so the files in them, and the list, follow along.
    /// </summary>
    public void EnsureCategoryFolders()
    {
        if (!Settings.SortByType) return;
        var root = Settings.DownloadFolder;
        var moved = false;
        foreach (var (oldName, newName) in FileNames.RenamedCategoryFolders)
        {
            var from = Path.Combine(root, oldName);
            var to = Path.Combine(root, newName);
            try
            {
                if (!Directory.Exists(from)) continue;
                if (Directory.Exists(to))
                {
                    if (Directory.EnumerateFileSystemEntries(to).Any()) continue; // both in use: leave them be
                    Directory.Delete(to);
                }
                Directory.Move(from, to);
                lock (_gate)
                    foreach (var item in _items)
                        if (string.Equals(item.Folder.TrimEnd('\\'), from, StringComparison.OrdinalIgnoreCase))
                            item.Folder = to;
                moved = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A file inside is open; try again next start.
            }
        }
        if (moved) Touch(save: true);

        foreach (var sub in FileNames.CategoryFolders.Values)
        {
            try { Directory.CreateDirectory(Path.Combine(Settings.DownloadFolder, sub)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    public DownloadItem AddFile(FileRequest req, string? fileName, string? folder, long? knownSize, bool start)
    {
        var picked = !string.IsNullOrWhiteSpace(folder);
        var named = !string.IsNullOrWhiteSpace(fileName);
        folder = picked ? folder! : FolderFor(fileName);
        var name = string.IsNullOrWhiteSpace(fileName) ? "" : FileNames.MakeUnique(folder, FileNames.Sanitize(fileName), TakenNames(folder));
        var item = new DownloadItem
        {
            Kind = DownloadKind.File,
            Url = req.Url,
            Referrer = req.Referrer,
            UserAgent = req.UserAgent,
            CookieHeader = req.CookieHeader,
            FileName = name,
            Folder = folder,
            AutoFolder = !picked && !named,
            TotalBytes = knownSize ?? -1,
            Connections = Settings.Connections,
            Status = start ? DownloadStatus.Queued : DownloadStatus.Paused,
        };
        Add(item);
        return item;
    }

    public DownloadItem AddMedia(MediaRequest req, MediaInfo? info, MediaOptions options, string? folder, bool start)
    {
        folder = string.IsNullOrWhiteSpace(folder) ? MediaFolder(options.AudioOnly) : folder;
        var title = info?.Title ?? req.Title ?? "video";
        options.Title ??= title;
        options.Thumbnail ??= info?.Thumbnail;
        options.Duration ??= info?.Duration;
        options.Site ??= info?.Site;
        options.Cookies ??= req.Cookies;

        long estimate = -1;
        if (info is not null)
        {
            if (options.AudioOnly) estimate = info.AudioApproxBytes ?? -1;
            else estimate = info.Qualities.FirstOrDefault(q => q.Height == options.Height)?.ApproxBytes ?? -1;
        }
        if (estimate > 0) options.EstimatedBytes = estimate;
        var ext = options.AudioOnly ? "." + options.AudioFormat : ".mp4";
        var item = new DownloadItem
        {
            Kind = DownloadKind.Media,
            Url = req.Url,
            PageUrl = req.PageUrl ?? info?.WebpageUrl,
            Referrer = req.Referrer,
            UserAgent = req.UserAgent,
            FileName = FileNames.Sanitize(title, "video") + ext,
            Folder = folder,
            TotalBytes = estimate,
            Connections = Settings.Connections,
            Media = options,
            Status = start ? DownloadStatus.Queued : DownloadStatus.Paused,
        };
        Add(item);
        return item;
    }

    void Add(DownloadItem item)
    {
        lock (_gate) _items.Insert(0, item);
        Touch(save: true);
        Pump();
    }

    public void Start(string id)
    {
        var item = Find(id);
        if (item is null) return;
        lock (_gate)
        {
            if (item.Status is DownloadStatus.Paused or DownloadStatus.Failed)
            {
                item.Status = DownloadStatus.Queued;
                item.Error = null;
            }
        }
        Touch(save: true);
        Pump();
    }

    public void Pause(string id)
    {
        var item = Find(id);
        if (item is null) return;
        lock (_gate)
        {
            if (item.Status == DownloadStatus.Queued) item.Status = DownloadStatus.Paused;
            else if (_running.TryGetValue(id, out var r)) r.Cts.Cancel();
        }
        Touch(save: true);
    }

    /// <summary>Starts over from the first byte: for expired links and corrupt partial files.</summary>
    public void Restart(string id)
    {
        var item = Find(id);
        if (item is null) return;
        Task.Run(async () =>
        {
            await StopAsync(item);
            if (item.Kind == DownloadKind.Media)
                try { Directory.Delete(YtDlp.TempFolder(item), recursive: true); } catch { }
            lock (item.Sync)
            {
                TryDelete(item.PartPath);
                item.Segments.Clear();
                item.MediaDownloadedBytes = 0;
                item.Status = DownloadStatus.Queued;
                item.Error = null;
            }
            Touch(save: true);
            Pump();
        });
    }

    public async Task RemoveAsync(string id, bool deleteFile)
    {
        var item = Find(id);
        if (item is null) return;
        lock (_gate) _removing.Add(id);
        await StopAsync(item);
        lock (_gate)
        {
            _items.Remove(item);
            _removing.Remove(id);
        }
        if (item.Status != DownloadStatus.Completed)
        {
            TryDelete(item.PartPath);
            if (item.Kind == DownloadKind.Media)
                try { Directory.Delete(YtDlp.TempFolder(item), recursive: true); } catch { }
        }
        else if (deleteFile)
        {
            TryDelete(item.FullPath);
        }
        if (Media.LogFolder is not null) TryDelete(Path.Combine(Media.LogFolder, item.Id + ".log"));
        Touch(save: true);
    }

    async Task StopAsync(DownloadItem item)
    {
        Task? task = null;
        lock (_gate)
        {
            if (_running.TryGetValue(item.Id, out var r))
            {
                r.Cts.Cancel();
                task = r.Task;
            }
        }
        if (task is not null)
            try { await task; } catch { }
    }

    public void PauseAll()
    {
        foreach (var item in Items.Where(i => i.Status is DownloadStatus.Queued or DownloadStatus.Downloading))
            Pause(item.Id);
    }

    public void ResumeAll()
    {
        foreach (var item in Items.Where(i => i.Status is DownloadStatus.Paused or DownloadStatus.Failed))
            Start(item.Id);
    }

    /// <summary>Starts queued items, oldest first, while there is a free slot.</summary>
    void Pump()
    {
        lock (_gate)
        {
            var free = Settings.MaxParallel - _running.Count;
            if (free <= 0) return;
            foreach (var item in _items.Where(i => i.Status == DownloadStatus.Queued && !_running.ContainsKey(i.Id))
                                       .OrderBy(i => i.AddedAt).Take(free).ToList())
            {
                var cts = new CancellationTokenSource();
                item.Status = DownloadStatus.Downloading;
                item.Error = null;
                var throttle = new Throttle();
                _throttles[item.Id] = throttle;
                var task = Task.Run(() => RunItemAsync(item, throttle, cts.Token));
                _running[item.Id] = (task, cts);
            }
        }
        Touch(save: false);
    }

    async Task RunItemAsync(DownloadItem item, Throttle throttle, CancellationToken ct)
    {
        try
        {
            if (item.Kind == DownloadKind.File)
            {
                var downloader = new HttpDownloader(_http, item)
                {
                    TakenNames = () => TakenNames(item.Folder),
                    FolderForName = FolderFor,
                    Limiter = throttle,
                };
                await downloader.RunAsync(ct);
            }
            else
            {
                await Media.RunAsync(item, ct, throttle);
            }
            lock (item.Sync)
            {
                item.Status = DownloadStatus.Completed;
                item.CompletedAt = DateTimeOffset.Now;
                item.Speed = 0;
                item.Stage = null;
                ForgetLogin(item);
            }
            Completed?.Invoke(item);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            lock (item.Sync)
            {
                item.Status = DownloadStatus.Paused;
                item.Stage = null;
            }
        }
        catch (Exception ex)
        {
            lock (item.Sync)
            {
                item.Status = DownloadStatus.Failed;
                item.Error = ex is DownloadFailedException ? ex.Message : "Lỗi không mong muốn: " + ex.Message;
                item.Stage = null;
            }
            Failed?.Invoke(item);
        }
        finally
        {
            item.Speed = 0;
            lock (_gate)
            {
                if (_running.Remove(item.Id, out var r)) r.Cts.Dispose();
                _throttles.Remove(item.Id);
            }
            Touch(save: true);
            Pump();
        }
    }

    void Touch(bool save)
    {
        lock (_gate)
        {
            Version++;
            if (save) _dirty = true;
        }
    }

    /// <summary>Call about twice a second: refreshes speeds and saves progress every few seconds.</summary>
    public void Tick()
    {
        var now = _clock.ElapsedMilliseconds;
        var seconds = Math.Max(0.05, (now - _lastTickMs) / 1000.0);
        _lastTickMs = now;

        List<DownloadItem> running;
        lock (_gate) running = _items.Where(i => _running.ContainsKey(i.Id)).ToList();
        foreach (var item in running)
        {
            lock (item.Sync)
            {
                var bytes = item.DownloadedBytes;
                item.Speed = Smooth(item.Speed, (bytes - item.LastSampleBytes) / seconds);
                item.LastSampleBytes = bytes;
                foreach (var s in item.Segments)
                {
                    s.Speed = s.Active ? Smooth(s.Speed, (s.Downloaded - s.LastSampleBytes) / seconds) : 0;
                    s.LastSampleBytes = s.Downloaded;
                }
            }
        }
        if (running.Count > 0) Touch(save: false);
        ShareBandwidth(seconds);

        bool save;
        lock (_gate)
        {
            save = _dirty || (running.Count > 0 && now - _lastSaveMs > 3000);
            _dirty = false;
        }
        if (save)
        {
            _lastSaveMs = now;
            Save();
        }
    }

    /// <summary>
    /// Equal parts of the line for what runs at once (see BandwidthBalancer); no limits otherwise. Speeds
    /// are measured on the bytes passing each download's throttle: yt-dlp only reports per finished chunk.
    /// </summary>
    void ShareBandwidth(double seconds)
    {
        Dictionary<string, Throttle> throttles;
        lock (_gate) throttles = new Dictionary<string, Throttle>(_throttles);
        foreach (var id in _measured.Keys.Except(throttles.Keys).ToList()) _measured.Remove(id);
        var speeds = new Dictionary<string, double>();
        foreach (var (id, t) in throttles)
        {
            var passed = t.Passed;
            var (last, speed) = _measured.TryGetValue(id, out var m) ? m : (passed, 0.0);
            speed = speed * 0.5 + Math.Max(0, passed - last) / seconds * 0.5;
            _measured[id] = (passed, speed);
            speeds[id] = speed;
        }
        if (!Settings.ShareBandwidth || throttles.Count < 2)
        {
            foreach (var t in throttles.Values) t.Rate = 0;
            return;
        }
        var limits = throttles.ToDictionary(kv => kv.Key, kv => kv.Value.Rate);
        foreach (var (id, rate) in _balancer.Balance(speeds, limits))
            if (throttles.TryGetValue(id, out var t)) t.Rate = rate;
        BalanceTrace = string.Join("  ", throttles.Select(kv =>
            $"{kv.Key[..4]} {speeds[kv.Key] / 1048576:F1}/{(kv.Value.Rate > 0 ? (kv.Value.Rate / 1048576).ToString("F1") : "∞")}"));
    }

    /// <summary>Each running download's measured speed / limit in MB/s, for looking into the sharing.</summary>
    public string? BalanceTrace { get; private set; }

    static double Smooth(double previous, double sample) =>
        sample < 0 ? 0 : previous <= 0 ? sample : previous * 0.7 + sample * 0.3;

    public void SaveSettings()
    {
        Settings.Normalize();
        JsonStore.Save(_settingsPath, Settings);
        Touch(save: false);
        Pump();
    }

    public void Save()
    {
        List<DownloadItem> copy;
        lock (_gate) copy = _items.ToList();
        // Serialize each item under its own lock: the downloader edits Segments concurrently.
        var json = new List<DownloadItem>(copy.Count);
        foreach (var item in copy)
            lock (item.Sync)
                json.Add(System.Text.Json.JsonSerializer.Deserialize<DownloadItem>(
                    System.Text.Json.JsonSerializer.Serialize(item, JsonStore.Options), JsonStore.Options)!);
        try
        {
            JsonStore.Save(_itemsPath, json);
        }
        catch (IOException)
        {
            lock (_gate) _dirty = true;
        }
    }

    public void Dispose()
    {
        List<(string Id, Task Task)> running;
        lock (_gate)
        {
            running = _running.Select(kv => (kv.Key, kv.Value.Task)).ToList();
            foreach (var r in _running.Values) r.Cts.Cancel();
        }
        try { Task.WaitAll(running.Select(r => r.Task).ToArray(), TimeSpan.FromSeconds(5)); } catch { }
        // Stopped by closing the app, not by the user: carry on next time.
        foreach (var (id, _) in running)
            if (Find(id) is { Status: DownloadStatus.Paused } item) item.Status = DownloadStatus.Queued;
        Save();
        _http.Dispose();
    }

    static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
