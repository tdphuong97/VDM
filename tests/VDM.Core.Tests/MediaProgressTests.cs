namespace VDM.Core.Tests;

public sealed class MediaProgressTests
{
    static MediaProgress P(string format, long done, long? total = null, long? estimate = null) =>
        new("downloading", done, total, estimate, null, format);

    [Fact]
    public void Video_then_audio_add_up()
    {
        var acc = new MediaProgressTally(estimate: -1);
        acc.Apply(P("137", 770_000_000, 770_000_000));
        acc.Apply(P("140", 40_000_000, 89_000_000));
        Assert.Equal(810_000_000, acc.Done);
        Assert.Equal(859_000_000, acc.Total);
    }

    [Fact]
    public void A_restarted_stream_is_not_counted_twice()
    {
        // yt-dlp starts the same format again (a retry, a new URL): its count drops and climbs again.
        var acc = new MediaProgressTally(estimate: 860_000_000);
        acc.Apply(P("137", 300_000_000, 770_000_000));
        acc.Apply(P("137", 10_000_000, 770_000_000));
        acc.Apply(P("137", 400_000_000, 770_000_000));
        Assert.Equal(400_000_000, acc.Done);
        Assert.Equal(860_000_000, acc.Total);
    }

    [Fact]
    public void The_size_known_before_starting_beats_a_wild_early_estimate()
    {
        var acc = new MediaProgressTally(estimate: 860_000_000);
        acc.Apply(P("137", 5_000_000, null, estimate: 3_300_000_000));
        Assert.Equal(860_000_000, acc.Total);
    }

    [Fact]
    public void Without_a_known_size_the_reported_totals_are_used()
    {
        var acc = new MediaProgressTally(estimate: -1);
        acc.Apply(P("hls-720", 5_000_000, null, estimate: 120_000_000));
        Assert.Equal(120_000_000, acc.Total);
    }

    [Fact]
    public void The_total_never_falls_below_what_is_already_downloaded()
    {
        var acc = new MediaProgressTally(estimate: 100);
        acc.Apply(P("137", 500));
        Assert.Equal(500, acc.Total);
    }
}
