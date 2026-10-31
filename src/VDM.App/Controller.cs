using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VDM.Core;
using VDM.Core.Host;
using Forms = System.Windows.Forms;

namespace VDM.App;

/// <summary>VDM on Windows: the shared host, plus the tray icon, the WPF windows and Windows' own dialogs.</summary>
sealed class Controller : IPlatform
{
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    Forms.NotifyIcon? _tray;
    MainWindow? _main;
    bool _trayHinted;
    DownloadItem? _lastNotified;

    public AppHost Host { get; }
    public DownloadManager Manager => Host.Manager;
    public bool Exiting { get; private set; }
    public BitmapFrame WindowIcon { get; } =
        BitmapFrame.Create(Application.GetResourceStream(new Uri("pack://application:,,,/Assets/vdm.ico")).Stream);

    public Controller()
    {
        Host = new AppHost(this, AppPaths.Data, AppPaths.Tools,
            typeof(Controller).Assembly.GetName().Version?.ToString(3) ?? "1.0.0");
    }

    public void Start(bool minimized, bool updated = false)
    {
        Host.Start();
        CreateTray();
        _timer.Tick += (_, _) => Host.Tick();
        _timer.Start();

        _main = new MainWindow(this);
        Host.AttachMain(new MainView(_main));
        if (!minimized) _main.Show();
        if (updated) Notify("Đã cập nhật VDM", $"VDM đã lên bản {Host.Version}. File đang tải dở được tải tiếp.", null);
    }

    sealed class MainView(MainWindow window) : IMainView
    {
        public bool Ready => window.IsVisible && window.Host.Ready;
        public void Emit(string evt, object? data) => window.Host.Emit(evt, data);
    }

    // ---------- IPlatform ----------
    public string Name => "windows";
    public string ExtensionChromium => AppPaths.ExtensionChromium;
    public string ExtensionFirefox => AppPaths.ExtensionFirefox;
    public string FirefoxXpi => AppPaths.FirefoxXpi;

    public void Post(Action action) => Application.Current.Dispatcher.BeginInvoke(action);
    public void OpenFile(string path) => Shell.OpenFile(path);
    public void Reveal(string path) => Shell.Reveal(path);
    public void OpenUrl(string url) => Shell.OpenUrl(url);
    public string? ClipboardUrl() => Shell.ClipboardUrl();
    public void CopyText(string text) => Shell.CopyText(text);
    public bool OpenInBrowser(string browser, string target) => Shell.OpenInBrowser(browser, target);
    public bool StartsWithSystem() => Shell.IsStartWithWindows();
    public void SetStartWithSystem(bool on) => Shell.SetStartWithWindows(on);
    public void Warn(string message) => MessageBox.Show(message, "VDM", MessageBoxButton.OK, MessageBoxImage.Warning);
    public void ShowMain(string? view) => _main?.Reveal(view);
    public void Quit() => Exit();

    public bool CanInstallUpdates => true;

    public void InstallUpdate(string installer)
    {
        // /UPDATE=1: the installer waits for this VDM to close, keeps the earlier choices and opens VDM again.
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(installer, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /UPDATE=1") { UseShellExecute = true });
        Exit();
    }

    public Task<string?> ChooseFolderAsync(object? owner, string current)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Chọn thư mục lưu file",
            InitialDirectory = Directory.Exists(current) ? current : KnownFolders.Downloads,
        };
        var window = owner as Window ?? _main;
        return Task.FromResult(dialog.ShowDialog(window) == true ? dialog.FolderName : null);
    }

    public void OpenCapture(CaptureRequest request)
    {
        var w = new CaptureWindow(this, request);
        w.Show();
        w.Activate();
    }

    public void Notify(string title, string text, DownloadItem? item)
    {
        _lastNotified = item; // a click on a message without a file opens VDM
        ShowBalloon(title, text);
    }

    // ---------- tray ----------
    void CreateTray()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Mở VDM", null, (_, _) => ShowMain(null));
        menu.Items.Add("Tạm dừng tất cả", null, (_, _) => Manager.PauseAll());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Thoát VDM", null, (_, _) => Exit());
        using var stream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/vdm.ico")).Stream;
        _tray = new Forms.NotifyIcon
        {
            Icon = new System.Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize),
            Text = "VDM",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _tray.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) ShowMain(null); };
        _tray.BalloonTipClicked += (_, _) =>
        {
            if (_lastNotified is { Status: DownloadStatus.Completed } done) Shell.Reveal(done.FullPath);
            else ShowMain(null);
        };
    }

    public void HintTray()
    {
        if (_trayHinted) return;
        _trayHinted = true;
        ShowBalloon("VDM vẫn đang chạy", "Các file tiếp tục tải và VDM vẫn bắt link từ trình duyệt. Bấm biểu tượng dưới khay để mở lại.");
    }

    void ShowBalloon(string title, string text)
    {
        if (_tray is null) return;
        _tray.BalloonTipTitle = title;
        _tray.BalloonTipText = text.Length > 200 ? text[..200] : text;
        _tray.ShowBalloonTip(4000);
    }

    public void Exit()
    {
        if (Exiting) return;
        Exiting = true;
        _timer.Stop();
        if (_tray is not null) { _tray.Visible = false; _tray.Dispose(); }
        Host.Dispose();
        foreach (var w in Application.Current.Windows.Cast<Window>().ToList()) w.Close();
        Application.Current.Shutdown();
    }
}
