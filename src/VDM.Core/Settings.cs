using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VDM.Core;

public sealed class AppSettings
{
    /// <summary>IDM's own list. R0* and R1* are the parts of a split RAR archive (.r00, .r01 … .r19).</summary>
    public static readonly string[] DefaultCaptureExtensions =
        ("3gp 7z aac ace aif apk arj asf avi bin bz2 exe gz gzip img iso lzh m4a m4v mkv mov mp3 mp4 mpa mpe " +
         "mpeg mpg msi msu ogg ogv pdf plj pps ppt qt r0* r1* ra rar rm rmvb sea sit sitx tar tif tiff wav wma " +
         "wmv z zip").Split(' ');

    /// <summary>The list VDM 1.0 saved as its default; a settings file still holding it moves to IDM's list.</summary>
    public static readonly string[] Version10Extensions =
    {
        "3gp", "7z", "aac", "apk", "avi", "bin", "bz2", "dmg", "exe", "flac", "gz", "img", "iso",
        "m4a", "m4v", "mkv", "mov", "mp3", "mp4", "mpeg", "mpg", "msi", "msix", "ogg", "pdf", "rar",
        "tar", "wav", "webm", "wma", "wmv", "xz", "zip",
    };

    public string DownloadFolder { get; set; } = KnownFolders.Downloads;
    public int Connections { get; set; } = 8;
    public int MaxParallel { get; set; } = 3;
    /// <summary>Downloads running at once share the line equally (each one held to its part).</summary>
    public bool ShareBandwidth { get; set; } = true;

    public bool CaptureEnabled { get; set; } = true;
    /// <summary>Catch every file the browser downloads; off, only CaptureExtensions.</summary>
    public bool CaptureAll { get; set; } = true;
    /// <summary>Put files into Compressed, Programs, Documents, Video, Music or Picture by type, as IDM does.</summary>
    public bool SortByType { get; set; } = true;
    /// <summary>
    /// A folder the user picked in the download dialog and asked VDM to keep, by type (video, audio,
    /// archive…): the next file of that type goes there instead of its type folder.
    /// </summary>
    public Dictionary<string, string> RememberedFolders { get; set; } = new();
    public bool AskBeforeDownload { get; set; } = true;
    public List<string> CaptureExtensions { get; set; } = DefaultCaptureExtensions.ToList();
    /// <summary>Files smaller than this stay with the browser (0 = catch every listed type).</summary>
    public int CaptureMinSizeKb { get; set; }

    public int VideoHeight { get; set; } = 1080;
    public string AudioFormat { get; set; } = "mp3";

    public bool StartWithWindows { get; set; }
    public bool CloseToTray { get; set; } = true;
    public bool AutoUpdateTools { get; set; } = true;
    public DateTimeOffset? ToolsCheckedAt { get; set; }
    /// <summary>Look for a newer VDM once a day.</summary>
    public bool AutoCheckUpdates { get; set; } = true;
    public DateTimeOffset? UpdateCheckedAt { get; set; }

    public void Normalize()
    {
        Connections = Math.Clamp(Connections, 1, 32);
        MaxParallel = Math.Clamp(MaxParallel, 1, 10);
        CaptureMinSizeKb = Math.Max(0, CaptureMinSizeKb);
        if (string.IsNullOrWhiteSpace(DownloadFolder)) DownloadFolder = KnownFolders.Downloads;
        AudioFormat = AudioFormat is "m4a" ? "m4a" : "mp3";
        RememberedFolders = (RememberedFolders ?? new())
            .Where(p => !string.IsNullOrWhiteSpace(p.Value) && Path.IsPathFullyQualified(p.Value))
            .ToDictionary(p => p.Key, p => p.Value);
        var list = CaptureExtensions
            .Select(e => e.Trim().TrimStart('.').ToLowerInvariant())
            .Where(e => e.Length is > 0 and <= 10 && e.All(c => char.IsLetterOrDigit(c) || c is '*' or '?'))
            .Distinct().ToList();
        if (list.ToHashSet().SetEquals(Version10Extensions)) list = DefaultCaptureExtensions.ToList();
        CaptureExtensions = list.OrderBy(e => e, StringComparer.Ordinal).ToList();
    }
}

public static class KnownFolders
{
    static readonly Guid DownloadsId = new("374DE290-123F-4565-9164-39C4925E467B");

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint flags, IntPtr token, out IntPtr path);

    public static string Downloads
    {
        get
        {
            if (OperatingSystem.IsWindows() && SHGetKnownFolderPath(DownloadsId, 0, IntPtr.Zero, out var ptr) == 0)
            {
                try { return Marshal.PtrToStringUni(ptr)!; }
                finally { Marshal.FreeCoTaskMem(ptr); }
            }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        }
    }
}

/// <summary>Reads and writes JSON files atomically, so a crash mid-save never loses the list.</summary>
public static class JsonStore
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static T? Load<T>(string path)
    {
        foreach (var candidate in new[] { path, path + ".bak" })
        {
            try
            {
                if (File.Exists(candidate))
                    return JsonSerializer.Deserialize<T>(File.ReadAllText(candidate), Options);
            }
            catch (JsonException)
            {
            }
        }
        return default;
    }

    public static void Save<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, Options));
        if (File.Exists(path)) File.Replace(tmp, path, path + ".bak", ignoreMetadataErrors: true);
        else File.Move(tmp, path);
    }
}
