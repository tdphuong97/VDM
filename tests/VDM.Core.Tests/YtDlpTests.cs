namespace VDM.Core.Tests;

public sealed class YtDlpTests
{
    const string YouTubeInfo = """
    {
      "id": "abc", "title": "Sơn Tùng M-TP | Chúng Ta Của Tương Lai", "duration": 300,
      "thumbnail": "https://i.ytimg.com/vi/abc/maxresdefault.jpg", "uploader": "Sơn Tùng M-TP",
      "extractor_key": "Youtube", "webpage_url": "https://www.youtube.com/watch?v=abc",
      "formats": [
        {"format_id": "sb0", "ext": "mhtml", "vcodec": "none", "acodec": "none", "format_note": "storyboard"},
        {"format_id": "140", "ext": "m4a", "vcodec": "none", "acodec": "mp4a.40.2", "filesize": 4800000},
        {"format_id": "251", "ext": "webm", "vcodec": "none", "acodec": "opus", "filesize": 5200000},
        {"format_id": "18", "ext": "mp4", "vcodec": "avc1.42001E", "acodec": "mp4a.40.2", "width": 640, "height": 360, "filesize": 20000000},
        {"format_id": "137", "ext": "mp4", "vcodec": "avc1.640028", "acodec": "none", "width": 1920, "height": 1080, "filesize": 90000000},
        {"format_id": "248", "ext": "webm", "vcodec": "vp9", "acodec": "none", "width": 1920, "height": 1080, "filesize": 70000000},
        {"format_id": "313", "ext": "webm", "vcodec": "vp9", "acodec": "none", "width": 3840, "height": 2160, "filesize_approx": 400000000},
        {"format_id": "136", "ext": "mp4", "vcodec": "avc1.4d401f", "acodec": "none", "width": 1280, "height": 720, "tbr": 1000}
      ]
    }
    """;

    [Fact]
    public void Lists_qualities_from_best_to_worst_with_sizes_including_audio()
    {
        var info = YtDlp.ParseInfo(YouTubeInfo);

        Assert.Equal("Sơn Tùng M-TP | Chúng Ta Của Tương Lai", info.Title);
        Assert.Equal(new int?[] { 2160, 1080, 720, 360 }, info.Qualities.Select(q => q.Height));
        Assert.Equal("1080p Full HD", info.Qualities[1].Label);
        // 1080p prefers the H.264 stream, plus the m4a audio it will be merged with.
        Assert.Equal(90_000_000 + 4_800_000, info.Qualities[1].ApproxBytes);
        // 720p has only a bitrate: 1000 kbit/s for 300 s.
        Assert.Equal(1000L * 125 * 300 + 4_800_000, info.Qualities[2].ApproxBytes);
        // 360p already carries its audio.
        Assert.Equal(20_000_000, info.Qualities[3].ApproxBytes);
        Assert.Equal(4_800_000, info.AudioApproxBytes);
    }

    [Fact]
    public void The_size_comes_from_the_stream_yt_dlp_downloads_not_a_bigger_hls_guess()
    {
        // wVLhvXB2flE, a 92-minute mix: at 1080p H.264 YouTube offers an HLS stream with no size (its
        // bitrate times the length guesses 3.26 GB) beside the https stream yt-dlp really takes (770 MB).
        var info = YtDlp.ParseInfo("""
        {"title": "mix", "duration": 5511, "formats": [
          {"format_id": "140", "protocol": "https", "ext": "m4a", "vcodec": "none", "acodec": "mp4a.40.2", "filesize": 89186094},
          {"format_id": "270", "protocol": "m3u8_native", "vcodec": "avc1.640028", "acodec": "none", "width": 1920, "height": 1080, "tbr": 4726.631},
          {"format_id": "137", "protocol": "https", "vcodec": "avc1.640028", "acodec": "none", "width": 1920, "height": 1080, "filesize": 770896207},
          {"format_id": "616", "protocol": "m3u8_native", "vcodec": "vp09.00.40.08", "acodec": "none", "width": 1920, "height": 1080, "tbr": 4132.894}
        ]}
        """);

        Assert.Equal(770_896_207 + 89_186_094, info.Qualities.Single(q => q.Height == 1080).ApproxBytes);
    }

