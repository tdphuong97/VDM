using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace VDM.Core;

/// <summary>
/// A speed limit that can change while data flows. All connections of one download share one, so the
/// limit holds for the download as a whole. Rate 0 means no limit.
/// </summary>
public sealed class Throttle
{
    readonly object _lock = new();
    double _rate;
    double _tokens;
    long _passed;

    /// <summary>Bytes that went through, limited or not: the download's real speed comes from this.</summary>
    public long Passed => Interlocked.Read(ref _passed);
    long _last = Stopwatch.GetTimestamp();

    /// <summary>Bytes per second; 0 lets everything through.</summary>
    public double Rate
    {
        get { lock (_lock) return _rate; }
        set
        {
            lock (_lock)
            {
                Refill();
                _rate = Math.Max(0, value);
                _tokens = Math.Min(_tokens, Burst);
            }
        }
    }

    double Burst => Math.Max(_rate * 0.25, 64 * 1024); // a quarter second of data may go at once

    void Refill()
    {
        var now = Stopwatch.GetTimestamp();
        var seconds = (now - _last) / (double)Stopwatch.Frequency;
        _last = now;
        if (_rate > 0) _tokens = Math.Min(Burst, _tokens + seconds * _rate);
    }

    /// <summary>Waits until <paramref name="bytes"/> may pass. The bytes are taken at once (going into
    /// debt if need be); whoever comes next waits the debt off, so the average holds the rate.</summary>
    public async ValueTask TakeAsync(int bytes, CancellationToken ct)
    {
        Interlocked.Add(ref _passed, bytes);
        while (true)
        {
            TimeSpan wait;
            lock (_lock)
            {
                if (_rate <= 0) return;
                Refill();
                if (_tokens >= 0)
                {
                    _tokens -= bytes;
                    return;
                }
                wait = TimeSpan.FromSeconds(Math.Min(0.5, -_tokens / _rate));
            }
            await Task.Delay(wait < TimeSpan.FromMilliseconds(1) ? TimeSpan.FromMilliseconds(1) : wait, ct);
        }
    }
}

/// <summary>
/// Shares the line between the downloads running at once: each gets an equal part (21 MB/s over three
/// is 7 MB/s each), except that one held back by its own server only keeps what it uses, plus room to
/// grow, and the rest goes to the others. Call it twice a second.
/// Only downloads that are moving data count: one still asking the site for the video (yt-dlp takes
/// 5-10 s) is neither limited nor given a part yet. The line's capacity is learnt by letting the
/// downloads run free: when one starts or stops moving, and for a moment every 30 seconds, no limits
/// apply until their total stops climbing (2 to 10 s); the most they brought in, then or in the last
/// 15 seconds, is what the parts are cut from.
/// </summary>
public sealed class BandwidthBalancer
{
    /// <summary>A download slower than this is still starting; it is given this much to grow into.</summary>
    const double Floor = 512 * 1024;
    /// <summary>Faster than this, a download is moving data rather than still starting or stalled.</summary>
    const double Moving = 64 * 1024;
    const int Window = 30;        // ticks of history kept: 15 s
    const int MinLearn = 4;       // a free run lasts at least 2 s
    const int MaxLearn = 20;      // and at most 10 s
    const int Relearn = 60;       // ticks between free runs: 30 s

    readonly Queue<double> _totals = new();
    HashSet<string> _ids = new();
    int _learning;      // ticks into the current free run, 0 when not learning
    int _sinceLearnt;
    double _learnt;     // the most the line carried in the last free run

    /// <summary>New limits (bytes/s, 0 = none) from each running download's speed and its current limit.</summary>
    public Dictionary<string, double> Balance(IReadOnlyDictionary<string, double> speeds, IReadOnlyDictionary<string, double> limits)
    {
        var result = speeds.Keys.ToDictionary(k => k, _ => 0.0);
        var total = speeds.Values.Sum();
        _totals.Enqueue(total);
        while (_totals.Count > Window) _totals.Dequeue();

        var moving = speeds.Where(kv => kv.Value > Moving).Select(kv => kv.Key).ToHashSet();
        if (!_ids.SetEquals(moving) || ++_sinceLearnt >= Relearn)
        {
            _ids = moving;
            _learning = 1;
            _sinceLearnt = 0;
            _learnt = 0;
        }
        if (moving.Count < 2) return result;
        if (_learning > 0)
        {
            _learnt = Math.Max(_learnt, total);
            // Real data, not yet the trickle of a site being asked for the video: the free run only
            // counts from then on, and lasts while the total keeps climbing (10% up on a second ago).
            var flowing = total >= Floor * moving.Count;
            var recent = _totals.Reverse().Take(3).ToList();
            var climbing = recent.Count == 3 && recent[0] > recent[2] * 1.1;
            if (!flowing || _learning < MinLearn || (climbing && _learning < MaxLearn))
            {
                if (flowing) _learning++;
                return result;
            }
            _learning = 0;
        }

        var capacity = Math.Max(_learnt, _totals.Max());
        if (capacity <= 0) return result;

        double Limit(string id) => limits.TryGetValue(id, out var l) ? l : 0;
        // Hungry: it uses (nearly) all it is allowed, so it would take more. With no limit, a download
        // that moves counts as hungry; one well under its limit is held back by something else.
        bool Hungry(string id) => Limit(id) <= 0 ? speeds[id] > Floor : speeds[id] >= 0.8 * Limit(id);
        var demand = moving.ToDictionary(id => id, id => Hungry(id) ? double.PositiveInfinity : Math.Max(speeds[id] * 1.5, Floor));

        // Water-filling: whoever wants less than an equal part gets what it wants; the rest share the remainder.
        var open = moving.ToList();
        var left = capacity;
        while (open.Count > 0)
        {
            var share = left / open.Count;
            var small = open.Where(id => demand[id] < share).ToList();
            if (small.Count == 0)
            {
                foreach (var id in open) result[id] = share;
                break;
            }
            foreach (var id in small)
            {
                result[id] = demand[id];
                left -= demand[id];
                open.Remove(id);
            }
        }
        return result;
    }
}

