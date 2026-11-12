using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using CoolhandLabs.Internal;
using CoolhandLabs.Services;

namespace CoolhandLabs.Http;

/// <summary>
/// Decides which requests to capture and turns captured traffic into log submissions: the
/// client-independent half of every integration (coolhand-node's <c>RequestMonitoringService</c>
/// without the global patching). Each <see cref="Coolhand"/> client owns one; <see cref="CoolhandHandler"/>
/// calls <see cref="Begin(string, string, IEnumerable{KeyValuePair{string, IEnumerable{string}}})"/> for every request it sees.
/// </summary>
/// <remarks>
/// Use this to monitor an HTTP stack this SDK has no integration for: call <c>Begin</c> on the calling
/// flow, hand it the request body, then feed the response through <see cref="CaptureSession.Response"/>
/// and <see cref="ResponseCapture.Wrap"/> (or the <c>OnBytes</c>/<c>Complete</c>/<c>Abandoned</c> callbacks).
/// </remarks>
public sealed class RequestMonitor
{
    /// <summary>Receives each completed call.</summary>
    public delegate void Submitter(CallData callData, MatchedPattern matchedPattern, DeclaredContext? declared);

    private readonly Submitter _submitter;
    private readonly UrlFilters.SelfEndpoint? _selfEndpoint;
    private volatile IReadOnlyList<string> _excludeApiPatterns;
    private long _totalRequests;
    private long _interceptedCalls;
    private int _finalizing;

    /// <summary>Creates a monitor.</summary>
    /// <param name="patterns">Provider patterns and redaction.</param>
    /// <param name="excludeApiPatterns">URL substrings never captured.</param>
    /// <param name="selfApiEndpoint">The Coolhand API endpoint, never captured.</param>
    /// <param name="silent">Suppress progress logging.</param>
    /// <param name="idleTimeout">How long a response may sit unread before it is logged as incomplete (zero disables).</param>
    /// <param name="submitter">Receives each completed call.</param>
    public RequestMonitor(PatternMatchingService patterns, IReadOnlyList<string> excludeApiPatterns, string selfApiEndpoint, bool silent,
        TimeSpan idleTimeout, Submitter submitter)
    {
        Patterns = patterns;
        _excludeApiPatterns = excludeApiPatterns.ToList();
        _selfEndpoint = UrlFilters.ComputeSelfEndpoint(selfApiEndpoint);
        Log = new Log(silent);
        IdleTimeout = idleTimeout;
        _submitter = submitter;
    }

    internal PatternMatchingService Patterns { get; }

    internal Log Log { get; }

    internal TimeSpan IdleTimeout { get; }

    /// <summary>
    /// Starts capturing <paramref name="method"/> <paramref name="url"/> when it is a recognised LLM call
    /// that is not excluded, not the Coolhand API itself and not a non-inference endpoint; null otherwise
    /// (pass the request through untouched). The active declared context is snapshotted now.
    /// </summary>
    /// <param name="method">HTTP method.</param>
    /// <param name="url">Absolute URL.</param>
    /// <param name="headers">Request headers (any case; values redacted before logging).</param>
    public CaptureSession? Begin(string method, string url, IEnumerable<KeyValuePair<string, IEnumerable<string>>>? headers) =>
        Begin(method, url, headers, DeclaredContexts.Current);

    /// <summary>As <see cref="Begin(string, string, IEnumerable{KeyValuePair{string, IEnumerable{string}}})"/>, with an explicit declared context (null for none).</summary>
    public CaptureSession? Begin(string method, string url, IEnumerable<KeyValuePair<string, IEnumerable<string>>>? headers,
        DeclaredContext? declared)
    {
        try
        {
            Interlocked.Increment(ref _totalRequests);
            Log.Info(() => "🌐 REQUEST to: " + HostOf(url));
            var matched = Patterns.MatchesApiPatternFromUrl(url);
            if (matched is null) return null;
            if (UrlFilters.IsSelfOrExcluded(url, _selfEndpoint, _excludeApiPatterns!) || UrlFilters.IsNonInferenceUrl(url, method)) return null;
            var id = Interlocked.Increment(ref _interceptedCalls);
            Log.Info("🎯 INTERCEPTING " + matched.Pattern.Name + " call");
            var session = new CaptureSession(this, id, method, url, headers, matched, declared);
            Log.Info("📞 Starting API call #" + id + " to " + session.SanitizedUrl);
            return session;
        }
        catch (Exception e)
        {
            // Deciding whether to capture must never break the host's request.
            Log.Info("⚠️ Could not prepare capture; not intercepting this request: " + e.Message);
            return null;
        }
    }

