using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using VDM.Core;

namespace VDM.App;

/// <summary>
/// Hosts one HTML page from the ui folder and carries messages both ways:
/// the page sends {id, cmd, args} and gets {id, result|error}; the app pushes {event, data}.
/// </summary>
sealed class WebHost
{
    public const string HostName = "app.vdm";
    static Task<CoreWebView2Environment>? _environment;

    readonly WebView2 _web;
    readonly Func<string, JsonElement, Task<object?>> _handler;

    public bool Ready { get; private set; }

    public WebHost(WebView2 web, Func<string, JsonElement, Task<object?>> handler)
    {
        _web = web;
        _handler = handler;
    }

    static Task<CoreWebView2Environment> Environment()
    {
        // One browser process for every window; its profile lives with the app's data.
        return _environment ??= CoreWebView2Environment.CreateAsync(null,
            Path.Combine(AppPaths.Data, "WebView2"),
            new CoreWebView2EnvironmentOptions("--disable-features=msSmartScreenProtection"));
    }

    public async Task LoadAsync(string page)
    {
        _web.DefaultBackgroundColor = System.Drawing.Color.FromArgb(0xF1, 0xF6, 0xF6);
        await _web.EnsureCoreWebView2Async(await Environment());
        var core = _web.CoreWebView2;
        var s = core.Settings;
        s.AreDefaultContextMenusEnabled = false;
        s.IsStatusBarEnabled = false;
        s.IsZoomControlEnabled = false;
        s.AreBrowserAcceleratorKeysEnabled = false;
        s.IsPasswordAutosaveEnabled = false;
        s.IsGeneralAutofillEnabled = false;
#if !DEBUG
        s.AreDevToolsEnabled = false;
#endif
        core.SetVirtualHostNameToFolderMapping(HostName, AppPaths.Ui, CoreWebView2HostResourceAccessKind.Allow);
        core.WebMessageReceived += OnMessage;
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            Shell.OpenUrl(e.Uri);
        };
        core.NavigationStarting += (_, e) =>
        {
            // The page itself never navigates away; links to the web open in the user's browser.
            if (!e.Uri.StartsWith($"https://{HostName}/", StringComparison.OrdinalIgnoreCase))
            {
                e.Cancel = true;
                Shell.OpenUrl(e.Uri);
            }
        };
        core.NavigationCompleted += (_, _) => Ready = true;
        core.Navigate($"https://{HostName}/{page}");
    }

    async void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        JsonNode? reply = null;
        int id = 0;
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            id = root.GetProperty("id").GetInt32();
            var cmd = root.GetProperty("cmd").GetString() ?? "";
            var args = root.TryGetProperty("args", out var a) ? a.Clone() : default;
            var result = await _handler(cmd, args);
            reply = new JsonObject { ["id"] = id, ["result"] = JsonSerializer.SerializeToNode(result, JsonStore.Options) };
        }
        catch (Exception ex)
        {
            reply = new JsonObject { ["id"] = id, ["error"] = ex is DownloadFailedException ? ex.Message : ex.Message };
        }
        Post(reply);
    }

    public void Emit(string evt, object? data)
    {
        Post(new JsonObject { ["event"] = evt, ["data"] = JsonSerializer.SerializeToNode(data, JsonStore.Options) });
    }

    void Post(JsonNode node)
    {
        if (_web.CoreWebView2 is null) return;
        if (!_web.Dispatcher.CheckAccess())
        {
            _web.Dispatcher.BeginInvoke(() => Post(node));
            return;
        }
        try { _web.CoreWebView2.PostWebMessageAsJson(node.ToJsonString()); }
        catch (InvalidOperationException) { }
    }
}
