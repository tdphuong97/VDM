using System.Security.Cryptography;
using System.Text;

namespace VDM.Core.Tests;

public sealed class UpdaterTests : IDisposable
{
    readonly TestServer _server = new(300_000);
    readonly HttpClient _http = HttpDownloader.CreateClient();
    readonly string _folder = Directory.CreateTempSubdirectory("vdm-update-").FullName;

    public void Dispose()
    {
        _server.Dispose();
        _http.Dispose();
        try { Directory.Delete(_folder, true); } catch { }
    }

    string Feed(string version, string? sha = null, long? size = null)
    {
        var json = $$"""
            {
              "version": "{{version}}",
              "notes": "- Nhớ thư mục đã chọn\n- Tải video Facebook trong bảng tin",
              "page": "https://github.com/someone/VDM/releases/tag/v{{version}}",
              "windows": { "url": "{{_server.Url}}", "size": {{size ?? _server.Data.Length}}, "sha256": "{{sha ?? Convert.ToHexString(SHA256.HashData(_server.Data))}}" }
            }
            """;
        _server.Files["/feed/latest.json"] = Encoding.UTF8.GetBytes(json);
        return _server.BaseUrl + "/feed/latest.json";
    }

    [Fact]
    public async Task A_newer_version_is_offered_with_its_installer()
    {
        var info = await new Updater(_http, Feed("1.3.0")).CheckAsync("1.2.5", "windows", default);

        Assert.NotNull(info);
        Assert.Equal("1.3.0", info.Version);
        Assert.Equal(_server.Url, info.Package!.Url);
        Assert.Contains("Facebook", info.Notes);
    }

    [Theory]
    [InlineData("1.3.0", "1.3.0")]
    [InlineData("1.2.9", "1.3.0")]
    public async Task The_same_or_an_older_version_is_not_offered(string published, string running)
    {
        Assert.Null(await new Updater(_http, Feed(published)).CheckAsync(running, "windows", default));
    }

    [Theory]
    [InlineData("1.10.0", "1.9.3", true)]
    [InlineData("1.2.10", "1.2.9", true)]
    [InlineData("2.0", "1.99.99", true)]
    [InlineData("1.2.5", "1.2.5", false)]
    [InlineData("bad", "1.2.5", false)]
    public void Versions_compare_by_number_not_by_text(string candidate, string current, bool newer)
    {
        Assert.Equal(newer, Updater.IsNewer(candidate, current));
    }

    [Fact]
    public async Task A_system_without_an_installer_still_hears_about_the_new_version()
    {
        var info = await new Updater(_http, Feed("1.3.0")).CheckAsync("1.2.5", "mac", default);
        Assert.NotNull(info);
        Assert.Null(info.Package);
        Assert.Equal("https://github.com/someone/VDM/releases/tag/v1.3.0", info.Page);
    }

    [Fact]
    public async Task A_missing_or_broken_feed_says_so_in_words()
    {
        var missing = new Updater(_http, _server.BaseUrl + "/feed/none.json");
        _server.StatusCode = 404;
        await Assert.ThrowsAsync<UpdateException>(() => missing.CheckAsync("1.2.5", "windows", default));

        _server.StatusCode = 200;
        _server.Files["/feed/latest.json"] = Encoding.UTF8.GetBytes("<html>not json</html>");
        await Assert.ThrowsAsync<UpdateException>(() => new Updater(_http, _server.BaseUrl + "/feed/latest.json").CheckAsync("1.2.5", "windows", default));
    }

    [Fact]
    public async Task The_installer_is_downloaded_and_checked()
    {
        var updater = new Updater(_http, Feed("1.3.0"));
        var info = await updater.CheckAsync("1.2.5", "windows", default);
        var seen = new List<double>();

        var path = await updater.DownloadAsync(info!, _folder, seen.Add, default);

        Assert.Equal("VDM-Setup-1.3.0.exe", Path.GetFileName(path));
        Assert.Equal(_server.Data, await File.ReadAllBytesAsync(path));
        Assert.Equal(1.0, seen[^1]);
    }

    [Fact]
    public async Task An_installer_that_does_not_match_its_fingerprint_is_thrown_away()
    {
        var updater = new Updater(_http, Feed("1.3.0", sha: new string('0', 64)));
        var info = await updater.CheckAsync("1.2.5", "windows", default);

        await Assert.ThrowsAsync<UpdateException>(() => updater.DownloadAsync(info!, _folder, null, default));
        Assert.Empty(Directory.GetFiles(_folder));
    }
}
