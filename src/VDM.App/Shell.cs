using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace VDM.App;

static class AppPaths
{
    public static string Root => AppContext.BaseDirectory;
    public static string Ui => Path.Combine(Root, "ui");
    public static string Tools => Path.Combine(Root, "tools");
    public static string Extension => Path.Combine(Root, "extension");
    public static string ExtensionChromium => Path.Combine(Extension, "chromium");
    public static string ExtensionFirefox => Path.Combine(Extension, "firefox");
    public static string FirefoxXpi => Path.Combine(Extension, "vdm-firefox.xpi");
    /// <summary>Set VDM_DATA to run a second, separate VDM (for testing) beside the one in use.</summary>
    public static string? DataOverride { get; } = Environment.GetEnvironmentVariable("VDM_DATA") is { Length: > 0 } d ? d : null;
    public static string Data { get; } = DataOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VDM");
}

/// <summary>Small bridges to Windows: opening things, the Run key, title bar colour, browsers.</summary>
static class Shell
{
    public static void OpenUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || (u.Scheme != "http" && u.Scheme != "https")) return;
        Start(new ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true });
    }

    public static void OpenFile(string path)
    {
        if (File.Exists(path)) Start(new ProcessStartInfo(path) { UseShellExecute = true });
        else Reveal(path);
    }

    /// <summary>Opens Explorer with the file selected, or the folder if the file is gone.</summary>
    public static void Reveal(string path)
    {
        if (File.Exists(path))
            Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { "/select,", path } });
        else
        {
            var dir = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
            if (dir is not null && Directory.Exists(dir)) Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { dir } });
        }
    }

    static void Start(ProcessStartInfo psi)
    {
        try { Process.Start(psi)?.Dispose(); } catch (Exception) { }
    }

    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static void SetStartWithWindows(bool on)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true) ?? Registry.CurrentUser.CreateSubKey(RunKey);
        if (on) key.SetValue("VDM", $"\"{Environment.ProcessPath}\" --minimized");
        else key.DeleteValue("VDM", throwOnMissingValue: false);
    }

    public static bool IsStartWithWindows()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue("VDM") is string;
    }

    public static string? ClipboardUrl()
    {
        try
        {
            var text = Clipboard.ContainsText() ? Clipboard.GetText().Trim() : null;
            return text is not null && text.Length < 4096 && Uri.TryCreate(text, UriKind.Absolute, out var u)
                   && (u.Scheme == "http" || u.Scheme == "https") ? text : null;
        }
        catch (COMException)
        {
            return null;
        }
    }

    public static void CopyText(string text)
    {
        for (var i = 0; i < 5; i++)
        {
            try { Clipboard.SetText(text); return; }
            catch (COMException) { Thread.Sleep(30); } // another app holds the clipboard
        }
    }

    // ---------- title bar: tint it to the frame so the window reads as one surface (Windows 11) ----------
    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public static void TintTitleBar(Window window, byte r, byte g, byte b)
    {
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        var caption = r | (g << 8) | (b << 16);
        var text = 0x13 | (0x14 << 8) | (0x16 << 16);
        DwmSetWindowAttribute(hwnd, 35, ref caption, 4); // DWMWA_CAPTION_COLOR
        DwmSetWindowAttribute(hwnd, 34, ref caption, 4); // DWMWA_BORDER_COLOR
        DwmSetWindowAttribute(hwnd, 36, ref text, 4);    // DWMWA_TEXT_COLOR
    }

    // ---------- browsers ----------
    public static string? BrowserExe(string name)
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var (appPath, candidates) = name switch
        {
            "Chrome" => ("chrome.exe", new[] { $@"{pf}\Google\Chrome\Application\chrome.exe", $@"{pf86}\Google\Chrome\Application\chrome.exe", $@"{local}\Google\Chrome\Application\chrome.exe" }),
            "Edge" => ("msedge.exe", new[] { $@"{pf86}\Microsoft\Edge\Application\msedge.exe", $@"{pf}\Microsoft\Edge\Application\msedge.exe" }),
            "Cốc Cốc" => ("browser.exe", new[] { $@"{local}\CocCoc\Browser\Application\browser.exe", $@"{pf}\CocCoc\Browser\Application\browser.exe", $@"{pf86}\CocCoc\Browser\Application\browser.exe" }),
            "Firefox" => ("firefox.exe", new[] { $@"{pf}\Mozilla Firefox\firefox.exe", $@"{pf86}\Mozilla Firefox\firefox.exe" }),
            _ => ("", Array.Empty<string>()),
        };
        foreach (var c in candidates)
            if (File.Exists(c)) return c;
        if (appPath.Length > 0 && name != "Cốc Cốc")
        {
            foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
            {
                using var key = hive.OpenSubKey($@"Software\Microsoft\Windows\CurrentVersion\App Paths\{appPath}");
                if (key?.GetValue(null) is string p && File.Exists(p)) return p;
            }
        }
        return null;
    }

    /// <summary>Opens the browser's extension page; false when the browser is not installed.</summary>
    public static bool OpenInBrowser(string browser, string target)
    {
        var exe = BrowserExe(browser);
        if (exe is null) return false;
        Start(new ProcessStartInfo(exe) { ArgumentList = { target } });
        return true;
    }
}