    [Fact]
    public void A_separate_video_stream_is_taken_over_an_old_combined_one()
    {
        // Icq1mZFekTg at 360p: the old combined format 18 (video with weak audio) beside the video-only 134.
        // VDM takes the video-only stream plus the good audio, and says so in its size.
        var info = YtDlp.ParseInfo("""
        {"title": "lyric video", "duration": 201, "formats": [
          {"format_id": "140", "protocol": "https", "ext": "m4a", "vcodec": "none", "acodec": "mp4a.40.2", "filesize": 3255938},
          {"format_id": "134", "protocol": "https", "vcodec": "avc1.4D401E", "acodec": "none", "width": 640, "height": 360, "filesize": 2652304},
          {"format_id": "18", "protocol": "https", "vcodec": "avc1.42001E", "acodec": "mp4a.40.2", "width": 640, "height": 360, "filesize": 9078567}
        ]}
        """);

        Assert.Equal(2_652_304 + 3_255_938, info.Qualities.Single(q => q.Height == 360).ApproxBytes);
    }

    [Fact]
    public void Media_downloads_can_go_through_the_speed_sharing_proxy()
    {
        var item = new DownloadItem { Kind = DownloadKind.Media, Url = "https://www.youtube.com/watch?v=abc", Folder = "v", Media = new MediaOptions() };
        var args = YtDlp.DownloadArgs(item, new ToolPaths("t"), null, "p.txt", proxy: "http://127.0.0.1:5555");
        Assert.Equal("http://127.0.0.1:5555", args[args.IndexOf("--proxy") + 1]);
        Assert.DoesNotContain("--proxy", YtDlp.DownloadArgs(item, new ToolPaths("t"), null, "p.txt"));
    }

    [Fact]
    public void Video_downloads_pick_a_video_only_stream_and_fall_back_to_a_combined_one()
    {
        var item = new DownloadItem { Kind = DownloadKind.Media, Url = "https://www.youtube.com/watch?v=abc", Folder = @"C:\Videos", Media = new MediaOptions { Height = 360 } };
        var args = YtDlp.DownloadArgs(item, new ToolPaths(@"C:\VDM	ools"), null, @"C:	\p.txt");
        Assert.Equal("bv+ba/b", args[args.IndexOf("-f") + 1]);
    }

    [Fact]
    public void Vertical_video_is_labelled_by_its_short_side()
    {
        var info = YtDlp.ParseInfo("""
        {"title": "short", "formats": [{"format_id": "1", "vcodec": "avc1", "acodec": "mp4a", "width": 1080, "height": 1920}]}
        """);
        Assert.Equal(1080, info.Qualities.Single().Height);
    }

    [Fact]
    public void A_source_without_resolutions_still_offers_the_original()
    {
        var info = YtDlp.ParseInfo("""{"title": "clip", "ext": "mp4", "url": "https://x/clip.mp4"}""");
        var q = Assert.Single(info.Qualities);
        Assert.Null(q.Height);
        Assert.Equal("Chất lượng gốc", q.Label);
    }

    [Fact]
    public void A_playlist_is_refused_in_words()
    {
        var ex = Assert.Throws<DownloadFailedException>(() => YtDlp.ParseInfo("""{"_type": "playlist", "entries": []}"""));
        Assert.Contains("danh sách phát", ex.Message);
    }

    [Fact]
    public void Parses_the_progress_template()
    {
        var p = YtDlp.ParseProgress("[vdm] downloading|1048576|NA|52428800.0|2097152.5|137");
        Assert.NotNull(p);
        Assert.Equal(1_048_576, p!.Downloaded);
        Assert.Null(p.Total);
        Assert.Equal(52_428_800, p.TotalEstimate);
        Assert.Equal("137", p.FormatId);
        Assert.Null(YtDlp.ParseProgress("[download] Destination: x.mp4"));
    }

