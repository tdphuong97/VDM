using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.Win32.SafeHandles;

namespace VDM.Core;

/// <summary>A failure the user has to act on; retrying the same request will not help.</summary>
public sealed class DownloadFailedException(string message, Exception? inner = null) : Exception(message, inner);

sealed class RangeNotSupportedException() : Exception("Máy chủ không trả đúng đoạn dữ liệu được yêu cầu.");

/// <summary>This connection is one more than the server will serve; the others carry on without it.</summary>
sealed class SurplusConnectionException() : Exception("Máy chủ không nhận thêm kết nối.");

/// <summary>
/// Fetches one HTTP file over several connections, IDM style: the file starts as one segment, and
/// every idle connection splits the segment with the most bytes left in half and takes the back
/// half. A connection that finishes early therefore keeps helping the slow ones until the end.
/// State lives in <see cref="DownloadItem.Segments"/>, so a paused download resumes where it stopped.
/// </summary>
public sealed class HttpDownloader
{
    public const int MinSplitBytes = 512 * 1024;
    const int BufferSize = 64 * 1024; // must stay below MinSplitBytes, see TakeWork
    const int MaxRetries = 8;

    public static string DefaultUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0.0.0 Safari/537.36";

    readonly HttpClient _http;
    readonly DownloadItem _item;
    readonly object _lock;
    SafeFileHandle? _handle;
    Exception? _firstError;
    int _workers;

