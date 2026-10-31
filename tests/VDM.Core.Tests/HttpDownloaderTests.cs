namespace VDM.Core.Tests;

public sealed class HttpDownloaderTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("vdm-test-").FullName;
    static readonly HttpClient Http = HttpDownloader.CreateClient();

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    DownloadItem NewItem(TestServer server, int connections = 8) => new()
    {
        Url = server.Url,
        Folder = _dir,
        Connections = connections,
    };

    static HttpDownloader Downloader(DownloadItem item) => new(Http, item)
    {
        ReadTimeout = TimeSpan.FromSeconds(5),
        HeaderTimeout = TimeSpan.FromSeconds(1),
        Backoff = _ => TimeSpan.FromMilliseconds(50),
    };

    [Fact]
    public async Task Splits_the_file_across_connections_and_reassembles_it_exactly()
    {
        using var server = new TestServer(8 * 1024 * 1024) { ChunkDelayMs = 2 };
        var item = NewItem(server);

        await Downloader(item).RunAsync(CancellationToken.None);

        Assert.Equal(server.Data, await File.ReadAllBytesAsync(item.FullPath));
        Assert.Equal("test.bin", item.FileName);
        Assert.True(item.ResumeSupported);
        Assert.True(item.Segments.Count >= 4, $"expected several segments, got {item.Segments.Count}");
        Assert.True(server.RangesRequested.Count(r => r != "bytes=0-") >= 3);
        Assert.False(File.Exists(item.PartPath));
    }

    [Fact]
    public async Task Opens_up_to_32_connections_on_a_server_that_allows_them()
    {
        using var server = new TestServer(48 * 1024 * 1024) { ChunkDelayMs = 2 };
        var item = NewItem(server, connections: 32);

        await Downloader(item).RunAsync(CancellationToken.None);

        Assert.Equal(server.Data, await File.ReadAllBytesAsync(item.FullPath));
        Assert.True(server.RangesRequested.Count >= 32, $"only {server.RangesRequested.Count} requests");
    }

    [Fact]
    public async Task Falls_back_to_one_connection_when_the_server_has_no_ranges()
    {
        using var server = new TestServer(3 * 1024 * 1024) { SupportsRanges = false };
        var item = NewItem(server);

        await Downloader(item).RunAsync(CancellationToken.None);

        Assert.Equal(server.Data, await File.ReadAllBytesAsync(item.FullPath));
        Assert.False(item.ResumeSupported);
        Assert.Single(item.Segments);
        Assert.Single(server.RangesRequested);
    }

    [Fact]
    public async Task Downloads_a_stream_of_unknown_length()
    {
        using var server = new TestServer(1_234_567) { SupportsRanges = false, SendLength = false };
        var item = NewItem(server);

        await Downloader(item).RunAsync(CancellationToken.None);

        Assert.Equal(server.Data, await File.ReadAllBytesAsync(item.FullPath));
        Assert.Equal(1_234_567, item.TotalBytes);
    }

    [Fact]
    public async Task Retries_a_dropped_connection_from_where_it_stopped()
    {
        using var server = new TestServer(4 * 1024 * 1024) { DropAfterBytes = 300_000, Drops = 3 };
        var item = NewItem(server, connections: 2);

        await Downloader(item).RunAsync(CancellationToken.None);

        Assert.Equal(server.Data, await File.ReadAllBytesAsync(item.FullPath));
    }

    [Fact]
    public async Task Restarts_from_zero_after_a_drop_when_the_server_cannot_resume()
    {
        using var server = new TestServer(2 * 1024 * 1024) { SupportsRanges = false, DropAfterBytes = 500_000, Drops = 1 };
        var item = NewItem(server);

        await Downloader(item).RunAsync(CancellationToken.None);

        Assert.Equal(server.Data, await File.ReadAllBytesAsync(item.FullPath));
    }

    [Fact]
    public async Task Pauses_and_resumes_without_refetching_finished_bytes()
    {
        using var server = new TestServer(6 * 1024 * 1024) { ChunkDelayMs = 5 };
        var item = NewItem(server, connections: 4);

        using (var cts = new CancellationTokenSource())
        {
            var run = Downloader(item).RunAsync(cts.Token);
            while (item.Segments.Sum(s => s.Downloaded) < 1024 * 1024) await Task.Delay(10);
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        }

        var donePaused = item.Segments.Sum(s => s.Downloaded);
        Assert.True(File.Exists(item.PartPath));
        Assert.All(item.Segments, s => Assert.False(s.Active));
        server.RangesRequested.Clear();

        await Downloader(item).RunAsync(CancellationToken.None);

        Assert.Equal(server.Data, await File.ReadAllBytesAsync(item.FullPath));
        Assert.DoesNotContain("bytes=0-", server.RangesRequested);
        Assert.True(donePaused > 0);
    }

    [Fact]
    public async Task Finishes_on_the_connections_a_server_allows_when_it_ignores_the_rest()
    {
        using var server = new TestServer(4 * 1024 * 1024) { MaxConcurrent = 2, ChunkDelayMs = 1 };
        var item = NewItem(server, connections: 8);

        var run = Downloader(item).RunAsync(CancellationToken.None);
        var finished = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(30)));

        Assert.Same(run, finished);
        await run;
        Assert.Equal(server.Data, await File.ReadAllBytesAsync(item.FullPath));
    }

    [Fact]
    public async Task All_connections_of_a_download_keep_together_to_its_speed_limit()
    {
        using var server = new TestServer(1024 * 1024);
        var item = NewItem(server, connections: 8);
        var downloader = new HttpDownloader(Http, item) { ReadTimeout = TimeSpan.FromSeconds(5), Limiter = new Throttle { Rate = 512 * 1024 } };

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await downloader.RunAsync(CancellationToken.None);

        Assert.InRange(sw.Elapsed.TotalSeconds, 1.6, 4.0); // 1 MB at 512 KB/s, whatever the connection count
        Assert.Equal(server.Data, await File.ReadAllBytesAsync(item.FullPath));
    }

    [Fact]
    public async Task Reports_an_expired_link_in_words()
    {
        using var server = new TestServer(1024) { StatusCode = 403 };
        var item = NewItem(server);

        var ex = await Assert.ThrowsAsync<DownloadFailedException>(() => Downloader(item).RunAsync(CancellationToken.None));

        Assert.Contains("403", ex.Message);
    }

    [Fact]
    public async Task Takes_the_name_from_content_disposition_including_vietnamese()
    {
        using var server = new TestServer(10_000) { ContentDisposition = "attachment; filename*=UTF-8''T%E1%BA%A3i%20v%E1%BB%81.zip" };
        var item = NewItem(server);

        await Downloader(item).RunAsync(CancellationToken.None);

        Assert.Equal("Tải về.zip", item.FileName);
        Assert.True(File.Exists(Path.Combine(_dir, "Tải về.zip")));
    }

    [Fact]
    public async Task Does_not_overwrite_a_file_that_already_exists()
    {
        using var server = new TestServer(10_000);
        File.WriteAllText(Path.Combine(_dir, "test.bin"), "mine");
        var item = NewItem(server);

        await Downloader(item).RunAsync(CancellationToken.None);

        Assert.Equal("test (1).bin", item.FileName);
        Assert.Equal("mine", File.ReadAllText(Path.Combine(_dir, "test.bin")));
    }
}