/// <summary>
/// A tiny HTTP proxy on 127.0.0.1 for one yt-dlp run, so VDM can hold that download to its share of the
/// line while it runs (yt-dlp's own --limit-rate is fixed at start). HTTPS goes through CONNECT tunnels,
/// untouched; plain HTTP requests are forwarded. Only what comes back from the server is slowed.
/// </summary>
public sealed class ThrottlingProxy : IDisposable
{
    readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    readonly CancellationTokenSource _cts = new();
    readonly Throttle _throttle;

    public string Url { get; }

    public ThrottlingProxy(Throttle throttle)
    {
        _throttle = throttle;
        _listener.Start();
        Url = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
        _ = Task.Run(AcceptLoopAsync);
    }

    async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
            catch { return; }
            _ = Task.Run(() => ServeAsync(client));
        }
    }

    async Task ServeAsync(TcpClient client)
    {
        using var _ = client;
        using var upstream = new TcpClient();
        var ct = _cts.Token;
        try
        {
            var down = client.GetStream();
            var (head, extra) = await ReadHeadAsync(down, ct);
            if (head is null) return;
            var lines = head.Split("\r\n");
            var parts = lines[0].Split(' ');
            if (parts.Length < 3) return;

            Stream up;
            if (parts[0].Equals("CONNECT", StringComparison.OrdinalIgnoreCase))
            {
                var (host, port) = HostPort(parts[1], 443);
                await upstream.ConnectAsync(host, port, ct);
                up = upstream.GetStream();
                await down.WriteAsync("HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray(), ct);
            }
            else
            {
                if (!Uri.TryCreate(parts[1], UriKind.Absolute, out var uri)) return;
                await upstream.ConnectAsync(uri.Host, uri.Port, ct);
                up = upstream.GetStream();
                var sb = new StringBuilder($"{parts[0]} {uri.PathAndQuery} {parts[2]}\r\n");
                foreach (var line in lines.Skip(1))
                {
                    if (line.Length == 0) continue;
                    var name = line.Split(':', 2)[0].Trim();
                    if (name.Equals("Proxy-Connection", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("Connection", StringComparison.OrdinalIgnoreCase)) continue;
                    sb.Append(line).Append("\r\n");
                }
                sb.Append("Connection: close\r\n\r\n");
                await up.WriteAsync(Encoding.Latin1.GetBytes(sb.ToString()), ct);
            }
            if (extra.Length > 0) await up.WriteAsync(extra, ct);

            using var pair = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var toServer = PumpAsync(down, up, null, pair.Token);
            var toClient = PumpAsync(up, down, _throttle, pair.Token);
            await Task.WhenAny(toServer, toClient);
            pair.Cancel(); // one side closed: close the other
        }
        catch (Exception)
        {
            // The client or the server went away; nothing to tell anyone.
        }
    }

    static (string host, int port) HostPort(string target, int fallback)
    {
        var colon = target.LastIndexOf(':');
        if (colon > 0 && int.TryParse(target[(colon + 1)..], out var port) && !target.EndsWith(']'))
            return (target[..colon].Trim('[', ']'), port);
        return (target.Trim('[', ']'), fallback);
    }

    /// <summary>Reads up to the blank line; returns the head and any body bytes read past it.</summary>
    static async Task<(string? head, byte[] extra)> ReadHeadAsync(Stream s, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        var filled = 0;
        while (filled < buffer.Length)
        {
            var n = await s.ReadAsync(buffer.AsMemory(filled), ct);
            if (n == 0) return (null, Array.Empty<byte>());
            filled += n;
            var end = buffer.AsSpan(0, filled).IndexOf("\r\n\r\n"u8);
            if (end >= 0)
                return (Encoding.Latin1.GetString(buffer, 0, end), buffer[(end + 4)..filled]);
        }
        return (null, Array.Empty<byte>());
    }

    static async Task PumpAsync(Stream from, Stream to, Throttle? throttle, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var n = await from.ReadAsync(buffer, ct);
            if (n == 0) return;
            if (throttle is not null) await throttle.TakeAsync(n, ct);
            await to.WriteAsync(buffer.AsMemory(0, n), ct);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
    }
}
