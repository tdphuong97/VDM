namespace VDM.Core.Tests;

/// <summary>
/// Talks to YouTube through the bundled yt-dlp. Runs only when VDM_TOOLS points at deps\tools,
/// because it needs the network and the real programs.
/// </summary>
public sealed class MediaIntegrationTests : IDisposable
{
    static readonly string? Tools = Environment.GetEnvironmentVariable("VDM_TOOLS");
    const string Zoo = "https://www.youtube.com/watch?v=jNQXAC9IVRw"; // "Me at the zoo", 19 s
    readonly string _dir = Directory.CreateTempSubdirectory("vdm-media-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public async Task Probes_and_downloads_a_youtube_video_and_its_audio_as_mp3()
    {
        if (Tools is null) return;
        var media = new MediaDownloader(new ToolPaths(Tools));

        var info = await media.ProbeAsync(new MediaRequest { Url = Zoo }, CancellationToken.None);
        Assert.Contains("zoo", info.Title, StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(info.Qualities);
        var lowest = info.Qualities[^1].Height;

        var video = new DownloadItem
        {
            Kind = DownloadKind.Media, Url = Zoo, Folder = _dir, FileName = "zoo.mp4",
            Media = new MediaOptions { Height = lowest },
        };
        await media.RunAsync(video, CancellationToken.None);
        Assert.EndsWith(".mp4", video.FileName);
        Assert.True(new FileInfo(video.FullPath).Length > 100_000);
        Assert.False(Directory.Exists(YtDlp.TempFolder(video)));

        var audio = new DownloadItem
        {
            Kind = DownloadKind.Media, Url = Zoo, Folder = _dir, FileName = "zoo.mp3",
            Media = new MediaOptions { AudioOnly = true, AudioFormat = "mp3" },
        };
        await media.RunAsync(audio, CancellationToken.None);
        Assert.EndsWith(".mp3", audio.FileName);
        Assert.True(audio.TotalBytes > 50_000);
    }
}