    [Fact]
    public void Video_args_sort_by_the_chosen_resolution_and_prefer_h264()
    {
        var item = new DownloadItem
        {
            Kind = DownloadKind.Media, Url = "https://www.youtube.com/watch?v=abc", Folder = @"C:\Videos",
            Referrer = "https://www.youtube.com/", UserAgent = "UA",
            Media = new MediaOptions { Height = 720 },
        };
        var args = YtDlp.DownloadArgs(item, new ToolPaths(@"C:\VDM\tools"), null, @"C:\t\p.txt");

        Assert.Contains("res:720,vcodec:h264,acodec:aac", args);
        Assert.Contains("mp4", args);
        Assert.DoesNotContain("--referer", args); // never for YouTube
        Assert.Equal("https://www.youtube.com/watch?v=abc", args[^1]);
        Assert.Equal("--", args[^2]);
    }

    [Fact]
    public void YouTube_streams_come_down_in_chunks_over_several_connections()
    {
        var item = new DownloadItem
        {
            Kind = DownloadKind.Media, Url = "https://www.youtube.com/watch?v=abc", Folder = @"C:\Videos", Connections = 16,
            Media = new MediaOptions { Height = 1080 },
        };
        var args = YtDlp.DownloadArgs(item, new ToolPaths(@"C:\VDM	ools"), null, @"C:	\p.txt");

        Assert.Equal("youtube:formats=dashy", args[args.IndexOf("--extractor-args") + 1]);
        Assert.Equal("16", args[args.IndexOf("--concurrent-fragments") + 1]);
    }

    [Fact]
    public void Other_sites_keep_their_own_formats_but_use_the_connection_setting()
    {
        var item = new DownloadItem
        {
            Kind = DownloadKind.Media, Url = "https://cdn.example.vn/live/master.m3u8", Folder = @"C:\Videos", Connections = 32,
            Media = new MediaOptions(),
        };
        var args = YtDlp.DownloadArgs(item, new ToolPaths(@"C:\VDM	ools"), null, @"C:	\p.txt");

        Assert.DoesNotContain("--extractor-args", args);
        Assert.Equal("16", args[args.IndexOf("--concurrent-fragments") + 1]); // more than 16 at once draws 429s
    }

    [Fact]
    public void Audio_args_extract_mp3_and_pass_browser_headers_off_youtube()
    {
        var item = new DownloadItem
        {
            Kind = DownloadKind.Media, Url = "https://cdn.example.vn/live/master.m3u8", Folder = @"C:\Music",
            Referrer = "https://phim.example.vn/xem/1", UserAgent = "UA",
            Media = new MediaOptions { AudioOnly = true },
        };
        var args = YtDlp.DownloadArgs(item, new ToolPaths(@"C:\VDM\tools"), @"C:\t\c.txt", @"C:\t\p.txt");

        Assert.Contains("-x", args);
        Assert.Equal("mp3", args[args.IndexOf("--audio-format") + 1]);
        Assert.Equal("https://phim.example.vn/xem/1", args[args.IndexOf("--referer") + 1]);
        Assert.Equal(@"C:\t\c.txt", args[args.IndexOf("--cookies") + 1]);
    }

    [Fact]
    public void Cookie_file_uses_netscape_columns()
    {
        var text = YtDlp.CookieFileText(new[]
        {
            new BrowserCookie { Name = "sid", Value = "1", Domain = ".example.vn", Path = "/", Secure = true, ExpirationDate = 1900000000.5 },
            new BrowserCookie { Name = "h", Value = "2", Domain = "www.example.vn", HostOnly = true },
        });
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("# Netscape HTTP Cookie File", lines[0]);
        Assert.Equal(".example.vn\tTRUE\t/\tTRUE\t1900000000\tsid\t1", lines[1]);
        Assert.Equal("www.example.vn\tFALSE\t/\tFALSE\t0\th\t2", lines[2]);
    }

    [Theory]
    [InlineData("ERROR: [youtube] abc: Sign in to confirm your age. This video may be inappropriate", "độ tuổi")]
    [InlineData("ERROR: [youtube] abc: Private video. Sign in if you've been granted access", "riêng tư")]
    [InlineData("ERROR: Unsupported URL: https://example.org", "chưa được hỗ trợ")]
    [InlineData(null, "Không tải được video.")]
    public void Errors_are_explained_in_vietnamese(string? raw, string expected) =>
        Assert.Contains(expected, YtDlp.FriendlyError(raw));
}
