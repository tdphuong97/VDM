using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace VDM.Core.Tests;

public sealed class BandwidthBalancerTests
{
    const double MB = 1024 * 1024;

    /// <summary>
    /// A home line shared by downloads the way TCP shares it: in proportion to how many connections each
    /// opens, up to its speed limit and whatever its server allows. Runs the balancer twice a second.
    /// </summary>
    static Dictionary<string, double> Simulate(double line, (string id, double weight, double server)[] flows, int ticks = 120,
        string? finishes = null, int finishAt = -1, int startDelay = 0)
    {
        var balancer = new BandwidthBalancer();
        var limits = flows.ToDictionary(f => f.id, _ => 0.0);
        var speeds = flows.ToDictionary(f => f.id, _ => 0.0);
        for (var t = 0; t < ticks; t++)
        {
            if (t == finishAt)
            {
                flows = flows.Where(f => f.id != finishes).ToArray();
                speeds.Remove(finishes!);
                limits.Remove(finishes!);
            }
            // Weighted water-filling: a flow takes its weighted share, capped by its limit and its server,
            // and like a real connection it speeds up step by step (about 60% more each half second).
            // Before startDelay the downloads are still asking the site for the video (yt-dlp takes 5-10 s):
            // a trickle of web pages and API answers, 150 KB/s.
            var cap = flows.ToDictionary(f => f.id, f => t < startDelay ? 0.15 * MB : Math.Min(Math.Min(f.server, limits[f.id] > 0 ? limits[f.id] : double.MaxValue),
                speeds[f.id] * 1.6 + 1 * MB));
            var open = flows.ToList();
            var left = line;
            while (open.Count > 0)
            {
                var weight = open.Sum(f => f.weight);
                var done = open.Where(f => cap[f.id] <= left * f.weight / weight).ToList();
                if (done.Count == 0)
                {
                    foreach (var f in open) speeds[f.id] = left * f.weight / weight;
                    break;
                }
                foreach (var f in done) { speeds[f.id] = cap[f.id]; left -= cap[f.id]; open.Remove(f); }
            }
            limits = new Dictionary<string, double>(balancer.Balance(speeds, limits));
        }
        return speeds;
    }

    [Fact]
    public void Two_hungry_downloads_share_the_line_even_when_one_opens_eight_times_the_connections()
    {
        var s = Simulate(60 * MB, new[] { ("4K", 8.0, double.MaxValue), ("8K", 1.0, double.MaxValue) });
        Assert.InRange(s["4K"] / MB, 26, 34);
        Assert.InRange(s["8K"] / MB, 26, 34);
    }

    [Fact]
    public void Three_downloads_on_21_MB_s_get_about_7_each()
    {
        var s = Simulate(21 * MB, new[] { ("a", 8.0, double.MaxValue), ("b", 1.0, double.MaxValue), ("c", 3.0, double.MaxValue) });
        foreach (var v in s.Values) Assert.InRange(v / MB, 6, 8);
    }

    [Fact]
    public void When_one_of_three_finishes_the_other_two_speed_up_to_half_each()
    {
        // 21 MB/s: 7 each while three run; one finishes, the two left go up to about 10.5 each.
        var during = Simulate(21 * MB, new[] { ("a", 8.0, double.MaxValue), ("b", 1.0, double.MaxValue), ("c", 3.0, double.MaxValue) }, ticks: 60);
        foreach (var v in during.Values) Assert.InRange(v / MB, 6, 8);
        var after = Simulate(21 * MB, new[] { ("a", 8.0, double.MaxValue), ("b", 1.0, double.MaxValue), ("c", 3.0, double.MaxValue) },
            ticks: 120, finishes: "c", finishAt: 60);
        Assert.InRange(after["a"] / MB, 9, 12);
        Assert.InRange(after["b"] / MB, 9, 12);
    }

    [Fact]
    public void A_download_its_server_keeps_slow_leaves_the_rest_of_the_line_to_the_others()
    {
        var s = Simulate(60 * MB, new[] { ("4K", 8.0, double.MaxValue), ("8K", 1.0, 2.4 * MB) });
        Assert.InRange(s["8K"] / MB, 2.3, 2.4);
        Assert.True(s["4K"] / MB > 50, $"the 4K got only {s["4K"] / MB:F1} MB/s of the 57.6 left");
    }