    /// <summary>Hostname-only rendering for debug logs (a URL may carry a secret in its query).</summary>
    private static string HostOf(string url) => UrlParts.Parse(url)?.Hostname ?? "unknown";

    internal void Submit(CallData callData, MatchedPattern matched, DeclaredContext? declared) => _submitter(callData, matched, declared);

    internal void FinalizeStarted() => Interlocked.Increment(ref _finalizing);

    internal void FinalizeEnded() => Interlocked.Decrement(ref _finalizing);

    /// <summary>Captures whose body ended but whose log has not been handed to the submitter yet.</summary>
    public int PendingFinalizations => Volatile.Read(ref _finalizing);

    /// <summary>The URL substrings never captured. Setting it warns about glob-like entries.</summary>
    public IReadOnlyList<string> ExcludeApiPatterns
    {
        get => _excludeApiPatterns;
        set
        {
            UrlFilters.WarnOnGlobLikePatterns(value);
            _excludeApiPatterns = (value ?? Array.Empty<string>()).ToList();
        }
    }

    /// <summary>Requests seen.</summary>
    public long TotalRequests => Interlocked.Read(ref _totalRequests);

    /// <summary>Requests captured.</summary>
    public long InterceptedCalls => Interlocked.Read(ref _interceptedCalls);
}

/// <summary>
/// One captured request, from <see cref="RequestMonitor.Begin(string, string, IEnumerable{KeyValuePair{string, IEnumerable{string}}})"/>
/// until its response is complete. The URL, request headers and declared context are fixed when the session starts.
/// </summary>
public sealed class CaptureSession
{
    private readonly CappedBuffer _requestBuffer;

    internal CaptureSession(RequestMonitor monitor, long id, string? method, string url,
        IEnumerable<KeyValuePair<string, IEnumerable<string>>>? headers, MatchedPattern matched, DeclaredContext? declared)
    {
        Monitor = monitor;
        Id = id;
        Timestamp = JsText.IsoTimestamp(DateTimeOffset.UtcNow);
        Method = method ?? "GET";
        SanitizedUrl = monitor.Patterns.SanitizeUrl(url);
        SanitizedHeaders = monitor.Patterns.SanitizeHeaders(headers, matched.Pattern);
        Matched = matched;
        Declared = declared;
        Protocol = UrlParts.Parse(url)?.Scheme ?? "https";
        _requestBuffer = new CappedBuffer(CappedBuffer.MaxBytes, () => monitor.Log.Info(
            "⚠️ Request body for call #" + id + " exceeded " + CappedBuffer.MaxBytes + " bytes; truncating capture"));
    }

    internal RequestMonitor Monitor { get; }

    /// <summary>The call number within its client.</summary>
    public long Id { get; }

    internal string Timestamp { get; }

    internal string Method { get; }

    /// <summary>The URL as it will be logged (credential query parameters redacted).</summary>
    public string SanitizedUrl { get; }

    internal IReadOnlyDictionary<string, string> SanitizedHeaders { get; }

    internal MatchedPattern Matched { get; }

    /// <summary>The declared context snapshotted when the session began, or null.</summary>
    public DeclaredContext? Declared { get; }

    internal string Protocol { get; }

    /// <summary>Records (up to 50 MiB of) the request body. Call before the response arrives.</summary>
    public void RequestBody(ReadOnlySpan<byte> bytes) => _requestBuffer.Push(bytes);

    internal string? RequestText() => _requestBuffer.Size == 0 ? null : Encoding.UTF8.GetString(_requestBuffer.ToArray());

    /// <summary>
    /// The response arrived: returns the capture its body must be fed through. The call is logged once
    /// that body ends, is abandoned, or sits unread for the idle timeout.
    /// </summary>
    public ResponseCapture Response(int statusCode, IEnumerable<KeyValuePair<string, IEnumerable<string>>>? headers)
    {
        Monitor.Log.Info("📥 Response received for call #" + Id + ", status: " + statusCode);
        return new ResponseCapture(this, statusCode, headers);
    }

    /// <summary>The request failed before any response: nothing is logged (as in coolhand-node).</summary>
    public void Failed(Exception? error) =>
        Monitor.Log.Info("❌ Request error for call #" + Id + ": " + (error?.Message ?? "unknown"));
}

