using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;

namespace VDM.Core.Tests;

public sealed class LocalApiTests : IDisposable
{
    readonly LocalApi _api;
    readonly HttpClient _http = new();
    readonly string _base;

    public LocalApiTests()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        _api = LocalApi.Start(() => new { app = "VDM" }, port, port);
        _base = $"http://localhost:{_api.Port}";
    }

    public void Dispose()
    {
        _api.Dispose();
        _http.Dispose();
    }

    HttpRequestMessage Request(HttpMethod method, string path, string? origin, bool marker = true)
    {
        var req = new HttpRequestMessage(method, _base + path);
        if (marker) req.Headers.Add("X-VDM", "1");
        if (origin is not null) req.Headers.Add("Origin", origin);
        return req;
    }

    [Fact]
    public async Task Extension_can_ping_and_is_remembered()
    {
        var res = await _http.SendAsync(Request(HttpMethod.Get, "/ping?browser=Edge", "chrome-extension://abcdef"));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("VDM", await res.Content.ReadAsStringAsync());
        Assert.True(_api.BrowsersSeen.ContainsKey("Edge"));
    }

    [Theory]
    [InlineData("https://evil.example", true)]
    [InlineData("chrome-extension://abcdef", false)]
    public async Task Web_pages_and_requests_without_the_marker_are_refused(string origin, bool marker)
    {
        var res = await _http.SendAsync(Request(HttpMethod.Get, "/ping", origin, marker));
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task A_captured_download_reaches_the_app()
    {
        FileRequest? got = null;
        _api.FileCaptured += r => got = r;
        var req = Request(HttpMethod.Post, "/download", "moz-extension://1234");
        req.Content = JsonContent.Create(new { url = "https://example.org/a.zip", fileSize = 42, referrer = "https://example.org/" });

        var res = await _http.SendAsync(req);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("https://example.org/a.zip", got?.Url);
        Assert.Equal(42, got?.FileSize);
    }

    [Fact]
    public async Task Non_web_links_are_rejected()
    {
        var req = Request(HttpMethod.Post, "/download", "chrome-extension://abcdef");
        req.Content = JsonContent.Create(new { url = "file:///C:/Windows/win.ini" });
        var res = await _http.SendAsync(req);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }
}