    [Fact]
    public void Downloads_starting_together_still_reach_the_whole_line()
    {
        var s = Simulate(60 * MB, new[] { ("a", 8.0, double.MaxValue), ("b", 8.0, double.MaxValue) }, ticks: 60);
        Assert.True(s.Values.Sum() / MB > 52, $"together they reached only {s.Values.Sum() / MB:F1} of 60 MB/s");
        foreach (var v in s.Values) Assert.InRange(v / MB, 24, 36);
    }

    [Fact]
    public void Downloads_that_take_a_while_to_start_still_get_the_whole_line()
    {
        var s = Simulate(60 * MB, new[] { ("a", 8.0, double.MaxValue), ("b", 8.0, double.MaxValue) }, ticks: 60, startDelay: 16);
        Assert.True(s.Values.Sum() / MB > 52, $"together they reached only {s.Values.Sum() / MB:F1} of 60 MB/s");
        foreach (var v in s.Values) Assert.InRange(v / MB, 24, 36);
    }

    [Fact]
    public void One_download_alone_is_never_limited()
    {
        var limits = new BandwidthBalancer().Balance(new Dictionary<string, double> { ["a"] = 40 * MB }, new Dictionary<string, double>());
        Assert.Equal(0, limits["a"]);
    }
}

public sealed class ThrottleTests
{
    [Fact]
    public async Task Holds_data_to_the_rate_and_lets_it_change_midway()
    {
        var t = new Throttle { Rate = 400 * 1024 };
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 20; i++) await t.TakeAsync(32 * 1024, CancellationToken.None); // 640 KB
        Assert.InRange(sw.Elapsed.TotalSeconds, 1.1, 2.2);

        t.Rate = 0; // no limit
        sw.Restart();
        for (var i = 0; i < 100; i++) await t.TakeAsync(64 * 1024, CancellationToken.None);
        Assert.True(sw.Elapsed.TotalSeconds < 0.3);
    }
}

public sealed class ThrottlingProxyTests
{
    [Fact]
    public async Task Plain_http_through_the_proxy_arrives_whole_and_at_the_limit()
    {
        using var server = new TestServer(600 * 1024);
        var throttle = new Throttle { Rate = 400 * 1024 };
        using var proxy = new ThrottlingProxy(throttle);
        using var http = new HttpClient(new HttpClientHandler { Proxy = new WebProxy(proxy.Url), UseProxy = true });

        var sw = Stopwatch.StartNew();
        var body = await http.GetByteArrayAsync(server.Url);

        Assert.Equal(server.Data, body);
        Assert.InRange(sw.Elapsed.TotalSeconds, 0.9, 3.0);
    }

    [Fact]
    public async Task Connect_opens_a_tunnel_like_https_uses()
    {
        using var server = new TestServer(200 * 1024);
        using var proxy = new ThrottlingProxy(new Throttle());
        var target = new Uri(server.Url);
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, new Uri(proxy.Url).Port);
        var stream = tcp.GetStream();
        var connect = $"CONNECT {target.Host}:{target.Port} HTTP/1.1\r\nHost: {target.Host}:{target.Port}\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(connect));
        var reply = await ReadLineAsync(stream);
        Assert.Contains(" 200 ", reply);
        while ((await ReadLineAsync(stream)).Length > 0) { }

        // Through the tunnel, talk to the server directly.
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"GET {target.PathAndQuery} HTTP/1.1\r\nHost: {target.Host}\r\nConnection: close\r\n\r\n"));
        var all = new MemoryStream();
        await stream.CopyToAsync(all);
        var text = Encoding.Latin1.GetString(all.ToArray());
        Assert.StartsWith("HTTP/1.1 200", text);
        Assert.Equal(server.Data.Length, all.Length - (text.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4));
    }

    static async Task<string> ReadLineAsync(Stream s)
    {
        var sb = new StringBuilder();
        var b = new byte[1];
        while (await s.ReadAsync(b) == 1)
        {
            if (b[0] == '\n') break;
            if (b[0] != '\r') sb.Append((char)b[0]);
        }
        return sb.ToString();
    }
}
