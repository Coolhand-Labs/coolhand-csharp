using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace CoolhandLabs.Tests.Support;

/// <summary>One request a fake handler received.</summary>
public sealed record RecordedRequest(HttpMethod Method, Uri Uri, Dictionary<string, string> Headers, string? Body)
{
    public JsonNode Json => JsonNode.Parse(Body ?? "null")!;

    public string? Header(string name) => Headers.TryGetValue(name.ToLowerInvariant(), out var v) ? v : null;
}

/// <summary>
/// An in-process stand-in for an HTTP server: records every request (reading its body as the
/// transport would) and answers with <see cref="Responder"/>. Nothing touches the network.
/// </summary>
public sealed class FakeHandler : HttpMessageHandler
{
    private readonly ConcurrentQueue<RecordedRequest> _requests = new();

    public Func<HttpRequestMessage, string?, HttpResponseMessage> Responder { get; set; } = (_, _) => Json(200, "{}");

    public IReadOnlyList<RecordedRequest> Requests => _requests.ToList();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string? body = null;
        if (request.Content is not null)
        {
            // Serialize the content the way the transport does (CopyToAsync), so tee'ing contents see it.
            using var buffer = new MemoryStream();
            await request.Content.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            body = Encoding.UTF8.GetString(buffer.ToArray());
        }
        var headers = new Dictionary<string, string>();
        foreach (var h in request.Headers.Concat(request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>()))
        {
            headers[h.Key.ToLowerInvariant()] = string.Join(", ", h.Value);
        }
        _requests.Enqueue(new RecordedRequest(request.Method, request.RequestUri!, headers, body));
        var response = Responder(request, body);
        response.RequestMessage ??= request;
        return response;
    }

    public static HttpResponseMessage Json(int status, string json, params (string Name, string Value)[] headers)
    {
        var response = new HttpResponseMessage((HttpStatusCode)status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        foreach (var (name, value) in headers)
        {
            if (!response.Headers.TryAddWithoutValidation(name, value)) response.Content.Headers.TryAddWithoutValidation(name, value);
        }
        return response;
    }

    public static HttpResponseMessage Bytes(int status, byte[] body, string contentType, params (string Name, string Value)[] headers)
    {
        var content = new ByteArrayContent(body);
        content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        var response = new HttpResponseMessage((HttpStatusCode)status) { Content = content };
        foreach (var (name, value) in headers)
        {
            if (!response.Headers.TryAddWithoutValidation(name, value)) content.Headers.TryAddWithoutValidation(name, value);
        }
        return response;
    }

    public static HttpResponseMessage Stream(int status, Stream body, string contentType)
    {
        var content = new StreamContent(body);
        content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        return new HttpResponseMessage((HttpStatusCode)status) { Content = content };
    }
}

/// <summary>A fake Coolhand API plus a client pointed at it.</summary>
public sealed class FakeCoolhand : IAsyncDisposable
{
    public const string BaseUrl = "https://coolhand.test";

    public FakeHandler Api { get; } = new();

    public Coolhand Client { get; }

    public FakeCoolhand(Func<CoolhandOptions, CoolhandOptions>? configure = null)
    {
        var options = new CoolhandOptions { ApiKey = "ch_test_key", BaseUrl = BaseUrl, HttpMessageHandler = Api };
        Client = new Coolhand(configure is null ? options : configure(options));
    }

    /// <summary>The <c>llm_request_log</c> objects posted so far.</summary>
    public List<JsonObject> Logs => Api.Requests.Where(r => r.Uri.AbsolutePath == "/api/v2/llm_request_logs" && r.Method == HttpMethod.Post)
        .Select(r => (JsonObject)r.Json["llm_request_log"]!).ToList();

    /// <summary>Waits until <paramref name="count"/> logs were posted (captures are submitted in the background).</summary>
    public async Task<List<JsonObject>> WaitForLogsAsync(int count, int timeoutMs = 10_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            await Client.FlushAsync(TimeSpan.FromSeconds(5));
            var logs = Logs;
            if (logs.Count >= count) return logs;
            await Task.Delay(10);
        }
        throw new TimeoutException("expected " + count + " logs, got " + Logs.Count);
    }

    /// <summary>An HttpClient monitored by this client, sending to <paramref name="provider"/>.</summary>
    public HttpClient Http(FakeHandler provider) => Client.CreateHttpClient(provider);

    public async ValueTask DisposeAsync() => await Client.DisposeAsync();
}

/// <summary>A stream that hands out chunks with pauses, and can fail partway.</summary>
public sealed class ChunkedStream : Stream
{
    private readonly Queue<byte[]> _chunks;
    private readonly Exception? _failAfter;
    private byte[]? _current;
    private int _offset;

    public ChunkedStream(IEnumerable<string> chunks, Exception? failAfter = null)
    {
        _chunks = new Queue<byte[]>(chunks.Select(Encoding.UTF8.GetBytes));
        _failAfter = failAfter;
    }

    public bool Disposed { get; private set; }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_current is null || _offset >= _current.Length)
        {
            if (_chunks.Count == 0)
            {
                if (_failAfter is not null) throw _failAfter;
                return 0;
            }
            _current = _chunks.Dequeue();
            _offset = 0;
        }
        var n = Math.Min(count, _current.Length - _offset);
        Array.Copy(_current, _offset, buffer, offset, n);
        _offset += n;
        return n;
    }

    protected override void Dispose(bool disposing)
    {
        Disposed = true;
        base.Dispose(disposing);
    }
}

/// <summary>Captures what the SDK writes to <see cref="CoolhandLog"/> during a test.</summary>
public sealed class LogCapture : IDisposable
{
    private readonly Action<CoolhandLogLevel, string>? _previous;
    private readonly ConcurrentQueue<(CoolhandLogLevel Level, string Message)> _messages = new();

    public LogCapture()
    {
        _previous = CoolhandLog.Sink;
        CoolhandLog.Sink = (level, message) => _messages.Enqueue((level, message));
    }

    public IReadOnlyList<(CoolhandLogLevel Level, string Message)> Messages => _messages.ToList();

    public string All => string.Join("\n", _messages.Select(m => m.Message));

    public void Dispose() => CoolhandLog.Sink = _previous;
}

/// <summary>Serializes tests that touch process-wide state (the global client, environment variables, the log sink).</summary>
[CollectionDefinition("global", DisableParallelization = true)]
public sealed class GlobalStateCollection
{
}
