using System.Net.Http.Headers;
using System.Text;

namespace VDM.Core;

public static class FileNames
{
    static readonly char[] Invalid = Path.GetInvalidFileNameChars()
        .Concat(new[] { '<', '>', ':', '"', '/', '\\', '|', '?', '*' }).Distinct().ToArray();

    static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    static readonly Dictionary<string, string> MimeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["application/zip"] = ".zip",
        ["application/x-zip-compressed"] = ".zip",
        ["application/x-rar-compressed"] = ".rar",
        ["application/vnd.rar"] = ".rar",
        ["application/x-7z-compressed"] = ".7z",
        ["application/pdf"] = ".pdf",
        ["application/x-msdownload"] = ".exe",
        ["application/x-msi"] = ".msi",
        ["application/x-iso9660-image"] = ".iso",
        ["video/mp4"] = ".mp4",
        ["video/webm"] = ".webm",
        ["video/x-matroska"] = ".mkv",
        ["audio/mpeg"] = ".mp3",
        ["audio/mp4"] = ".m4a",
        ["image/jpeg"] = ".jpg",
        ["image/png"] = ".png",
    };

    /// <summary>Makes a name safe for Windows: no reserved characters or device names, no trailing dots.</summary>
    public static string Sanitize(string? name, string fallback = "download")
    {
        if (string.IsNullOrWhiteSpace(name)) return fallback;
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
            sb.Append(Array.IndexOf(Invalid, c) >= 0 || char.IsControl(c) ? '_' : c);
        var s = sb.ToString().Trim().TrimEnd('.', ' ');
        if (s.Length == 0) return fallback;

        var stem = Path.GetFileNameWithoutExtension(s);
        if (Reserved.Contains(stem)) s = "_" + s;

        if (s.Length > 180)
        {
            var ext = Path.GetExtension(s);
            if (ext.Length > 12) ext = "";
            s = s[..(180 - ext.Length)].TrimEnd('.', ' ') + ext;
        }
        return s;
    }

    /// <summary>Reads filename* (RFC 5987) first, then filename, from a Content-Disposition header.</summary>
    public static string? FromContentDisposition(ContentDispositionHeaderValue? cd)
    {
        if (cd is null) return null;
        var star = cd.FileNameStar;
        if (!string.IsNullOrWhiteSpace(star)) return star.Trim('"');
        var plain = cd.FileName?.Trim('"');
        if (string.IsNullOrWhiteSpace(plain)) return null;
        return DecodeLatin1Utf8(Uri.UnescapeDataString(plain));
    }

    /// <summary>Servers often send raw UTF-8 bytes in filename=; .NET reads them as Latin-1.</summary>
    static string DecodeLatin1Utf8(string s)
    {
        if (s.All(c => c < 0x80) || s.Any(c => c > 0xFF)) return s;
        try
        {
            var bytes = Encoding.Latin1.GetBytes(s);
            var utf8 = new UTF8Encoding(false, true).GetString(bytes);
            return utf8;
        }
        catch (DecoderFallbackException)
        {
            return s;
        }
    }

    public static string? FromUrl(Uri uri)
    {
        var last = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        if (string.IsNullOrWhiteSpace(last)) return null;
        return Uri.UnescapeDataString(last);
    }

    public static string? ExtensionForMime(string? mime)
    {
        if (string.IsNullOrWhiteSpace(mime)) return null;
        var bare = mime.Split(';')[0].Trim();
        return MimeExtensions.TryGetValue(bare, out var ext) ? ext : null;
    }

    /// <summary>Picks the best name from header, URL and content type, then sanitizes it.</summary>
    public static string Choose(string? suggested, ContentDispositionHeaderValue? cd, Uri url, string? mime)
    {
        var name = !string.IsNullOrWhiteSpace(suggested) ? suggested
            : FromContentDisposition(cd) ?? FromUrl(url) ?? url.Host;
        name = Sanitize(name);
        if (string.IsNullOrEmpty(Path.GetExtension(name)) && ExtensionForMime(mime) is { } ext)
            name += ext;
        return name;
    }

    /// <summary>
    /// Returns a name that is not taken on disk (neither the file nor its .vdmpart) and not in
    /// <paramref name="taken"/>: "a.zip", then "a (1).zip", "a (2).zip"…
    /// </summary>
    public static string MakeUnique(string folder, string name, ISet<string>? taken = null)
    {
        bool Free(string n) =>
            !File.Exists(Path.Combine(folder, n)) &&
            !File.Exists(Path.Combine(folder, n + ".vdmpart")) &&
            (taken is null || !taken.Contains(n));

        if (Free(name)) return name;
        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        for (var i = 1; ; i++)
        {
            var candidate = $"{stem} ({i}){ext}";
            if (Free(candidate)) return candidate;
        }
    }

    /// <summary>The sub-folder of the download folder each type goes to; other files stay at the top.</summary>
    public static readonly IReadOnlyDictionary<string, string> CategoryFolders = new Dictionary<string, string>
    {
        ["archive"] = "Compressed",
        ["program"] = "Programs",
        ["document"] = "Documents",
        ["video"] = "Video",
        ["audio"] = "Music",
        ["image"] = "Picture",
    };

    /// <summary>Names VDM 1.1.0 used for the same folders; they are renamed on start.</summary>
    public static readonly IReadOnlyDictionary<string, string> RenamedCategoryFolders = new Dictionary<string, string>
    {
        ["Compresses"] = "Compressed",
        ["Musics"] = "Music",
        ["Videos"] = "Video",
        ["Pictures"] = "Picture",
    };

    /// <summary>The bucket a file belongs to, used by the list's type filter.</summary>
    public static string Category(string fileName)
    {
        var ext = Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();
        if (ext.Length == 3 && ext[0] == 'r' && char.IsDigit(ext[1]) && char.IsDigit(ext[2])) return "archive"; // .r00 parts
        return ext switch
        {
            "mp4" or "mkv" or "webm" or "avi" or "mov" or "wmv" or "flv" or "m4v" or "3gp" or "ts" or "mpg" or "mpeg"
                or "mpe" or "asf" or "qt" or "rm" or "rmvb" or "ogv" => "video",
            "mp3" or "m4a" or "aac" or "flac" or "wav" or "ogg" or "opus" or "wma" or "aif" or "aiff" or "mpa" or "ra" => "audio",
            "zip" or "rar" or "7z" or "tar" or "gz" or "gzip" or "bz2" or "xz" or "iso" or "img" or "ace" or "arj" or "lzh"
                or "sea" or "sit" or "sitx" or "z" => "archive",
            "exe" or "msi" or "msu" or "msix" or "apk" or "appx" or "dmg" => "program",
            "pdf" or "doc" or "docx" or "xls" or "xlsx" or "ppt" or "pptx" or "pps" or "ppsx" or "txt" or "epub" or "csv" => "document",
            "jpg" or "jpeg" or "png" or "gif" or "webp" or "bmp" or "svg" or "tif" or "tiff" => "image",
            _ => "other",
        };
    }
}
