using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;

namespace VDM.Core;

/// <summary>
/// The door the browser extension knocks on: a small HTTP server on localhost.
/// Web pages cannot use it: every request must carry the X-VDM header (which forces a CORS
/// preflight that this server never approves) and must not come from an http(s) origin.
/// </summary>
public sealed class LocalApi : IDisposable
{
    public const int FirstPort = 47913;
    public const int LastPort = 47917;

    readonly HttpListener _listener = new();
    readonly CancellationTokenSource _cts = new();
    readonly Func<object> _config;

    public int Port { get; }
    /// <summary>Browser name ("Chrome", "Edge", "Firefox", "Cốc Cốc") and when it last called in.</summary>
    public ConcurrentDictionary<string, DateTimeOffset> BrowsersSeen { get; } = new();

    public event Action<FileRequest>? FileCaptured;
    public event Action<MediaRequest>? MediaCaptured;
    public event Action? ShowRequested;

    LocalApi(int port, Func<object> config)
    {
        Port = port;
        _config = config;
        _listener.Prefixes.Add($"http://localhost:{port}/");
        _listener.Start();
        _ = Task.Run(LoopAsync);
    }

    /// <summary>Takes the first free port of the range the extension knows to look in.</summary>
    public static LocalApi Start(Func<object> config, int firstPort = FirstPort, int lastPort = LastPort)
    {
        HttpListenerException? last = null;
        for (var port = firstPort; port <= lastPort; port++)
        {
            try { return new LocalApi(port, config); }
            catch (HttpListenerException ex) { last = ex; }
        }
        throw new InvalidOperationException("Không mở được cổng cho extension trình duyệt.", last);
    }

    async Task LoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch { return; }
            _ = Task.Run(() => HandleAsync(ctx));
        }
    }

    public static bool IsAllowed(string? origin, string? marker, string method)
    {
        if (method == "OPTIONS") return false;
        if (marker != "1") return false;
        if (string.IsNullOrEmpty(origin) || origin == "null") return true;
        return origin.StartsWith("chrome-extension://", StringComparison.Ordinal)
               || origin.StartsWith("moz-extension://", StringComparison.Ordinal)
               || origin.StartsWith("extension://", StringComparison.Ordinal);
    }

    async Task HandleAsync(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var res = ctx.Response;
        try
        {
            if (!IsAllowed(req.Headers["Origin"], req.Headers["X-VDM"], req.HttpMethod))
            {
                res.StatusCode = 403;
                return;
            }

            var path = req.Url?.AbsolutePath.TrimEnd('/') ?? "";
            switch (req.HttpMethod, path)
            {
                case ("GET", "/ping"):
                    var browser = req.QueryString["browser"];
                    if (!string.IsNullOrWhiteSpace(browser) && browser.Length < 40)
                        BrowsersSeen[browser] = DateTimeOffset.Now;
                    await WriteJsonAsync(res, _config());
                    return;
                case ("POST", "/download"):
                    var file = await ReadJsonAsync<FileRequest>(req);
                    if (file is null || !IsWebUrl(file.Url)) { res.StatusCode = 400; return; }
                    FileCaptured?.Invoke(file);
                    await WriteJsonAsync(res, new { ok = true });
                    return;
                case ("POST", "/media"):
                    var media = await ReadJsonAsync<MediaRequest>(req);
                    if (media is null || !IsWebUrl(media.Url)) { res.StatusCode = 400; return; }
                    MediaCaptured?.Invoke(media);
                    await WriteJsonAsync(res, new { ok = true });
                    return;
                case ("POST", "/show"):
                    ShowRequested?.Invoke();
                    await WriteJsonAsync(res, new { ok = true });
                    return;
                default:
                    res.StatusCode = 404;
                    return;
            }
        }
        catch (Exception)
        {
            res.StatusCode = 500;
        }
        finally
        {
            try { res.Close(); } catch { }
        }
    }

    static bool IsWebUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);

    static async Task<T?> ReadJsonAsync<T>(HttpListenerRequest req)
    {
        if (req.ContentLength64 > 4 * 1024 * 1024) return default;
        using var reader = new StreamReader(req.InputStream, Encoding.UTF8);
        var body = await reader.ReadToEndAsync();
        try { return JsonSerializer.Deserialize<T>(body, JsonStore.Options); }
        catch (JsonException) { return default; }
    }

    static async Task WriteJsonAsync(HttpListenerResponse res, object value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonStore.Options);
        res.ContentType = "application/json; charset=utf-8";
        res.ContentLength64 = bytes.Length;
        await res.OutputStream.WriteAsync(bytes);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); _listener.Close(); } catch { }
    }
}