/// <summary>
/// Captures a response body as the host reads it, bounded at 50 MiB, and logs the call exactly once:
/// when the body ends (<see cref="Complete"/>), when the host abandons it (<see cref="Abandoned"/>, sent
/// with <c>stream_incomplete: true</c> and the partial body), or when the host stops reading for the
/// idle timeout (30 seconds by default), whichever comes first. The host's data is never altered.
/// </summary>
public sealed class ResponseCapture
{
    private static readonly string[] StreamingTypes =
        { "text/event-stream", "application/x-ndjson", "application/jsonl", "application/vnd.amazon.eventstream" };

    private readonly CaptureSession _session;
    private readonly int _statusCode;
    private readonly List<KeyValuePair<string, IEnumerable<string>>> _headers;
    private readonly CappedBuffer _buffer;
    private readonly object _timerLock = new();
    private Timer? _idleTimer;
    private int _finished;

    internal ResponseCapture(CaptureSession session, int statusCode, IEnumerable<KeyValuePair<string, IEnumerable<string>>>? headers)
    {
        _session = session;
        _statusCode = statusCode;
        _headers = headers?.Select(h => new KeyValuePair<string, IEnumerable<string>>(h.Key, h.Value.ToList())).ToList()
                   ?? new List<KeyValuePair<string, IEnumerable<string>>>();
        var contentType = Header("content-type")?.ToLowerInvariant() ?? "";
        IsStreaming = StreamingTypes.Any(t => contentType.Contains(t, StringComparison.Ordinal));
        _buffer = new CappedBuffer(CappedBuffer.MaxBytes, () => session.Monitor.Log.Info(
            "⚠️ Response body for call #" + session.Id + " exceeded " + CappedBuffer.MaxBytes + " bytes; truncating capture"));
        Touch();
    }

    /// <summary>First value of a response header (case-insensitive), or null.</summary>
    public string? Header(string name)
    {
        foreach (var pair in _headers)
        {
            if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                var first = pair.Value.FirstOrDefault();
                if (first is not null) return first;
            }
        }
        return null;
    }

    /// <summary>
    /// True for streamed responses (server-sent events, NDJSON, AWS event streams). Disposing such a body
    /// early logs what was read as incomplete; disposing any other body early first reads the rest of it
    /// (up to the cap) so a parser that stops at the end of the JSON value still logs a complete call.
    /// </summary>
    public bool IsStreaming { get; }

    /// <summary>True once the call has been handed off for logging.</summary>
    public bool IsFinished => Volatile.Read(ref _finished) != 0;

    /// <summary>Body bytes the host received.</summary>
    public void OnBytes(ReadOnlySpan<byte> bytes)
    {
        if (IsFinished || bytes.Length == 0) return;
        _buffer.Push(bytes);
        Touch();
    }

    /// <summary>The body was read to its end.</summary>
    public void Complete() => Finish(false);

    /// <summary>The host stopped before the end (cancel, dispose, error): logged with <c>stream_incomplete</c>.</summary>
    public void Abandoned() => Finish(true);

    /// <summary>A host-facing stream that captures what passes through it.</summary>
    public Stream Wrap(Stream body) => new CapturingReadStream(body, this);

    /// <summary>Restarts the idle timer; integrations call this whenever the host shows activity.</summary>
    public void Touch()
    {
        var timeout = _session.Monitor.IdleTimeout;
        if (timeout <= TimeSpan.Zero || IsFinished) return;
        lock (_timerLock)
        {
            if (IsFinished) return;
            if (_idleTimer is null)
            {
                _idleTimer = new Timer(static state => ((ResponseCapture)state!).Abandoned(), this, timeout, Timeout.InfiniteTimeSpan);
            }
            else
            {
                _idleTimer.Change(timeout, Timeout.InfiniteTimeSpan);
            }
        }
    }

    private void Finish(bool incomplete)
    {
        if (Interlocked.Exchange(ref _finished, 1) != 0) return;
        lock (_timerLock)
        {
            _idleTimer?.Dispose();
            _idleTimer = null;
        }
        var monitor = _session.Monitor;
        monitor.FinalizeStarted();
        try
        {
            _ = Task.Run(() => Submit(incomplete));
        }
        catch
        {
            Submit(incomplete);
        }
    }

    private void Submit(bool incomplete)
    {
        var monitor = _session.Monitor;
        try
        {
            var patterns = monitor.Patterns;
            var requestBody = BodyParser.ParseAndSanitize(_session.RequestText(), patterns.SanitizeBody,
                e => monitor.Log.Info("⚠️ Request body sanitize failed for call #" + _session.Id + ": " + e.Message));
            var text = Decompress.Run(_buffer.ToArray(), Header("content-encoding"), monitor.Log.Info, incomplete);
            var responseBody = BodyParser.ParseAndSanitize(text, patterns.SanitizeBody,
                e => monitor.Log.Info("⚠️ Response body sanitize failed for call #" + _session.Id + ": " + e.Message));
            var callData = new CallData
            {
                Id = _session.Id,
                Timestamp = _session.Timestamp,
                Method = _session.Method,
                Url = _session.SanitizedUrl,
                Headers = _session.SanitizedHeaders,
                RequestBody = requestBody,
                ResponseBody = responseBody,
                ResponseHeaders = patterns.SanitizeHeaders(_headers, _session.Matched.Pattern),
                StatusCode = _statusCode,
                Protocol = _session.Protocol,
                StreamIncomplete = incomplete,
            };
            monitor.Submit(callData, _session.Matched, _session.Declared);
        }
        catch (Exception e)
        {
            monitor.Log.Info("⚠️ Could not complete call #" + _session.Id + ": " + e.Message);
        }
        finally
        {
            monitor.FinalizeEnded();
        }
    }
}

