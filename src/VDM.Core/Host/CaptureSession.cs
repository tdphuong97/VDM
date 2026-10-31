using System.Text.Json;

namespace VDM.Core.Host;

/// <summary>
/// The commands of one capture dialog: what was caught, its size or video formats, and adding the
/// download. The window around it belongs to the platform, which passes in how to close and resize it.
/// </summary>
public sealed class CaptureSession(AppHost host, CaptureRequest request, object? owner, Action close, Action<int> fitHeight)
{
    readonly CancellationTokenSource _cts = new();
    MediaInfo? _media;
    // Asking about the link starts with the session, while the platform is still opening the window and
    // its web view; the page then picks up the answer instead of starting from zero.
    Task<MediaInfo>? _probe;
    Task<HttpDownloader.LinkInfo>? _inspect;
    bool _probeTaken, _inspectTaken;

    public CaptureRequest Request { get; } = request;
    public string Title => Request.IsMedia ? "VDM - Tải video" : "VDM - Tải file";

    /// <summary>The window closed: stop asking the server or yt-dlp.</summary>
    public void Cancel() => _cts.Cancel();

    /// <summary>Starts asking about the link; the platform calls it as soon as the session exists.</summary>
    public void Begin()
    {
        if (Request.IsMedia) _probe ??= Probe();
        else _inspect ??= Inspect();
    }

    Task<MediaInfo> Probe() => Task.Run(() => host.Manager.Media.ProbeAsync(Request.Media!, _cts.Token));
    Task<HttpDownloader.LinkInfo> Inspect() => Task.Run(() => HttpDownloader.InspectAsync(host.Manager.Http, Request.File!, _cts.Token));

    public async Task<object?> HandleAsync(string cmd, JsonElement args)
    {
        var manager = host.Manager;
        var settings = manager.Settings;
        switch (cmd)
        {
            case "captureInit":
                return new
                {
                    mode = Request.IsMedia ? "media" : "file",
                    request = Request.IsMedia ? (object?)Request.Media : Request.File,
                    audioOnly = Request.AudioOnly,
                    platform = host.Platform.Name,
                    settings = new { downloadFolder = settings.DownloadFolder, settings.VideoHeight, settings.AudioFormat },
                    folders = new
                    {
                        video = manager.MediaFolder(audioOnly: false),
                        audio = manager.MediaFolder(audioOnly: true),
                        file = manager.FolderFor(Request.File?.FileName),
                    },
                    fileCategory = string.IsNullOrWhiteSpace(Request.File?.FileName) ? null : FileNames.Category(Request.File.FileName),
                    remembered = settings.RememberedFolders,
                };
            case "inspectFile":
                // The page's first ask takes the answer already on its way; asking again ("Thử lại") starts over.
                if (_inspect is null || _inspectTaken) _inspect = Inspect();
                _inspectTaken = true;
                var info = await _inspect;
                var shownName = string.IsNullOrWhiteSpace(Request.File!.FileName) ? info.FileName : FileNames.Sanitize(Request.File.FileName);
                return new
                {
                    fileName = shownName, info.Size, info.Resumable, info.Mime, folder = manager.FolderFor(shownName),
                    category = string.IsNullOrWhiteSpace(shownName) ? null : FileNames.Category(shownName),
                };
            case "probeMedia":
                if (_probe is null || _probeTaken) _probe = Probe();
                _probeTaken = true;
                _media = await _probe;
                return _media;
            case "confirmFile":
                if (args.Bool("remember") && args.Str("fileName") is { Length: > 0 } named)
                    Remember(FileNames.Category(named), args.Str("folder"));
                manager.AddFile(Request.File!, args.Str("fileName"), args.Str("folder"),
                    args.Long("size") is > 0 and var size ? size : Request.File!.FileSize, args.Bool("start"));
                host.ShowListIfOpen();
                close();
                return true;
            case "confirmMedia":
                var options = new MediaOptions
                {
                    AudioOnly = args.Bool("audioOnly"),
                    Height = args.Int("height"),
                    AudioFormat = args.Str("audioFormat") is "m4a" ? "m4a" : "mp3",
                };
                // "Tải" may come before the qualities did: then the download asks yt-dlp itself.
                if (args.Bool("remember")) Remember(options.AudioOnly ? "audio" : "video", args.Str("folder"));
                var known = _media ?? (_probe is { IsCompletedSuccessfully: true } done ? done.Result : null);
                manager.AddMedia(Request.Media!, known, options, args.Str("folder"), args.Bool("start"));
                host.ShowListIfOpen();
                close();
                return true;
            case "chooseFolder":
                return await host.Platform.ChooseFolderAsync(owner, args.Str("current"));
            case "forgetFolder":
                var typeFolder = manager.ForgetFolder(args.Str("category"));
                host.PushState(force: true);
                return typeFolder;
            case "resize":
                fitHeight(args.Int("height") ?? 0);
                return true;
            case "close":
                close();
                return true;
        }
        return null;
    }

    void Remember(string category, string? folder)
    {
        if (!string.IsNullOrWhiteSpace(folder)) host.Manager.RememberFolder(category, folder);
    }
}
