using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CoolhandLabs.Internal;
using CoolhandLabs.Models;

namespace CoolhandLabs.Services;

/// <summary>Settings shared by every service (coolhand-node's <c>BaseServiceConfig</c>).</summary>
public sealed record ServiceConfig
{
    /// <summary>The Coolhand API key, sent as <c>X-API-Key</c>.</summary>
    public string? ApiKey { get; init; }

    /// <summary>Suppress progress logging (default true).</summary>
    public bool Silent { get; init; } = true;

    /// <summary>Verbose logging; never suppresses submissions.</summary>
    public bool Debug { get; init; }

    /// <summary>Skip every write to the Coolhand API (reads still run).</summary>
    public bool DryRun { get; init; }

    /// <summary>The API origin, default <c>https://coolhandlabs.com</c>.</summary>
    public string? BaseUrl { get; init; }

    /// <summary>Per-call timeout, default 30 seconds (uploads use 10 minutes).</summary>
    public TimeSpan? RequestTimeout { get; init; }

    /// <summary>The handler used to reach Coolhand; null for a shared default that never follows redirects.</summary>
    public HttpMessageHandler? HttpMessageHandler { get; init; }

    internal static ServiceConfig From(CoolhandOptions options) => new()
    {
        ApiKey = options.ApiKey,
        Silent = options.Silent,
        Debug = options.Debug,
        DryRun = options.DryRun,
        BaseUrl = options.BaseUrl,
        RequestTimeout = options.RequestTimeout,
        HttpMessageHandler = options.HttpMessageHandler,
    };
}

/// <summary>
/// Request building, authentication, error shape and JSON helpers shared by every service
/// (coolhand-node's <c>BaseService</c>).
/// </summary>
/// <remarks>
/// Two conventions, as in coolhand-node: <b>writes</b> that submit data (feedback, log submission,
/// uploads) never throw for API or network failures; they log and return null. <b>Reads</b> and admin
/// writes throw <see cref="CoolhandException"/> on network failure or a non-JSON body and
/// <see cref="CoolhandHttpException"/> (with <see cref="CoolhandHttpException.StatusCode"/>) on a non-2xx
/// response. Invalid arguments throw <see cref="ArgumentException"/> before any request is made.
/// Cancelling a caller-supplied <see cref="CancellationToken"/> always throws
/// <see cref="OperationCanceledException"/>.
/// </remarks>
public abstract class BaseService
{
    internal static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromMilliseconds(30_000);
    internal const long MaxRequestTimeoutMs = 2_147_483_647L;
    internal static readonly TimeSpan UploadRequestTimeout = TimeSpan.FromMinutes(10);
    internal const string DefaultBaseUrl = "https://coolhandlabs.com";