/// <summary>
/// Hands the host the response body unchanged while copying it into a <see cref="ResponseCapture"/>.
/// End of input completes the capture; a read error, a cancellation or an early dispose of a streamed
/// body abandons it; an early dispose of any other body drains the rest (up to 50 MiB) in the background first.
/// </summary>
internal sealed class CapturingReadStream : Stream
{
    private readonly Stream _inner;
    private readonly ResponseCapture _capture;
    private int _disposed;

    public CapturingReadStream(Stream inner, ResponseCapture capture)
    {
        _inner = inner;
        _capture = capture;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() { }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    private int Observe(int n, ReadOnlySpan<byte> data)
    {
        if (n <= 0) _capture.Complete();
        else _capture.OnBytes(data.Slice(0, n));
        return n;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        int n;
        try
        {
            n = _inner.Read(buffer, offset, count);
        }
        catch
        {
            _capture.Abandoned();
            throw;
        }
        return count == 0 ? 0 : Observe(n, new ReadOnlySpan<byte>(buffer, offset, Math.Max(n, 0)));
    }

    public override int Read(Span<byte> buffer)
    {
        int n;
        try
        {
            n = _inner.Read(buffer);
        }
        catch
        {
            _capture.Abandoned();
            throw;
        }
        return buffer.Length == 0 ? 0 : Observe(n, buffer);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        int n;
        try
        {
            n = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _capture.Abandoned();
            throw;
        }
        return buffer.Length == 0 ? 0 : Observe(n, buffer.Span);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            if (!_capture.IsFinished && !_capture.IsStreaming)
            {
                // Read the rest in the background, then release the connection.
                _ = Task.Run(DrainAndDisposeAsync);
                base.Dispose(disposing);
                return;
            }
            if (!_capture.IsFinished) _capture.Abandoned();
            _inner.Dispose();
        }
        base.Dispose(disposing);
    }

    private async Task DrainAndDisposeAsync()
    {
        var scratch = new byte[8192];
        long drained = 0;
        try
        {
            while (true)
            {
                var n = await _inner.ReadAsync(scratch).ConfigureAwait(false);
                if (n <= 0)
                {
                    _capture.Complete();
                    return;
                }
                _capture.OnBytes(scratch.AsSpan(0, n));
                drained += n;
                if (drained > CappedBuffer.MaxBytes)
                {
                    _capture.Abandoned();
                    return;
                }
            }
        }
        catch
        {
            _capture.Abandoned();
        }
        finally
        {
            try
            {
                _inner.Dispose();
            }
            catch
            {
                // nothing left to do
            }
        }
    }

    public override async ValueTask DisposeAsync()
    {
        Dispose(true);
        await base.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }
}

/// <summary>Request content that copies what is sent into a <see cref="CaptureSession"/>.</summary>
internal sealed class CapturingRequestContent : HttpContent
{
    private readonly HttpContent _inner;
    private readonly CaptureSession _session;

