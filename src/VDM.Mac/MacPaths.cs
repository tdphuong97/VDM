namespace VDM.Mac;

/// <summary>
/// Where things live. Inside VDM.app: the program and its tools in Contents/MacOS, the pages and the
/// extension in Contents/Resources. Run from a build folder, everything sits next to the program.
/// </summary>
static class MacPaths
{
    static readonly string Exe = AppContext.BaseDirectory;
    static readonly string Resources = Path.GetFullPath(Path.Combine(Exe, "..", "Resources"));

    public static string Ui { get; } = Path.GetFullPath(Directory.Exists(Path.Combine(Resources, "ui")) ? Path.Combine(Resources, "ui") : Path.Combine(Exe, "ui"));
    public static string Tools { get; } = Path.Combine(Exe, "tools");
    public static string BundledExtension { get; } = Directory.Exists(Path.Combine(Resources, "extension")) ? Path.Combine(Resources, "extension") : Path.Combine(Exe, "extension");

    /// <summary>Set VDM_DATA to run a second, separate VDM (for testing) beside the one in use.</summary>
    public static string? DataOverride { get; } = Environment.GetEnvironmentVariable("VDM_DATA") is { Length: > 0 } d ? d : null;
    /// <summary>~/Library/Application Support/VDM on macOS.</summary>
    public static string Data { get; } = DataOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VDM");

    /// <summary>
    /// Chrome loads an unpacked extension from a folder it can reach in its file picker; the inside of
    /// an app bundle is awkward to reach, so the extension is copied next to VDM's data.
    /// </summary>
    public static string Extension => Path.Combine(Data, "extension");
}