    private static readonly Lazy<HttpClient> SharedClient = new(() => new HttpClient(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectTimeout = TimeSpan.FromSeconds(30),
    })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    });

    /// <summary>The normalized API key ("" for an anonymous service).</summary>
    internal string ApiKey { get; }

    /// <summary>Whether progress logging is suppressed.</summary>
    internal bool Silent { get; }

    /// <summary>Whether verbose logging is on.</summary>
    internal bool Debug { get; }

    /// <summary>Whether writes are skipped.</summary>
    internal bool DryRun { get; }

    /// <summary>The per-call timeout.</summary>
    internal TimeSpan RequestTimeout { get; }

    internal Log Log { get; }

    private readonly HttpClient _http;

    /// <summary>The full endpoint this service talks to.</summary>
    public string ApiEndpoint { get; }

    /// <param name="config">Shared settings.</param>
    /// <param name="endpointPath">The path appended to the base URL, e.g. <c>/api/v2/llm_request_logs</c>.</param>
    /// <param name="allowAnonymous">Let a missing or blank key through as "no key" (only for public endpoints);
    /// no <c>X-API-Key</c> header is sent then.</param>
    /// <exception cref="ArgumentException">For a missing or malformed key, an invalid base URL or an out-of-range timeout.</exception>
    private protected BaseService(ServiceConfig config, string endpointPath, bool allowAnonymous = false)
    {
        ArgumentNullException.ThrowIfNull(config);
        ApiKey = NormalizeApiKey(config.ApiKey, allowAnonymous);
        Silent = config.Silent;
        Debug = config.Debug;
        DryRun = config.DryRun;
        Log = new Log(Silent);
        var timeout = config.RequestTimeout ?? DefaultRequestTimeout;
        if (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > MaxRequestTimeoutMs || timeout.Ticks % TimeSpan.TicksPerMillisecond != 0)
        {
            throw new ArgumentException("requestTimeoutMs must be a positive integer number of milliseconds (max "
                                        + MaxRequestTimeoutMs + "). Got: " + timeout.TotalMilliseconds.ToString(CultureInfo.InvariantCulture));
        }
        RequestTimeout = timeout;
        var rawBase = config.BaseUrl ?? DefaultBaseUrl;
        ValidateBaseUrl(rawBase);
        ApiEndpoint = rawBase.TrimEnd('/') + endpointPath;
        _http = config.HttpMessageHandler is { } handler
            ? new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan }
            : SharedClient.Value;
    }

    // ------------------------------------------------------------------ validation

    private static readonly Regex UrlUserinfo = new(@"^(\s*(?:[a-z][a-z0-9+.-]*://)?)[^/\s]*@", RegexOptions.IgnoreCase);
    private static readonly Regex HeaderSafe = new(@"^[\t\x20-\x7e\x80-\xff]*$");
    private static readonly Regex HttpWsEdges = new(@"^[\t\n\r ]+|[\t\n\r ]+$");
    private static readonly Regex MissingHost = new(@"^\s*https?://(?:[^/?#]*@)?(?:[/?#].*)?$", RegexOptions.IgnoreCase | RegexOptions.Singleline);

    /// <summary>The base URL as it may appear in a message: query/fragment dropped, userinfo masked.</summary>
    internal static string RedactForMessage(string raw)
    {
        var cut = raw.IndexOfAny(new[] { '?', '#' });
        var withoutQuery = cut == -1 ? raw : raw.Substring(0, cut) + "…";
        return UrlUserinfo.Replace(withoutQuery, "$1***@", 1);
    }

    internal static void ValidateBaseUrl(string raw)
    {
        var shown = RedactForMessage(raw);
        var url = UrlParts.Parse(raw);
        if (url is null)
        {
            if (MissingHost.IsMatch(raw)) throw new ArgumentException("baseUrl must include a hostname. Got: \"" + shown + "\"");
            throw new ArgumentException("Invalid baseUrl: \"" + shown + "\" is not a valid URL");
        }
        if (url.HasCredentials)
        {
            throw new ArgumentException("baseUrl must not include credentials (user:password@). Use the apiKey option instead.");
        }
        if (url.Query is not null || url.Fragment is not null || raw.Contains('?') || raw.Contains('#'))
        {
            throw new ArgumentException("baseUrl must not include a query string or fragment. Got: \"" + shown + "\"");
        }
        if (url.Scheme == "https") return;
        if (url.Scheme == "http" && url.Hostname is "localhost" or "127.0.0.1" or "[::1]") return;
        throw new ArgumentException("baseUrl must use https:// (got: \"" + shown + "\"). For local dev, http://localhost is allowed.");
    }

    internal static string NormalizeApiKey(string? raw, bool allowAnonymous)
    {
        if (raw is null || JsText.Trim(raw).Length == 0)
        {
            if (allowAnonymous) return "";
            throw new ArgumentException("apiKey must be a non-empty string");
        }
        var key = HttpWsEdges.Replace(raw, "");
        if (!HeaderSafe.IsMatch(key))
        {
            throw new ArgumentException("apiKey contains characters that are not valid in an HTTP header (e.g. a "
                                        + "newline or control character). Check how the key is loaded (.env parsing, stray whitespace).");
        }
        return key;
    }

    // ------------------------------------------------------------------ masking

    /// <summary><paramref name="text"/> with every occurrence of the API key masked.</summary>
    internal string Mask(string text)
    {
        if (ApiKey.Length == 0) return text;
        return text.Replace(ApiKey, "[redacted]", StringComparison.Ordinal);
    }

    /// <summary>First 2000 characters of a response body for an error message, with the key masked first.</summary>
    internal string Excerpt(string? text)
    {
        var masked = Mask(text ?? "");
        return masked.Length > 2000 ? masked.Substring(0, 2000) : masked;
    }

    internal static string FormatErrorMessage(Exception? error)
    {
        if (error is null) return "null";
        return string.IsNullOrEmpty(error.Message) ? error.GetType().Name : error.Message;
    }

    /// <summary>An error's message with the key masked.</summary>
    internal string DescribeError(Exception? error) => Mask(FormatErrorMessage(error));

    private bool LeaksKey(Exception? error)
    {
        if (ApiKey.Length == 0) return false;
        var current = error;
        for (var depth = 0; current is not null && depth <= 5; depth++, current = current.InnerException)
        {
            if (current.Message.Contains(ApiKey, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private CoolhandException ScrubbedError(string errorPrefix, Exception error)
    {
        var message = errorPrefix + ": " + DescribeError(error);
        return LeaksKey(error) ? new CoolhandException(message) : new CoolhandException(message, error);
    }

    // ------------------------------------------------------------------ requests

    /// <summary>A request with the User-Agent and (unless anonymous) the <c>X-API-Key</c> header.</summary>
    internal HttpRequestMessage NewRequest(HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.TryAddWithoutValidation("User-Agent", CoolhandVersion.UserAgent);
        if (ApiKey.Length > 0) request.Headers.TryAddWithoutValidation("X-API-Key", ApiKey);
        return request;
    }

    /// <summary>A UTF-8 JSON body.</summary>
    internal static HttpContent JsonBody(JsonNode payload)
    {
        var content = new ByteArrayContent(Encoding.UTF8.GetBytes(Json.Stringify(payload)));
        content.Headers.TryAddWithoutValidation("Content-Type", "application/json");
        return content;
    }

    /// <summary><paramref name="data"/> with the SDK collector string for <paramref name="method"/> appended last.</summary>
    internal static JsonObject WithCollector(JsonObject data, CollectionMethod method)
    {
        data["collector"] = Collector.GetCollectorString(method);
        return data;
    }

    private async Task<HttpResponseMessage> SendRawAsync(HttpRequestMessage request, TimeSpan timeout, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cts.Token).ConfigureAwait(false);
            return response;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("The request timed out after " + ((long)timeout.TotalMilliseconds).ToString(CultureInfo.InvariantCulture) + "ms");
        }
    }

    /// <summary>
    /// POSTs a JSON payload, resolving to the parsed JSON on 2xx and to null on any failure (logged,
    /// never thrown); skipped (null) in dry-run mode.
    /// </summary>
    internal Task<JsonNode?> SendRequestAsync(JsonNode payload, string successMessage, CancellationToken ct) =>
        SendAndParseAsync(() =>
        {
            var body = Json.Stringify(payload);
            if (Debug && !Silent)
            {
                Log.Info("[coolhand-csharp] DEBUG: Sending to " + ApiEndpoint);
                Log.Info("[coolhand-csharp] DEBUG: Payload size: " + body.Length + " bytes");
            }
            var request = NewRequest(HttpMethod.Post, ApiEndpoint);
            var content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
            content.Headers.TryAddWithoutValidation("Content-Type", "application/json");
            request.Content = content;
            return request;
        }, () =>
        {
            if (!Silent)
            {
                Log.Info("🚫 DRY RUN: Skipping API call to " + ApiEndpoint);
                try
                {
                    Log.Info("🚫 DRY RUN: Would send payload: " + Json.Pretty(payload));
                }
                catch (Exception e)
                {
                    Log.Error("❌ Failed to stringify payload for dry-run log: " + DescribeError(e));
                }
            }
        }, successMessage, RequestTimeout, ct);

    /// <summary>POSTs a multipart body; null on failure (logged), skipped in dry-run mode.</summary>
    internal Task<JsonNode?> SendMultipartAsync(byte[] body, string boundary, string successMessage, CancellationToken ct) =>
        SendAndParseAsync(() =>
        {
            if (Debug && !Silent) Log.Info("[coolhand-csharp] DEBUG: Sending multipart to " + ApiEndpoint);
            var request = NewRequest(HttpMethod.Post, ApiEndpoint);
            var content = new ByteArrayContent(body);
            content.Headers.TryAddWithoutValidation("Content-Type", "multipart/form-data; boundary=" + boundary);
            request.Content = content;
            return request;
        }, () =>
        {
            if (!Silent) Log.Info("🚫 DRY RUN: Skipping API call to " + ApiEndpoint);
        }, successMessage, UploadRequestTimeout, ct);

    private async Task<JsonNode?> SendAndParseAsync(Func<HttpRequestMessage> build, Action dryRunLog, string successMessage,
        TimeSpan timeout, CancellationToken ct)
    {
        if (DryRun)
        {
            dryRunLog();
            Log.Info("🚫 DRY RUN: " + successMessage.Replace("✅", "🚫", StringComparison.Ordinal));
            return null;
        }
        try
        {
            using var request = build();
            using var response = await SendRawAsync(request, timeout, ct).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if (status >= 200 && status < 300)
            {
                if (!Json.TryParse(text, out var parsed))
                {
                    Log.Error("❌ Request error: response was not valid JSON: " + Excerpt(text));
                    return null;
                }
                Log.Info(successMessage);
                return parsed;
            }
            Log.Error("❌ Request failed: " + status + " - " + Excerpt(text));
            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            Log.Error("❌ Request error: " + DescribeError(e));
            return null;
        }
    }

    /// <summary>A response body plus its headers and status.</summary>
    internal sealed record RawResponse(string Text, ResponseHeaders Headers, int Status);

    /// <summary>A response body as bytes plus its headers.</summary>
    internal sealed record BytesResponse(byte[] Bytes, ResponseHeaders Headers);

    /// <summary>
    /// Sends <paramref name="request"/> and returns the body, throwing <see cref="CoolhandHttpException"/>
    /// on a non-2xx response and <see cref="CoolhandException"/> on a network failure or redirect.
    /// </summary>
    internal async Task<RawResponse> FetchWithHeadersAsync(HttpRequestMessage request, string errorPrefix, CancellationToken ct)
    {
        var (bytes, headers, status) = await SendCheckedAsync(request, errorPrefix, ct).ConfigureAwait(false);
        return new RawResponse(Encoding.UTF8.GetString(bytes), headers, status);
    }

    /// <summary>Like <see cref="FetchWithHeadersAsync"/>, but returns raw bytes (for file downloads).</summary>
    internal async Task<BytesResponse> FetchBytesAsync(HttpRequestMessage request, string errorPrefix, CancellationToken ct)
    {
        var (bytes, headers, _) = await SendCheckedAsync(request, errorPrefix, ct).ConfigureAwait(false);
        return new BytesResponse(bytes, headers);
    }

    private async Task<(byte[] Bytes, ResponseHeaders Headers, int Status)> SendCheckedAsync(HttpRequestMessage request, string errorPrefix,
        CancellationToken ct)
    {
        HttpResponseMessage response;
        byte[] bytes;
        try
        {
            using (request)
            {
                response = await SendRawAsync(request, RequestTimeout, ct).ConfigureAwait(false);
            }
            bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            throw ScrubbedError(errorPrefix, e);
        }
        using (response)
        {
            var status = (int)response.StatusCode;
            if (status >= 300 && status < 400) throw new CoolhandException(errorPrefix + ": redirect (" + status + ") not followed");
            if (status < 200 || status >= 300)
            {
                throw new CoolhandHttpException(errorPrefix + " (" + status + "): " + Excerpt(Encoding.UTF8.GetString(bytes)), status);
            }
            return (bytes, ResponseHeaders.From(response), status);
        }
    }

    /// <summary>Sends and returns the body text.</summary>
    internal async Task<string> FetchOrThrowAsync(HttpRequestMessage request, string errorPrefix, CancellationToken ct) =>
        (await FetchWithHeadersAsync(request, errorPrefix, ct).ConfigureAwait(false)).Text;

    /// <summary>Parses <paramref name="text"/> as a JSON object or array, or throws with the noun's wording.</summary>
    internal JsonNode ParseJsonObject(string text, string noun)
    {
        if (!Json.TryParse(text, out var parsed)) throw new CoolhandException(noun + " response was not valid JSON: " + Excerpt(text));
        if (parsed is not (JsonObject or JsonArray)) throw new CoolhandException(noun + " response was not a JSON object: " + Excerpt(text));
        return parsed;
    }

    /// <summary>A parsed JSON body plus the response headers.</summary>
    internal sealed record JsonWithHeaders(JsonNode Body, ResponseHeaders Headers, int Status);

    /// <summary>GETs <paramref name="url"/> and parses the JSON body, keeping the headers (e.g. for pagination).</summary>
    internal async Task<JsonWithHeaders> GetJsonWithHeadersAsync(string url, string noun, CancellationToken ct)
    {
        HttpRequestMessage request;
        try
        {
            request = NewRequest(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
        }
        catch (Exception e) when (e is UriFormatException or ArgumentException or InvalidOperationException)
        {
            throw new CoolhandException(noun + " request failed: " + DescribeError(e));
        }
        var raw = await FetchWithHeadersAsync(request, noun + " request failed", ct).ConfigureAwait(false);
        return new JsonWithHeaders(ParseJsonObject(raw.Text, noun), raw.Headers, raw.Status);
    }

    /// <summary>GETs and parses a JSON body.</summary>
    internal async Task<JsonNode> GetJsonAsync(string url, string noun, CancellationToken ct) =>
        (await GetJsonWithHeadersAsync(url, noun, ct).ConfigureAwait(false)).Body;

    /// <summary>A record upserted by key, and whether the server created it (201) rather than updated it (200).</summary>
    internal sealed record Upserted(JsonNode Record, bool Created);

    /// <summary>
    /// <c>PUT &lt;endpoint&gt;/by_key/&lt;key&gt;</c>: each <c>/</c>-separated segment percent-encoded on its
    /// own. Validates the key first (so a dry run rejects what a live run would), then resolves to null on a dry run.
    /// </summary>
    internal Task<Upserted?> PutByKeyAsync(string? key, JsonObject body, string noun, CancellationToken ct)
    {
        var url = ByKeyUrl(key, noun);
        if (DryRun)
        {
            Log.Info("🚫 DRY RUN: Skipping " + noun.ToLowerInvariant() + " upsert for key \"" + key + "\"");
            return Task.FromResult<Upserted?>(null);
        }
        return SendPutAsync(url, body, noun, ct);
    }

    private async Task<Upserted?> SendPutAsync(string url, JsonObject body, string noun, CancellationToken ct)
    {
        var request = NewRequest(HttpMethod.Put, url);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        request.Content = JsonBody(body);
        var raw = await FetchWithHeadersAsync(request, noun + " request failed", ct).ConfigureAwait(false);
        return new Upserted(ParseJsonObject(raw.Text, noun), raw.Status == 201);
    }

    internal string ByKeyUrl(string? key, string noun)
    {
        if (key is null) throw new ArgumentException(noun + " upsert: key must be a non-empty string");
        var problem = EntityKeys.KeyProblem(key);
        if (problem is not null) throw new ArgumentException(noun + " upsert: key " + problem + " (got: \"" + key + "\")");
        var encoded = new StringBuilder();
        foreach (var segment in key.Split('/'))
        {
            if (segment is "." or "..")
            {
                throw new ArgumentException(noun + " upsert: key must not contain \".\" or \"..\" path segments (got: \"" + key + "\")");
            }
            if (encoded.Length > 0) encoded.Append('/');
            encoded.Append(JsText.EncodeUriComponent(segment));
        }
        return ApiEndpoint + "/by_key/" + encoded;
    }

    /// <summary>
    /// <c>&lt;endpoint&gt;/&lt;encoded id&gt;</c>, rejecting ids URL resolution would retarget: blank strings
    /// and the dot-segments <c>.</c> and <c>..</c>.
    /// </summary>
    internal string BuildResourceUrl(string? id, string errorMessage)
    {
        if (id is null || JsText.Trim(id).Length == 0 || id is "." or "..") throw new ArgumentException(errorMessage);
        return ApiEndpoint + "/" + JsText.EncodeUriComponent(id);
    }

    // ------------------------------------------------------------------ pagination

    private static long ParseHeaderInt(string? value, long fallback)
    {
        if (value is null) return fallback;
        var trimmed = JsText.Trim(value);
        if (trimmed.Length == 0 || !trimmed.All(char.IsAsciiDigit)) return fallback;
        return long.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : fallback;
    }

    /// <summary>
    /// Pagination from <c>X-Page</c>/<c>X-Per-Page</c>/<c>X-Total-Count</c>/<c>X-Total-Pages</c>,
    /// falling back to a lower-bound estimate from the page actually returned when totals are absent.
    /// </summary>
    internal static Pagination PaginationFromHeaders(ResponseHeaders headers, int itemCount, int? page, int? per)
    {
        const int defaultPerPage = 25;
        const int maxPerPage = 100;
        var requestedPage = Math.Max(1, page ?? 1);
        var truncatedPer = per ?? 0;
        var requestedPer = truncatedPer > 0 ? Math.Min(truncatedPer, maxPerPage) : defaultPerPage;
        var hasTotalCount = headers.Get("x-total-count") is not null;

        var currentPage = Math.Max(1, ParseHeaderInt(headers.Get("x-page"), requestedPage));
        var perPage = ParseHeaderInt(headers.Get("x-per-page"), requestedPer);
        long totalCount, totalPages;
        bool hasNextPage, hasPrevPage;
        if (hasTotalCount)
        {
            totalCount = ParseHeaderInt(headers.Get("x-total-count"), itemCount);
            totalPages = ParseHeaderInt(headers.Get("x-total-pages"),
                perPage > 0 ? (totalCount + perPage - 1) / perPage : (totalCount > 0 ? 1 : 0));
            hasNextPage = currentPage < totalPages;
            hasPrevPage = currentPage > 1;
        }
        else if (itemCount > 0)
        {
            totalCount = perPage > 0 ? (currentPage - 1) * perPage + itemCount : itemCount;
            totalPages = perPage > 0 ? (totalCount + perPage - 1) / perPage : currentPage;
            hasNextPage = perPage > 0 && itemCount >= perPage;
            hasPrevPage = currentPage > 1;
        }
        else
        {
            totalCount = 0;
            totalPages = 0;
            hasNextPage = false;
            hasPrevPage = currentPage > 1;
        }
        return new Pagination((int)Math.Min(currentPage, int.MaxValue), (int)Math.Min(perPage, int.MaxValue),
            totalCount, totalPages, hasNextPage, hasPrevPage);
    }

    /// <summary>The elements of a bare JSON array response.</summary>
    internal static IReadOnlyList<JsonNode> Items(JsonNode body, string noun)
    {
        if (body is not JsonArray array) throw new CoolhandException(noun + " response was not a JSON array");
        return array.Where(item => item is not null).Select(item => item!.DeepClone()).ToList();
    }

    /// <summary>GETs a bare-array list endpoint and reads its pagination headers.</summary>
    internal async Task<Page<JsonNode>> ListPageAsync(string url, string noun, int? page, int? per, CancellationToken ct)
    {
        var r = await GetJsonWithHeadersAsync(url, noun, ct).ConfigureAwait(false);
        var items = Items(r.Body, noun);
        return new Page<JsonNode>(items, PaginationFromHeaders(r.Headers, items.Count, page, per));
    }

    /// <summary>Logs a separator line (when not silent).</summary>
    internal void LogSeparator() => Log.Info(new string('═', 60));

    /// <summary>Serialises a <c>since</c>/<c>until</c> bound: a <see cref="DateTimeOffset"/> as ISO-8601 UTC with millis.</summary>
    internal static string? ToTimestampParam(DateTimeOffset? value) => value is null ? null : JsText.IsoTimestamp(value.Value);
}

/// <summary>Response headers by lowercase name, first value (coolhand-node's <c>Headers.get</c>).</summary>
public sealed class ResponseHeaders
{
    private readonly Dictionary<string, string> _values;

    /// <summary>Creates headers from name/value pairs.</summary>
    public ResponseHeaders(IEnumerable<KeyValuePair<string, string>> values)
    {
        _values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in values) _values.TryAdd(pair.Key, pair.Value);
    }

    /// <summary>A header's value (multiple values joined with ", "), or null.</summary>
    public string? Get(string name) => _values.TryGetValue(name, out var v) ? v : null;

    internal static ResponseHeaders From(HttpResponseMessage response)
    {
        var pairs = response.Headers.Concat(response.Content.Headers)
            .Select(h => new KeyValuePair<string, string>(h.Key, string.Join(", ", h.Value)));
        return new ResponseHeaders(pairs);
    }
}

/// <summary>Query-string builder with <c>URLSearchParams.set</c> semantics: null skipped, last set wins.</summary>
internal sealed class Query
{
    private readonly List<KeyValuePair<string, string>> _params = new();

    public Query Set(string name, object? value)
    {
        if (value is null) return this;
        var text = JsText.JsString(value);
        var index = _params.FindIndex(p => p.Key == name);
        if (index >= 0) _params[index] = new KeyValuePair<string, string>(name, text);
        else _params.Add(new KeyValuePair<string, string>(name, text));
        return this;
    }

    public string AppendTo(string url)
    {
        if (_params.Count == 0) return url;
        return url + "?" + string.Join("&", _params.Select(p => JsText.FormEncode(p.Key) + "=" + JsText.FormEncode(p.Value)));
    }
}
