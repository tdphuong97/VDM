namespace VDM.Core.Tests;

public sealed class DownloadManagerTests : IDisposable
{
    readonly string _data = Directory.CreateTempSubdirectory("vdm-data-").FullName;
    readonly string _downloads = Directory.CreateTempSubdirectory("vdm-dl-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_data, true); } catch { }
        try { Directory.Delete(_downloads, true); } catch { }
    }

    DownloadManager NewManager(int parallel = 3)
    {
        var m = new DownloadManager(_data, new ToolPaths(_data));
        m.Settings.DownloadFolder = _downloads;
        m.Settings.MaxParallel = parallel;
        return m;
    }

    static async Task WaitFor(Func<bool> condition, DownloadManager m, int seconds = 20)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException();
            m.Tick();
            await Task.Delay(50);
        }
    }

    [Fact]
    public async Task Runs_no_more_than_the_parallel_limit_and_finishes_the_queue()
    {
        using var server = new TestServer(512 * 1024) { ChunkDelayMs = 3 };
        using var m = NewManager(parallel: 2);
        var items = Enumerable.Range(0, 4)
            .Select(i => m.AddFile(new FileRequest { Url = server.Url }, $"f{i}.bin", null, null, start: true))
            .ToList();

        Assert.True(m.Items.Count(i => i.Status == DownloadStatus.Downloading) <= 2);
        await WaitFor(() => items.All(i => i.Status == DownloadStatus.Completed), m);

        foreach (var i in items)
            Assert.Equal(server.Data, await File.ReadAllBytesAsync(i.FullPath));
    }

    [Fact]
    public async Task Remembers_the_list_and_carries_on_after_a_restart()
    {
        using var server = new TestServer(6 * 1024 * 1024) { ChunkDelayMs = 5 };
        string id;
        using (var m = NewManager())
        {
            var item = m.AddFile(new FileRequest { Url = server.Url }, "big.bin", null, null, start: true);
            id = item.Id;
            await WaitFor(() => item.DownloadedBytes > 512 * 1024, m);
        } // closing the app mid-download

        using var again = NewManager();
        var restored = again.Find(id)!;
        Assert.Equal(DownloadStatus.Queued, restored.Status);
        again.Start(id);
        again.Tick();
        await WaitFor(() => restored.Status == DownloadStatus.Completed, again);
        Assert.Equal(server.Data, await File.ReadAllBytesAsync(restored.FullPath));
    }

    [Fact]
    public void Creates_idms_type_folders_inside_the_download_folder()
    {
        using var m = NewManager();
        m.EnsureCategoryFolders();
        foreach (var sub in new[] { "Compressed", "Programs", "Documents", "Video", "Music", "Picture" })
            Assert.True(Directory.Exists(Path.Combine(_downloads, sub)), sub);
    }

    [Theory]
    [InlineData("backup.zip", "Compressed")]
    [InlineData("Bao-cao.pdf", "Documents")]
    [InlineData("setup.exe", "Programs")]
    [InlineData("phim.mkv", "Video")]
    [InlineData("anh.jpg", "Picture")]
    [InlineData("bai-hat.mp3", "Music")]
    [InlineData("data.bin", "")]
    public void A_file_with_a_known_name_goes_to_its_type_folder(string name, string sub)
    {
        using var m = NewManager();
        var item = m.AddFile(new FileRequest { Url = "https://example.vn/x" }, name, null, null, start: false);
        Assert.Equal(Path.Combine(_downloads, sub).TrimEnd('\\'), item.Folder.TrimEnd('\\'));
    }

    [Fact]
    public void A_folder_the_user_picked_is_kept()
    {
        using var m = NewManager();
        var mine = Path.Combine(_downloads, "Của tôi");
        var item = m.AddFile(new FileRequest { Url = "https://example.vn/x" }, "backup.zip", mine, null, start: false);
        Assert.Equal(mine, item.Folder);
    }

    [Fact]
    public void Sorting_can_be_switched_off()
    {
        using var m = NewManager();
        m.Settings.SortByType = false;
        var item = m.AddFile(new FileRequest { Url = "https://example.vn/x" }, "backup.zip", null, null, start: false);
        Assert.Equal(_downloads, item.Folder);
    }

    [Fact]
    public async Task A_file_named_by_the_server_still_lands_in_its_type_folder()
    {
        using var server = new TestServer(200_000) { ContentDisposition = "attachment; filename=\"Hop-dong.pdf\"" };
        using var m = NewManager();
        var item = m.AddFile(new FileRequest { Url = server.Url }, null, null, null, start: true);

        await WaitFor(() => item.Status == DownloadStatus.Completed, m);

        Assert.Equal(Path.Combine(_downloads, "Documents", "Hop-dong.pdf"), item.FullPath);
        Assert.True(File.Exists(item.FullPath));
    }

    [Fact]
    public void Songs_go_to_musics_and_videos_to_videos()
    {
        using var m = NewManager();
        var song = m.AddMedia(new MediaRequest { Url = "https://www.youtube.com/watch?v=a" }, null, new MediaOptions { AudioOnly = true }, null, start: false);
        var clip = m.AddMedia(new MediaRequest { Url = "https://www.youtube.com/watch?v=b" }, null, new MediaOptions(), null, start: false);
        Assert.Equal(Path.Combine(_downloads, "Music"), song.Folder);
        Assert.Equal(Path.Combine(_downloads, "Video"), clip.Folder);
    }

    [Fact]
    public async Task A_finished_download_keeps_no_login_cookies()
    {
        using var server = new TestServer(100_000);
        using (var m = NewManager())
        {
            var item = m.AddFile(new FileRequest { Url = server.Url, CookieHeader = "c_user=1; xs=secret" }, "a.bin", null, null, start: true);
            await WaitFor(() => item.Status == DownloadStatus.Completed, m);
            Assert.Null(item.CookieHeader);
        }
        Assert.DoesNotContain("secret", await File.ReadAllTextAsync(Path.Combine(_data, "downloads.json")));
    }

    [Fact]
    public void Cookies_left_in_an_old_list_are_dropped_from_finished_downloads_only()
    {
        File.WriteAllText(Path.Combine(_data, "downloads.json"), """
            [
              { "id": "done", "kind": "file", "url": "https://example.vn/a", "fileName": "a.zip", "folder": "D:\\x", "status": "completed", "cookieHeader": "xs=old" },
              { "id": "half", "kind": "media", "url": "https://www.facebook.com/reel/1", "fileName": "b.mp4", "folder": "D:\\x", "status": "paused",
                "media": { "cookies": [ { "name": "xs", "value": "keep", "domain": ".facebook.com", "path": "/" } ] } }
            ]
            """);
        using var m = NewManager();
        Assert.Null(m.Find("done")!.CookieHeader);
        Assert.Single(m.Find("half")!.Media!.Cookies!); // still needed to resume
    }

    [Fact]
    public void A_remembered_folder_takes_the_next_files_of_that_type_only()
    {
        using var m = NewManager();
        var mine = Directory.CreateDirectory(Path.Combine(_downloads, "Bộ cài")).FullName;
        m.RememberFolder("archive", mine);

        var zip = m.AddFile(new FileRequest { Url = "https://example.vn/a" }, "backup.zip", null, null, start: false);
        var pdf = m.AddFile(new FileRequest { Url = "https://example.vn/b" }, "Bao-cao.pdf", null, null, start: false);

        Assert.Equal(mine, zip.Folder);
        Assert.Equal(mine, m.FolderFor("phan-2.rar"));
        Assert.Equal(Path.Combine(_downloads, "Documents"), pdf.Folder);
    }

    [Fact]
    public void Videos_and_songs_remember_their_own_folders()
    {
        using var m = NewManager();
        var films = Directory.CreateDirectory(Path.Combine(_downloads, "Phim")).FullName;
        m.RememberFolder("video", films);

        var clip = m.AddMedia(new MediaRequest { Url = "https://www.youtube.com/watch?v=a" }, null, new MediaOptions(), null, start: false);
        var song = m.AddMedia(new MediaRequest { Url = "https://www.youtube.com/watch?v=b" }, null, new MediaOptions { AudioOnly = true }, null, start: false);

        Assert.Equal(films, clip.Folder);
        Assert.Equal(Path.Combine(_downloads, "Music"), song.Folder);
    }

    [Fact]
    public void A_remembered_folder_is_still_there_after_a_restart()
    {
        var films = Directory.CreateDirectory(Path.Combine(_downloads, "Phim")).FullName;
        using (var m = NewManager()) m.RememberFolder("video", films);

        using var again = new DownloadManager(_data, new ToolPaths(_data));
        Assert.Equal(films, again.MediaFolder(audioOnly: false));
    }

    [Fact]
    public void Forgetting_or_picking_the_type_folder_goes_back_to_the_type_folder()
    {
        using var m = NewManager();
        var films = Directory.CreateDirectory(Path.Combine(_downloads, "Phim")).FullName;
        m.RememberFolder("video", films);

        Assert.Equal(Path.Combine(_downloads, "Video"), m.ForgetFolder("video"));
        Assert.Equal(Path.Combine(_downloads, "Video"), m.MediaFolder(audioOnly: false));

        m.RememberFolder("video", films);
        m.RememberFolder("video", Path.Combine(_downloads, "Video") + "\\");
        Assert.Empty(m.Settings.RememberedFolders);
    }

    [Fact]
    public void A_remembered_folder_that_is_gone_falls_back_to_the_type_folder()
    {
        using var m = NewManager();
        m.RememberFolder("document", Path.Combine(_downloads, "đã xóa"));
        Assert.Equal(Path.Combine(_downloads, "Documents"), m.FolderFor("Hop-dong.pdf"));
    }

    [Fact]
    public void A_remembered_folder_works_with_sorting_switched_off()
    {
        using var m = NewManager();
        m.Settings.SortByType = false;
        var mine = Directory.CreateDirectory(Path.Combine(_downloads, "Nén")).FullName;
        m.RememberFolder("archive", mine);

        Assert.Equal(mine, m.FolderFor("backup.zip"));
        Assert.Equal(_downloads, m.FolderFor("Bao-cao.pdf"));
    }

    [Fact]
    public async Task A_file_named_by_the_server_goes_to_the_folder_remembered_for_its_type()
    {
        using var server = new TestServer(200_000) { ContentDisposition = "attachment; filename=\"Hop-dong.pdf\"" };
        using var m = NewManager();
        m.Settings.SortByType = false;
        var papers = Directory.CreateDirectory(Path.Combine(_downloads, "Giấy tờ")).FullName;
        m.RememberFolder("document", papers);
        var item = m.AddFile(new FileRequest { Url = server.Url }, null, null, null, start: true);

        await WaitFor(() => item.Status == DownloadStatus.Completed, m);

        Assert.Equal(Path.Combine(papers, "Hop-dong.pdf"), item.FullPath);
    }

    [Fact]
    public void Folders_from_version_1_1_0_are_renamed_and_their_files_followed()
    {
        var oldMusic = Directory.CreateDirectory(Path.Combine(_downloads, "Musics")).FullName;
        File.WriteAllText(Path.Combine(oldMusic, "song.mp3"), "la la");
        Directory.CreateDirectory(Path.Combine(_downloads, "Compresses"));
        using var m = NewManager();
        var song = m.AddFile(new FileRequest { Url = "https://example.vn/song.mp3" }, "song.mp3", oldMusic, 5, start: false);

        m.EnsureCategoryFolders();

        Assert.Equal("la la", File.ReadAllText(Path.Combine(_downloads, "Music", "song.mp3")));
        Assert.False(Directory.Exists(oldMusic));
        Assert.False(Directory.Exists(Path.Combine(_downloads, "Compresses")));
        Assert.True(Directory.Exists(Path.Combine(_downloads, "Compressed")));
        Assert.Equal(Path.Combine(_downloads, "Music"), song.Folder);
    }

    [Fact]
    public void An_old_folder_is_left_alone_when_the_new_one_already_has_its_own_files()
    {
        var oldVideo = Directory.CreateDirectory(Path.Combine(_downloads, "Videos")).FullName;
        File.WriteAllText(Path.Combine(oldVideo, "a.mp4"), "x");
        Directory.CreateDirectory(Path.Combine(_downloads, "Video"));
        File.WriteAllText(Path.Combine(_downloads, "Video", "b.mp4"), "y");
        using var m = NewManager();

        m.EnsureCategoryFolders();

        Assert.True(File.Exists(Path.Combine(oldVideo, "a.mp4")));
        Assert.True(File.Exists(Path.Combine(_downloads, "Video", "b.mp4")));
    }

    [Fact]
    public async Task Downloads_waiting_from_last_time_start_again_when_the_app_opens()
    {
        using var server = new TestServer(300_000);
        string id;
        using (var m = NewManager())
        {
            id = m.AddFile(new FileRequest { Url = server.Url }, "later.bin", null, null, start: false).Id;
            m.Start(id);
        }
        using var again = NewManager();
        Assert.Equal(DownloadStatus.Queued, again.Find(id)!.Status);

        again.StartQueued();

        await WaitFor(() => again.Find(id)!.Status == DownloadStatus.Completed, again);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Two_downloads_share_a_line_equally_even_with_eight_connections_against_one(bool share)
    {
        // An 8 MB/s line: TCP alone gives the 8-connection download about 8 parts in 9.
        using var server = new TestServer(96 * 1024 * 1024) { ChunkSize = 8 * 1024, Line = new Throttle { Rate = 8 * 1048576 } };
        using var m = NewManager();
        m.Settings.ShareBandwidth = share;
        var wide = m.AddFile(new FileRequest { Url = server.Url }, "wide.bin", null, null, start: false);
        var narrow = m.AddFile(new FileRequest { Url = server.Url + "?n" }, "narrow.bin", null, null, start: false);
        wide.Connections = 8;
        narrow.Connections = 1;
        m.Start(wide.Id);
        m.Start(narrow.Id);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        long wideAt = 0, narrowAt = 0;
        // Measure from 4 s (both running, the first free run over) to 10 s (neither can have finished).
        while (sw.Elapsed < TimeSpan.FromSeconds(10))
        {
            m.Tick();
            await Task.Delay(500);
            if (sw.Elapsed > TimeSpan.FromSeconds(4) && wideAt == 0) { wideAt = wide.DownloadedBytes; narrowAt = narrow.DownloadedBytes; }
        }
        var wideRate = (wide.DownloadedBytes - wideAt) / 6.0 / 1048576;
        var narrowRate = (narrow.DownloadedBytes - narrowAt) / 6.0 / 1048576;
        m.PauseAll();

        if (share)
            Assert.InRange(wideRate / narrowRate, 0.6, 1.7);
        else
            Assert.True(wideRate / narrowRate > 3, $"without sharing expected a lopsided split, got {wideRate:F1} vs {narrowRate:F1}");
        Assert.True(wideRate + narrowRate > 5.5, $"the line is 8 MB/s; together they got {wideRate + narrowRate:F1}");
    }

    [Fact]
    public async Task Removing_an_unfinished_download_deletes_its_partial_file()
    {
        using var server = new TestServer(4 * 1024 * 1024) { ChunkDelayMs = 10 };
        using var m = NewManager();
        var item = m.AddFile(new FileRequest { Url = server.Url }, "x.bin", null, null, start: true);
        await WaitFor(() => File.Exists(item.PartPath) && item.DownloadedBytes > 0, m);

        await m.RemoveAsync(item.Id, deleteFile: false);

        Assert.False(File.Exists(item.PartPath));
        Assert.Null(m.Find(item.Id));
    }
}
