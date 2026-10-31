using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace VDM.Core.Tests;

/// <summary>A tiny HTTP file server with switches for the misbehaviours real servers have.</summary>
sealed class TestServer : IDisposable
{
    readonly HttpListener _listener = new();
    readonly CancellationTokenSource _cts = new();
    int _dropsLeft;

    public byte[] Data { get; }
    public bool SupportsRanges { get; set; } = true;
    public bool SendLength { get; set; } = true;
    public int ChunkSize { get; set; } = 16 * 1024;
    public int ChunkDelayMs { get; set; }
    public int StatusCode { get; set; } = 200;
    public string? ContentDisposition { get; set; }
    /// <summary>Abort the connection after this many bytes, this many times.</summary>
    public int DropAfterBytes { get; set; }
    public int Drops { set => _dropsLeft = value; }

    /// <summary>A shared "home line": every connection's data takes from it, chunk by chunk, so the line is
    /// split per connection the way TCP splits it.</summary>
    public Throttle? Line { get; set; }

    /// <summary>Requests beyond this many at once never get an answer, like servers that cap connections per IP.</summary>
    public int MaxConcurrent { get; set; } = int.MaxValue;
    int _active;

    public ConcurrentBag<string> RangesRequested { get; } = new();
    public string Url { get; }
    public string BaseUrl { get; }
    /// <summary>Other files to serve, by path ("/feed/latest.json"); every other path gets <see cref="Data"/>.</summary>
    public ConcurrentDictionary<string, byte[]> Files { get; } = new();

    public TestServer(int size, int seed = 1)
    {
        Data = new byte[size];
        new Random(seed).NextBytes(Data);
        var port = FreePort();
        BaseUrl = $"http://localhost:{port}";
        Url = $"{BaseUrl}/files/test.bin";
        _listener.Prefixes.Add($"http://localhost:{port}/");
        _listener.Start();
        _ = Task.Run(LoopAsync);
    }

    static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
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

    async Task HandleAsync(HttpListenerContext ctx)
    {
        var res = ctx.Response;
        var holding = Interlocked.Increment(ref _active) <= MaxConcurrent;
        if (!holding) Interlocked.Decrement(ref _active);
        try
        {
            var range = ctx.Request.Headers["Range"];
            RangesRequested.Add(range ?? "");
            if (!holding)
            {
                // Over the cap: the request is accepted but never answered.
                await Task.Delay(Timeout.Infinite, _cts.Token);
                return;
            }
            if (StatusCode != 200)
            {
                res.StatusCode = StatusCode;
                res.Close();
                return;
            }

            var data = Files.TryGetValue(ctx.Request.Url!.AbsolutePath, out var file) ? file : Data;
            long from = 0, to = data.Length - 1;
            if (SupportsRanges && range is not null && range.StartsWith("bytes="))
            {
                var parts = range["bytes=".Length..].Split('-');
                from = long.Parse(parts[0]);
                if (parts[1].Length > 0) to = Math.Min(long.Parse(parts[1]), data.Length - 1);
                res.StatusCode = 206;
                res.AddHeader("Content-Range", $"bytes {from}-{to}/{data.Length}");
            }
            if (SupportsRanges) res.AddHeader("Accept-Ranges", "bytes");
            if (ContentDisposition is not null) res.AddHeader("Content-Disposition", ContentDisposition);
            res.ContentType = "application/octet-stream";

            var length = to - from + 1;
            if (SendLength) res.ContentLength64 = length;
            else res.SendChunked = true;

            long sent = 0;
            var drop = DropAfterBytes > 0 && Interlocked.Decrement(ref _dropsLeft) >= 0;
            while (sent < length)
            {
                var n = (int)Math.Min(ChunkSize, length - sent);
                if (drop && sent + n > DropAfterBytes)
                {
                    res.Abort();
                    return;
                }
                if (Line is not null) await Line.TakeAsync(n, _cts.Token);
                await res.OutputStream.WriteAsync(data.AsMemory((int)(from + sent), n));
                sent += n;
                if (ChunkDelayMs > 0) await Task.Delay(ChunkDelayMs);
            }
            res.Close();
        }
        catch
        {
            try { res.Abort(); } catch { }
        }
        finally
        {
            if (holding) Interlocked.Decrement(ref _active);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); _listener.Close(); } catch { }
    }
}
