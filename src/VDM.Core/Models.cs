using System.Text.Json.Serialization;

namespace VDM.Core;

public enum DownloadKind { File, Media }

public enum DownloadStatus { Queued, Downloading, Paused, Completed, Failed }

/// <summary>
/// One byte range of a file, fetched by one connection. End is inclusive; End = -1 means
/// "until the server closes the stream" (size unknown).
/// </summary>
public sealed class Segment
{
    public long Start { get; set; }
    public long End { get; set; }
    public long Downloaded { get; set; }

    [JsonIgnore] public bool Active { get; set; }
    [JsonIgnore] public double Speed { get; set; }
    [JsonIgnore] internal long LastSampleBytes { get; set; }

    [JsonIgnore] public long Position => Start + Downloaded;
    [JsonIgnore] public long Length => End < 0 ? -1 : End - Start + 1;
    [JsonIgnore] public long Remaining => End < 0 ? long.MaxValue : End - Position + 1;
    [JsonIgnore] public bool IsDone => End >= 0 && Position > End;
}

public sealed class MediaOptions
{
    public bool AudioOnly { get; set; }
    /// <summary>Smaller side of the frame (1080 for 1920x1080 and for a 1080x1920 short); null = best.</summary>
    public int? Height { get; set; }
    public string AudioFormat { get; set; } = "mp3";
    public string? Title { get; set; }
    public string? Thumbnail { get; set; }
    public double? Duration { get; set; }
    public string? Site { get; set; }
    /// <summary>The size worked out from the chosen formats before starting, when yt-dlp said.</summary>
    public long? EstimatedBytes { get; set; }
    /// <summary>The browser's cookies for sites that need a login; never sent for YouTube.</summary>
    public List<BrowserCookie>? Cookies { get; set; }
}

public sealed class BrowserCookie
{
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";
    public string Domain { get; set; } = "";
    public string Path { get; set; } = "/";
    public bool Secure { get; set; }
    public bool HostOnly { get; set; }
    public double? ExpirationDate { get; set; }
}

public sealed class DownloadItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public DownloadKind Kind { get; set; }
    public string Url { get; set; } = "";
    public string? PageUrl { get; set; }
    public string? Referrer { get; set; }
    public string? UserAgent { get; set; }
    public string? CookieHeader { get; set; }

    public string FileName { get; set; } = "";
    public string Folder { get; set; } = "";
    /// <summary>The name comes from the server: pick the type folder once it is known.</summary>
    public bool AutoFolder { get; set; }
    public long TotalBytes { get; set; } = -1;
    public bool ResumeSupported { get; set; }
    public int Connections { get; set; } = 8;
    public List<Segment> Segments { get; set; } = new();

    public DownloadStatus Status { get; set; } = DownloadStatus.Queued;
    public string? Error { get; set; }
    /// <summary>What a media download is doing right now ("Đang tải video", "Đang ghép").</summary>
    public string? Stage { get; set; }
    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset? CompletedAt { get; set; }
    public MediaOptions? Media { get; set; }

    /// <summary>Bytes fetched; for media downloads this is fed by yt-dlp progress.</summary>
    public long MediaDownloadedBytes { get; set; }

    [JsonIgnore] public double Speed { get; set; }
    [JsonIgnore] internal long LastSampleBytes { get; set; }
    /// <summary>Guards Segments and the byte counters; the downloader and the UI snapshot share it.</summary>
    [JsonIgnore] public object Sync { get; } = new();

    [JsonIgnore]
    public long DownloadedBytes => Kind == DownloadKind.Media
        ? MediaDownloadedBytes
        : Segments.Sum(s => s.Downloaded);

    [JsonIgnore] public string FullPath => Path.Combine(Folder, FileName);
    [JsonIgnore] public string PartPath => FullPath + ".vdmpart";
}

/// <summary>A file link sent by the browser extension or typed by the user.</summary>
public sealed class FileRequest
{
    public string Url { get; set; } = "";
    public string? Referrer { get; set; }
    public string? UserAgent { get; set; }
    public string? CookieHeader { get; set; }
    public string? FileName { get; set; }
    public long? FileSize { get; set; }
    public string? Mime { get; set; }
}

/// <summary>A video page or stream sent by the extension; yt-dlp decides what can be fetched.</summary>
public sealed class MediaRequest
{
    public string Url { get; set; } = "";
    public string? PageUrl { get; set; }
    public string? Title { get; set; }
    public string? Referrer { get; set; }
    public string? UserAgent { get; set; }
    public List<BrowserCookie>? Cookies { get; set; }
}
