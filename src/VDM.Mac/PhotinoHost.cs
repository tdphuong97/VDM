using System.Text.Json;
using System.Text.Json.Nodes;
using Photino.NET;
using VDM.Core;

namespace VDM.Mac;

/// <summary>
/// Serves the pages to a Photino window from app://0.0.0.0/ and carries messages both ways: the page
/// sends {id, cmd, args} and gets {id, result|error}; the app pushes {event, data}.
/// </summary>
sealed class PhotinoHost
{
    // WebKit (macOS) serves a custom scheme to the top-level page; WebView2 (Windows, used for quick
    // checks) only intercepts http, so there the pages come from http://0.0.0.0/, as Photino.Blazor does.
    static readonly string Scheme = OperatingSystem.IsWindows() ? "http" : "app";
    static readonly Dictionary<string, string> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8",
        [".css"] = "text/css; charset=utf-8",
        [".js"] = "text/javascript; charset=utf-8",
        [".json"] = "application/json",
        [".woff2"] = "font/woff2",
        [".png"] = "image/png",
        [".svg"] = "image/svg+xml",
    };

    readonly PhotinoWindow _window;
    readonly Func<string, JsonElement, Task<object?>> _handler;

    /// <summary>The page has asked for its first data, so it is listening.</summary>
    public bool Ready { get; private set; }

    public PhotinoHost(PhotinoWindow window, Func<string, JsonElement, Task<object?>> handler)
    {
        _window = window;
        _handler = handler;
        window.RegisterCustomSchemeHandler(Scheme, ServeFile);
        window.RegisterWebMessageReceivedHandler(OnMessage);
    }

    public static string Url(string page) => $"{Scheme}://0.0.0.0/{page}";

    static Stream ServeFile(object sender, string scheme, string url, out string contentType)
    {
        var path = Uri.TryCreate(url, UriKind.Absolute, out var u) ? Uri.UnescapeDataString(u.AbsolutePath).TrimStart('/') : "";
        var full = Path.GetFullPath(Path.Combine(MacPaths.Ui, path));
        if (Environment.GetEnvironmentVariable("VDM_TRACE") == "1")
            Console.Error.WriteLine($"[scheme] {scheme} {url} -> {full} exists={File.Exists(full)}");
        if (!full.StartsWith(MacPaths.Ui + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(full))
        {
            contentType = "text/plain";
            return new MemoryStream();
        }
        contentType = Types.TryGetValue(Path.GetExtension(full), out var t) ? t : "application/octet-stream";
        return File.OpenRead(full);
    }

    async void OnMessage(object? sender, string message)
    {
        JsonNode reply;
        var id = 0;
        try
        {
            using var doc = JsonDocument.Parse(message);
            var root = doc.RootElement;
            id = root.GetProperty("id").GetInt32();
            var cmd = root.GetProperty("cmd").GetString() ?? "";
            if (cmd is "ready" or "captureInit") Ready = true;
            var args = root.TryGetProperty("args", out var a) ? a.Clone() : default;
            var result = await _handler(cmd, args);
            reply = new JsonObject { ["id"] = id, ["result"] = JsonSerializer.SerializeToNode(result, JsonStore.Options) };
        }
        catch (Exception ex)
        {
            reply = new JsonObject { ["id"] = id, ["error"] = ex.Message };
        }
        Send(reply);
    }

    public void Emit(string evt, object? data) =>
        Send(new JsonObject { ["event"] = evt, ["data"] = JsonSerializer.SerializeToNode(data, JsonStore.Options) });

    void Send(JsonNode node)
    {
        var text = node.ToJsonString();
        try { _window.Invoke(() => _window.SendWebMessage(text)); }
        catch (Exception) { } // the window is closing
    }
}
