namespace VDM.Core.Host;

/// <summary>What the capture dialog is about: a file to confirm, or a video page to choose a format for.</summary>
public sealed class CaptureRequest
{
    public bool IsMedia { get; init; }
    public FileRequest? File { get; init; }
    public MediaRequest? Media { get; init; }
    public bool AudioOnly { get; init; }
}

/// <summary>What VDM needs from the operating system. Windows and macOS each provide one.</summary>
public interface IPlatform
{
    /// <summary>"windows" or "mac", for the few words the interface says differently.</summary>
    string Name { get; }
    string ExtensionChromium { get; }
    string ExtensionFirefox { get; }
    string FirefoxXpi { get; }

    /// <summary>Runs the action on the UI thread.</summary>
    void Post(Action action);

    void OpenFile(string path);
    /// <summary>Shows the file selected in Explorer / Finder, or the folder when the file is gone.</summary>
    void Reveal(string path);
    void OpenUrl(string url);
    string? ClipboardUrl();
    void CopyText(string text);
    /// <param name="owner">The window asking: null for the main window, or what the platform passed to the capture session.</param>
    Task<string?> ChooseFolderAsync(object? owner, string current);
    /// <summary>Opens a page or file in the named browser; false when it is not installed.</summary>
    bool OpenInBrowser(string browser, string target);
    bool StartsWithSystem();
    void SetStartWithSystem(bool on);
    void Notify(string title, string text, DownloadItem? item);
    void Warn(string message);

    void OpenCapture(CaptureRequest request);
    void ShowMain(string? view);
    /// <summary>Stops every download and closes VDM.</summary>
    void Quit();

    /// <summary>Whether VDM can run a downloaded installer over itself (not on macOS, built from source).</summary>
    bool CanInstallUpdates { get; }
    /// <summary>Starts the installer to replace VDM, which then closes; the installer opens it again.</summary>
    void InstallUpdate(string installer);
}

/// <summary>Reads the arguments the page sends with a command.</summary>
public static class Args
{
    public static string Str(this System.Text.Json.JsonElement e, string name) =>
        e.ValueKind == System.Text.Json.JsonValueKind.Object && e.TryGetProperty(name, out var v)
        && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() ?? "" : "";

    public static bool Bool(this System.Text.Json.JsonElement e, string name) =>
        e.ValueKind == System.Text.Json.JsonValueKind.Object && e.TryGetProperty(name, out var v)
        && v.ValueKind == System.Text.Json.JsonValueKind.True;

    public static int? Int(this System.Text.Json.JsonElement e, string name) =>
        e.ValueKind == System.Text.Json.JsonValueKind.Object && e.TryGetProperty(name, out var v)
        && v.ValueKind == System.Text.Json.JsonValueKind.Number ? v.GetInt32() : null;

    public static long? Long(this System.Text.Json.JsonElement e, string name) =>
        e.ValueKind == System.Text.Json.JsonValueKind.Object && e.TryGetProperty(name, out var v)
        && v.ValueKind == System.Text.Json.JsonValueKind.Number ? v.GetInt64() : null;
}