    public CapturingRequestContent(HttpContent inner, CaptureSession session)
    {
        _inner = inner;
        _session = session;
        foreach (var header in inner.Headers)
        {
            if (string.Equals(header.Key, "Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
            Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
    }

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        await SerializeToStreamAsync(stream, context, CancellationToken.None).ConfigureAwait(false);

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
    {
        await using var tee = new TeeWriteStream(stream, _session);
        await _inner.CopyToAsync(tee, context, cancellationToken).ConfigureAwait(false);
    }

    protected override void SerializeToStream(Stream stream, TransportContext? context, CancellationToken cancellationToken)
    {
        using var tee = new TeeWriteStream(stream, _session);
        _inner.CopyTo(tee, context, cancellationToken);
    }

    protected override bool TryComputeLength(out long length)
    {
        var known = _inner.Headers.ContentLength;
        length = known ?? 0;
        return known is not null;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }

    private sealed class TeeWriteStream : Stream
    {
        private readonly Stream _target;
        private readonly CaptureSession _session;

        public TeeWriteStream(Stream target, CaptureSession session)
        {
            _target = target;
            _session = session;
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => _target.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) => _target.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        private void Capture(ReadOnlySpan<byte> data)
        {
            try
            {
                _session.RequestBody(data);
            }
            catch
            {
                // capture must never break the host's request
            }
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            Capture(new ReadOnlySpan<byte>(buffer, offset, count));
            _target.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Capture(buffer);
            _target.Write(buffer);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Capture(buffer.Span);
            return _target.WriteAsync(buffer, cancellationToken);
        }

        // The transport owns the target stream; never dispose it from here.
        protected override void Dispose(bool disposing) => base.Dispose(disposing);
    }
}

/// <summary>Response content whose body is copied into a <see cref="ResponseCapture"/> as the host reads it.</summary>
internal sealed class CapturingResponseContent : HttpContent
{
    private readonly HttpContent _inner;
    private readonly ResponseCapture _capture;
    private int _streamCreated;

    public CapturingResponseContent(HttpContent inner, ResponseCapture capture)
    {
        _inner = inner;
        _capture = capture;
        foreach (var header in inner.Headers) Headers.TryAddWithoutValidation(header.Key, header.Value);
    }

    private async Task<Stream> OpenAsync(CancellationToken ct)
    {
        Interlocked.Exchange(ref _streamCreated, 1);
        Stream inner;
        try
        {
            inner = await _inner.ReadAsStreamAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            _capture.Abandoned();
            throw;
        }
        return new CapturingReadStream(inner, _capture);
    }

    protected override Task<Stream> CreateContentReadStreamAsync() => OpenAsync(CancellationToken.None);

    protected override Stream CreateContentReadStream(CancellationToken cancellationToken)
    {
        Interlocked.Exchange(ref _streamCreated, 1);
        Stream inner;
        try
        {
            inner = _inner.ReadAsStream(cancellationToken);
        }
        catch
        {
            _capture.Abandoned();
            throw;
        }
        return new CapturingReadStream(inner, _capture);
    }

    protected override void SerializeToStream(Stream stream, TransportContext? context, CancellationToken cancellationToken)
    {
        using var source = CreateContentReadStream(cancellationToken);
        source.CopyTo(stream);
    }

    protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) => OpenAsync(cancellationToken);

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        SerializeToStreamAsync(stream, context, CancellationToken.None);

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
    {
        var source = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (source.ConfigureAwait(false))
        {
            await source.CopyToAsync(stream, cancellationToken).ConfigureAwait(false);
        }
    }

    protected override bool TryComputeLength(out long length)
    {
        var known = _inner.Headers.ContentLength;
        length = known ?? 0;
        return known is not null;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _streamCreated, 1) == 0 && !_capture.IsFinished)
        {
            // The host never read the body: read it ourselves (non-streamed) or log it as abandoned.
            if (_capture.IsStreaming)
            {
                _capture.Abandoned();
                _inner.Dispose();
            }
            else
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var stream = await OpenAsync(CancellationToken.None).ConfigureAwait(false);
                        await stream.DisposeAsync().ConfigureAwait(false);
                    }
                    catch
                    {
                        _capture.Abandoned();
                        _inner.Dispose();
                    }
                });
            }
        }
        else if (disposing && _capture.IsFinished)
        {
            _inner.Dispose();
        }
        base.Dispose(disposing);
    }
}