    public TimeSpan ReadTimeout { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>How long to wait for a server to start answering a request.</summary>
    public TimeSpan HeaderTimeout { get; init; } = TimeSpan.FromSeconds(20);
    public Func<int, TimeSpan> Backoff { get; init; } = n => TimeSpan.FromSeconds(Math.Min(30, 1 << Math.Min(n, 5)));
    /// <summary>The download's share of the line; every connection takes from it.</summary>
    public Throttle? Limiter { get; init; }
    /// <summary>For a file named by the server: the folder that name belongs in.</summary>
    public Func<string, string>? FolderForName { get; init; }
    /// <summary>Names other downloads have already claimed in the same folder.</summary>
    public Func<ISet<string>>? TakenNames { get; init; }

    public HttpDownloader(HttpClient http, DownloadItem item)
    {
        _http = http;
        _item = item;
        _lock = item.Sync;
    }

    public static HttpClient CreateClient() => new(new SocketsHttpHandler
    {
        UseCookies = false, // the browser's cookies arrive as a header
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = 20,
        AutomaticDecompression = DecompressionMethods.None,
        ConnectTimeout = TimeSpan.FromSeconds(20),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        MaxConnectionsPerServer = 256, // 32 connections each for several downloads from one site
    })
    { Timeout = Timeout.InfiniteTimeSpan };

    public sealed record LinkInfo(string FileName, long Size, bool Resumable, string? Mime);

    /// <summary>Reads name, size and range support for the "add download" dialog, fetching one byte.</summary>
    public static async Task<LinkInfo> InspectAsync(HttpClient http, FileRequest req, CancellationToken ct)
    {
        var probe = new HttpDownloader(http, new DownloadItem
        {
            Url = req.Url, Referrer = req.Referrer, UserAgent = req.UserAgent, CookieHeader = req.CookieHeader,
        });
        using var msg = probe.NewRequest();
        msg.Headers.Range = new RangeHeaderValue(0, 0);
        using var resp = await probe.SendWithTimeoutAsync(msg, ct);
        CheckStatus(resp);
        var resumable = resp.StatusCode == HttpStatusCode.PartialContent;
        var size = resumable
            ? resp.Content.Headers.ContentRange?.Length ?? -1
            : resp.Content.Headers.ContentLength ?? -1;
        var mime = resp.Content.Headers.ContentType?.MediaType;
        var name = FileNames.Choose(req.FileName, resp.Content.Headers.ContentDisposition,
            resp.RequestMessage?.RequestUri ?? new Uri(req.Url), mime);
        return new LinkInfo(name, size, resumable && size > 0, mime);
    }

    public async Task RunAsync(CancellationToken ct)
    {
        _firstError = null;
        HttpResponseMessage? first = null;
        try
        {
            if (NeedsFreshStart())
                first = await ProbeWithRetryAsync(ct);

            PreparePartFile();
            _handle = File.OpenHandle(_item.PartPath, FileMode.Open, FileAccess.ReadWrite,
                FileShare.Read, FileOptions.Asynchronous);

            try
            {
                await RunWorkersAsync(first, ct);
            }
            catch (RangeNotSupportedException) when (_item.ResumeSupported)
            {
                // The server advertised ranges but ignored them: start over on one connection.
                _handle.Dispose();
                _item.ResumeSupported = false;
                _item.Segments.Clear();
                first = await ProbeWithRetryAsync(ct, forceSingle: true);
                PreparePartFile();
                _handle = File.OpenHandle(_item.PartPath, FileMode.Open, FileAccess.ReadWrite,
                    FileShare.Read, FileOptions.Asynchronous);
                await RunWorkersAsync(first, ct);
            }

            Finish();
        }
        finally
        {
            first?.Dispose();
            _handle?.Dispose();
            _handle = null;
            lock (_lock)
                foreach (var s in _item.Segments) { s.Active = false; s.Speed = 0; }
        }
    }

    bool NeedsFreshStart()
    {
        if (_item.Segments.Count == 0) return true;
        if (!_item.ResumeSupported || _item.TotalBytes <= 0) return true;
        var part = new FileInfo(_item.PartPath);
        return !part.Exists || part.Length != _item.TotalBytes;
    }

    HttpRequestMessage NewRequest()
    {
        var req = new HttpRequestMessage(HttpMethod.Get, _item.Url);
        req.Headers.TryAddWithoutValidation("User-Agent",
            string.IsNullOrWhiteSpace(_item.UserAgent) ? DefaultUserAgent : _item.UserAgent);
        req.Headers.TryAddWithoutValidation("Accept", "*/*");
        if (!string.IsNullOrWhiteSpace(_item.Referrer))
            req.Headers.TryAddWithoutValidation("Referer", _item.Referrer);
        if (!string.IsNullOrWhiteSpace(_item.CookieHeader))
            req.Headers.TryAddWithoutValidation("Cookie", _item.CookieHeader);
        return req;
    }

    async Task<HttpResponseMessage> ProbeWithRetryAsync(CancellationToken ct, bool forceSingle = false)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await ProbeAsync(ct, forceSingle);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TimeoutException
                                       || (ex is OperationCanceledException && !ct.IsCancellationRequested))
            {
                if (attempt >= 3)
                    throw new DownloadFailedException("Không kết nối được tới máy chủ: " + ex.Message, ex);
                await Task.Delay(Backoff(attempt), ct);
            }
        }
    }

    /// <summary>Asks for "bytes=0-" and reads only the headers: size, range support and file name.</summary>
    async Task<HttpResponseMessage> ProbeAsync(CancellationToken ct, bool forceSingle)
    {
        var req = NewRequest();
        if (!forceSingle) req.Headers.Range = new RangeHeaderValue(0, null);
        var resp = await SendWithTimeoutAsync(req, ct);
        try
        {
            CheckStatus(resp);
        }
        catch
        {
            resp.Dispose();
            throw;
        }

        long total = -1;
        var ranges = false;
        if (resp.StatusCode == HttpStatusCode.PartialContent && resp.Content.Headers.ContentRange is { HasLength: true } cr)
        {
            total = cr.Length!.Value;
            ranges = !forceSingle;
        }
        else
        {
            total = resp.Content.Headers.ContentLength ?? -1;
        }

        if (string.IsNullOrWhiteSpace(_item.FileName))
        {
            var finalUri = resp.RequestMessage?.RequestUri ?? new Uri(_item.Url);
            var name = FileNames.Choose(null, resp.Content.Headers.ContentDisposition, finalUri,
                resp.Content.Headers.ContentType?.MediaType);
            if (FolderForName is not null && _item.AutoFolder)
            {
                _item.Folder = FolderForName(name);
                _item.AutoFolder = false;
            }
            _item.FileName = FileNames.MakeUnique(_item.Folder, name, TakenNames?.Invoke());
        }

        lock (_lock)
        {
            _item.TotalBytes = total;
            _item.ResumeSupported = ranges && total > 0;
            _item.Segments.Clear();
            _item.Segments.Add(new Segment { Start = 0, End = total > 0 ? total - 1 : -1 });
        }
        return resp;
    }

    void PreparePartFile()
    {
        Directory.CreateDirectory(_item.Folder);
        var fresh = _item.Segments.All(s => s.Downloaded == 0);
        using var fs = new FileStream(_item.PartPath, fresh ? FileMode.Create : FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        if (_item.TotalBytes > 0 && fs.Length != _item.TotalBytes)
        {
            SparseFile.Set(fs.SafeFileHandle, true);
            fs.SetLength(_item.TotalBytes);
        }
    }

    async Task RunWorkersAsync(HttpResponseMessage? first, CancellationToken ct)
    {
        using var workCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var connections = _item.ResumeSupported ? Math.Clamp(_item.Connections, 1, 32) : 1;

        Segment? firstSeg = null;
        if (first is not null)
            lock (_lock)
            {
                firstSeg = _item.Segments[0];
                firstSeg.Active = true;
            }

        var tasks = new List<Task> { GuardAsync(() => WorkerAsync(firstSeg, first, null, workCts.Token), workCts) };
        for (var i = 1; i < connections; i++)
        {
            // Open connections one at a time, each once the server has answered the one before, as IDM
            // does. Servers that cap connections per visitor just stop answering; then we stop asking.
            var answered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            tasks.Add(GuardAsync(() => WorkerAsync(null, null, answered, workCts.Token), workCts));
            var done = await Task.WhenAny(answered.Task, Task.Delay(HeaderTimeout, workCts.Token));
            if (done != answered.Task || !answered.Task.Result) break;
        }
        await Task.WhenAll(tasks);

        ct.ThrowIfCancellationRequested();
        if (_firstError is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(_firstError).Throw();
    }

    async Task GuardAsync(Func<Task> work, CancellationTokenSource workCts)
    {
        try
        {
            await work();
        }
        catch (OperationCanceledException) when (workCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            lock (_lock) _firstError ??= ex;
            workCts.Cancel();
        }
    }

    /// <param name="answered">Set true once the server answers this connection, false if it never does.</param>
    async Task WorkerAsync(Segment? seg, HttpResponseMessage? response, TaskCompletionSource<bool>? answered, CancellationToken ct)
    {
        Interlocked.Increment(ref _workers);
        try
        {
            while (true)
            {
                seg ??= TakeWork();
                if (seg is null) return;
                try
                {
                    await FetchSegmentAsync(seg, response, answered, ct);
                }
                catch (SurplusConnectionException)
                {
                    return; // the segment goes back to the pool for a connection that works
                }
                finally
                {
                    lock (_lock) { seg.Active = false; seg.Speed = 0; }
                }
                seg = null;
                response = null;
            }
        }
        finally
        {
            Interlocked.Decrement(ref _workers);
            answered?.TrySetResult(false);
        }
    }

    /// <summary>
    /// Returns an unfinished segment nobody is working on, or splits the busiest one. A split puts
    /// the new boundary at least MinSplitBytes past the owner's committed position, and the owner
    /// never has more than BufferSize bytes in flight, so it can never write past its new end.
    /// </summary>
    Segment? TakeWork()
    {
        lock (_lock)
        {
            var idle = _item.Segments.FirstOrDefault(s => !s.Active && !s.IsDone);
            if (idle is not null)
            {
                idle.Active = true;
                return idle;
            }
            if (!_item.ResumeSupported) return null;

            Segment? big = null;
            foreach (var s in _item.Segments)
                if (s.Active && s.End >= 0 && (big is null || s.Remaining > big.Remaining))
                    big = s;
            if (big is null || big.Remaining < 2L * MinSplitBytes) return null;

            var mid = big.Position + big.Remaining / 2;
            var created = new Segment { Start = mid, End = big.End, Active = true };
            big.End = mid - 1;
            _item.Segments.Insert(_item.Segments.IndexOf(big) + 1, created);
            return created;
        }
    }

    async Task FetchSegmentAsync(Segment seg, HttpResponseMessage? response, TaskCompletionSource<bool>? answered, CancellationToken ct)
    {
        var failures = 0;
        while (true)
        {
            lock (_lock)
                if (seg.IsDone) { response?.Dispose(); return; }

            var before = seg.Downloaded;
            try
            {
                response ??= await SendRangeAsync(seg, ct);
                answered?.TrySetResult(true);
                var ended = await CopyAsync(seg, response, ct);
                response.Dispose();
                response = null;

                lock (_lock)
                {
                    if (seg.IsDone) return;
                    if (ended && seg.End < 0)
                    {
                        // Unknown size: the end of the stream is the end of the file.
                        seg.End = seg.Position - 1;
                        _item.TotalBytes = seg.Downloaded;
                        return;
                    }
                }
                throw new IOException("Máy chủ đóng kết nối giữa chừng.");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                response?.Dispose();
                throw;
            }
            catch (Exception ex) when (ex is not DownloadFailedException and not RangeNotSupportedException)
            {
                response?.Dispose();
                response = null;
                if (seg.Downloaded > before) failures = 0;
                // Nothing came through while other connections are working: the server does not want
                // this one. Retrying it would only keep its bytes away from the connections it serves.
                else if (Volatile.Read(ref _workers) > 1) throw new SurplusConnectionException();
                if (++failures > MaxRetries)
                    throw new DownloadFailedException("Mất kết nối quá nhiều lần: " + ex.Message, ex);

                if (!_item.ResumeSupported)
                {
                    // No ranges: the only way to continue is from the first byte.
                    lock (_lock) seg.Downloaded = 0;
                }
                await Task.Delay(Backoff(failures), ct);
            }
        }
    }

    async Task<HttpResponseMessage> SendRangeAsync(Segment seg, CancellationToken ct)
    {
        var req = NewRequest();
        long from, to;
        lock (_lock) { from = seg.Position; to = seg.End; }
        if (_item.ResumeSupported && to >= 0)
            req.Headers.Range = new RangeHeaderValue(from, to);

        var resp = await SendWithTimeoutAsync(req, ct);
        try
        {
            CheckStatus(resp);
            if (req.Headers.Range is not null && from > 0)
            {
                if (resp.StatusCode != HttpStatusCode.PartialContent || resp.Content.Headers.ContentRange?.From != from)
                    throw new RangeNotSupportedException();
            }
            return resp;
        }
        catch
        {
            resp.Dispose();
            throw;
        }
    }

    /// <summary>Sends and waits for the headers, but not forever: some servers accept a connection and never answer.</summary>
    async Task<HttpResponseMessage> SendWithTimeoutAsync(HttpRequestMessage req, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(HeaderTimeout);
        try
        {
            return await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("Máy chủ không trả lời.");
        }
    }

    /// <summary>Copies until the segment is done (true if the stream also ended) or the stream ends.</summary>
    async Task<bool> CopyAsync(Segment seg, HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            while (true)
            {
                readCts.CancelAfter(ReadTimeout);
                int n;
                try
                {
                    n = await stream.ReadAsync(buffer.AsMemory(0, BufferSize), readCts.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new TimeoutException("Máy chủ ngừng gửi dữ liệu.");
                }
                if (n == 0) return true;
                if (Limiter is not null) await Limiter.TakeAsync(n, ct);

                long pos;
                int allowed;
                lock (_lock)
                {
                    pos = seg.Position;
                    allowed = seg.End < 0 ? n : (int)Math.Min(n, seg.End - pos + 1);
                }
                if (allowed > 0)
                {
                    try
                    {
                        await RandomAccess.WriteAsync(_handle!, buffer.AsMemory(0, allowed), pos, ct);
                    }
                    catch (IOException ex)
                    {
                        throw new DownloadFailedException(DiskMessage(ex), ex);
                    }
                }
                lock (_lock)
                {
                    seg.Downloaded += allowed;
                    if (seg.IsDone) return false;
                }
                if (allowed < n) return false;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    static string DiskMessage(IOException ex) => (ex.HResult & 0xFFFF) switch
    {
        0x70 or 0x27 => "Ổ đĩa đã đầy. Hãy giải phóng dung lượng rồi bấm tiếp tục.",
        _ => "Không ghi được file: " + ex.Message,
    };

    static void CheckStatus(HttpResponseMessage resp)
    {
        var code = (int)resp.StatusCode;
        if (code is >= 200 and < 300) return;
        if (code is 408 or 429 or >= 500)
            throw new HttpRequestException($"Máy chủ bận (HTTP {code}).", null, resp.StatusCode);
        throw code switch
        {
            401 or 403 => new DownloadFailedException(
                $"Máy chủ từ chối (HTTP {code}). Link có thể đã hết hạn, hãy bấm tải lại từ trang web."),
            404 or 410 => new DownloadFailedException($"Không tìm thấy file trên máy chủ (HTTP {code})."),
            416 => new DownloadFailedException("File trên máy chủ đã thay đổi kích thước (HTTP 416)."),
            _ => new DownloadFailedException($"Máy chủ trả lỗi HTTP {code}."),
        };
    }

    void Finish()
    {
        lock (_lock)
        {
            if (_item.TotalBytes > 0 && _item.Segments.Sum(s => s.Downloaded) != _item.TotalBytes)
                throw new DownloadFailedException("File tải về bị thiếu dữ liệu.");
        }
        _handle?.Dispose();
        _handle = null;

        var target = _item.FileName;
        if (File.Exists(Path.Combine(_item.Folder, target)))
        {
            var stem = Path.GetFileNameWithoutExtension(target);
            var ext = Path.GetExtension(target);
            for (var i = 1; File.Exists(Path.Combine(_item.Folder, target)); i++)
                target = $"{stem} ({i}){ext}";
        }
        var finalPath = Path.Combine(_item.Folder, target);
        File.Move(_item.PartPath, finalPath);
        _item.FileName = target;

        try
        {
            using var fs = new FileStream(finalPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
            SparseFile.Set(fs.SafeFileHandle, false);
        }
        catch (IOException)
        {
        }
    }
}
