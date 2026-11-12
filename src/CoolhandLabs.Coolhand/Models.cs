using System.Reflection;
using System.Text.Json.Nodes;
using CoolhandLabs.Internal;

namespace CoolhandLabs;

/// <summary>
/// This SDK's name and version. The version comes from the assembly (set once, in
/// <c>Directory.Build.props</c>) and drives the collector string and the User-Agent.
/// </summary>
public static class CoolhandVersion
{
    /// <summary><c>coolhand-csharp</c>.</summary>
    public const string PackageName = "coolhand-csharp";

    /// <summary>The released version, e.g. <c>0.1.0</c>.</summary>
    public static string PackageVersion { get; } = ReadVersion();

    /// <summary><c>coolhand-csharp-&lt;version&gt;</c>.</summary>
    public static string PackageIdentifier { get; } = PackageName + "-" + PackageVersion;

    /// <summary>The <c>User-Agent</c> sent on every call to the Coolhand API: <c>coolhand-csharp/&lt;version&gt;</c>.</summary>
    public static string UserAgent => PackageName + "/" + PackageVersion;

    private static string ReadVersion()
    {
        var info = typeof(CoolhandVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrEmpty(info)) return "unknown";
        var plus = info.IndexOf('+');
        return plus >= 0 ? info.Substring(0, plus) : info;
    }
}

/// <summary>How a log or feedback reached Coolhand; the suffix of the collector string.</summary>
public enum CollectionMethod
{
    /// <summary>Captured by an interception integration (<c>global-monitoring</c>).</summary>
    GlobalMonitoring,
    /// <summary>Submitted through an explicit API call (<c>manual</c>).</summary>
    Manual,
    /// <summary>Captured by monitoring started from environment variables alone (<c>auto-monitor</c>).</summary>
    AutoMonitor,
}

/// <summary>Collector strings (coolhand-node's <c>getCollectorString</c>).</summary>
public static class Collector
{
    /// <summary>The wire name of <paramref name="method"/>.</summary>
    public static string WireName(this CollectionMethod method) => method switch
    {
        CollectionMethod.GlobalMonitoring => "global-monitoring",
        CollectionMethod.Manual => "manual",
        CollectionMethod.AutoMonitor => "auto-monitor",
        _ => throw new ArgumentOutOfRangeException(nameof(method)),
    };

    /// <summary>
    /// <c>coolhand-csharp-&lt;version&gt;-&lt;method&gt;</c>, or <c>coolhand-csharp-&lt;version&gt;</c> when
    /// <paramref name="method"/> is null.
    /// </summary>
    public static string GetCollectorString(CollectionMethod? method = null) =>
        method is null ? CoolhandVersion.PackageIdentifier : CoolhandVersion.PackageIdentifier + "-" + method.Value.WireName();
}

/// <summary>Feedback sentiment, sent as <c>like</c>, <c>dislike</c> or <c>neutral</c>.</summary>
public enum Sentiment
{
    /// <summary><c>like</c></summary>
    Like,
    /// <summary><c>dislike</c></summary>
    Dislike,
    /// <summary><c>neutral</c></summary>
    Neutral,
}

/// <summary>Who supplied a piece of feedback; the server defaults to <c>unknown</c> when omitted.</summary>
public enum CreatorType
{
    /// <summary><c>human</c></summary>
    Human,
    /// <summary><c>agent</c></summary>
    Agent,
    /// <summary><c>unknown</c></summary>
    Unknown,
}

internal static class WireEnums
{
    public static string Wire(this Sentiment s) => s.ToString().ToLowerInvariant();

    public static string Wire(this CreatorType c) => c.ToString().ToLowerInvariant();

    public static Sentiment? ParseSentiment(string? value) => value switch
    {
        "like" => Sentiment.Like,
        "dislike" => Sentiment.Dislike,
        "neutral" => Sentiment.Neutral,
        _ => null,
    };

    public static CreatorType? ParseCreatorType(string? value) => value switch
    {
        "human" => CreatorType.Human,
        "agent" => CreatorType.Agent,
        "unknown" => CreatorType.Unknown,
        _ => null,
    };

    public static IReadOnlyList<string> Warnings(JsonNode? node)
    {
        var output = new List<string>();
        if (node is JsonObject obj && obj["warnings"] is JsonArray warnings)
        {
            foreach (var item in warnings)
            {
                output.Add(item is JsonObject w && w.ContainsKey("message")
                    ? Json.Text(w, "message") ?? ""
                    : item is JsonValue v ? Json.ScalarText(v) ?? "" : Json.Stringify(item));
            }
        }
        return output;
    }
}

/// <summary>
/// Feedback on an LLM output, the body of <see cref="Coolhand.CreateFeedbackAsync"/>. Every field is
/// optional; give at least one way to match the log: <see cref="LlmRequestLogId"/>,
/// <see cref="LlmProviderUniqueId"/> (e.g. the provider's request or response id),
/// <see cref="OriginalOutput"/> (fuzzy match) or <see cref="ClientUniqueId"/>. See <c>docs/feedback.md</c>.
/// </summary>
public sealed record LlmRequestLogFeedback
{
    /// <summary>The log's id: a hashid string (a raw integer id is also accepted, as a string of digits or via <see cref="LlmRequestLogNumericId"/>).</summary>
    public string? LlmRequestLogId { get; init; }

    /// <summary>A raw integer log id (legacy); used when <see cref="LlmRequestLogId"/> is null.</summary>
    public long? LlmRequestLogNumericId { get; init; }

    /// <summary>Deprecated boolean form; converted to <see cref="Sentiment"/> when that is absent.</summary>
    [Obsolete("Use Sentiment instead.")]
    public bool? Like { get; init; }

    /// <summary>Like, dislike or neutral. Takes precedence over <c>Like</c>.</summary>
    public Sentiment? Sentiment { get; init; }

    /// <summary>Human, agent or unknown.</summary>
    public CreatorType? CreatorType { get; init; }

    /// <summary>Your id for whoever gave the feedback.</summary>
    public string? CreatorUniqueId { get; init; }

    /// <summary>The workload this feedback belongs to.</summary>
    public string? WorkloadHashid { get; init; }

    /// <summary>Why the output was good or bad.</summary>
    public string? Explanation { get; init; }

    /// <summary>A corrected output (the strongest signal).</summary>
    public string? RevisedOutput { get; init; }

    /// <summary>The provider's id for the call (e.g. Anthropic's <c>request-id</c> or <c>msg_...</c>, OpenAI's <c>x-request-id</c>).</summary>
    public string? LlmProviderUniqueId { get; init; }

    /// <summary>The output being rated, for fuzzy matching.</summary>
    public string? OriginalOutput { get; init; }

    /// <summary>Your own id for the request.</summary>
    public string? ClientUniqueId { get; init; }
}

/// <summary>The feedback record the server created. Absent fields are null; <see cref="Raw"/> holds the full response.</summary>
/// <param name="Id">Feedback hashid.</param>
/// <param name="LlmRequestLogId">Hashid of the matched log, or null when it matched none.</param>
/// <param name="Sentiment">Like, dislike or neutral.</param>
/// <param name="CreatorType">Human, agent or unknown.</param>
/// <param name="CreatorUniqueId">As sent.</param>
/// <param name="WorkloadId">Hashid of the associated workload.</param>
/// <param name="Explanation">As sent.</param>
/// <param name="RevisedOutput">As sent.</param>
/// <param name="LlmProviderUniqueId">As sent.</param>
/// <param name="OriginalOutput">As sent.</param>
/// <param name="ClientUniqueId">As sent.</param>
/// <param name="ClientId">Hashid of the owning client.</param>
/// <param name="CollectorName">The collector string recorded.</param>
/// <param name="CoolhandFingerprintId">Fingerprint id.</param>
/// <param name="Warnings">Validation issues reported while creating the record.</param>
/// <param name="CreatedPartialId">Hashid of a partial created with sectional highlighting.</param>
/// <param name="CreatedAt">Creation time.</param>
/// <param name="UpdatedAt">Update time.</param>
/// <param name="Raw">The full JSON response.</param>
public sealed record FeedbackResponse(
    string? Id,
    string? LlmRequestLogId,
    Sentiment? Sentiment,
    CreatorType? CreatorType,
    string? CreatorUniqueId,
    string? WorkloadId,
    string? Explanation,
    string? RevisedOutput,
    string? LlmProviderUniqueId,
    string? OriginalOutput,
    string? ClientUniqueId,
    string? ClientId,
    string? CollectorName,
    string? CoolhandFingerprintId,
    IReadOnlyList<string> Warnings,
    string? CreatedPartialId,
    string? CreatedAt,
    string? UpdatedAt,
    JsonNode Raw)
{
    /// <summary>Reads a feedback record from its JSON.</summary>
    public static FeedbackResponse FromJson(JsonNode node) => new(
        Json.Text(node, "id"),
        Json.Text(node, "llm_request_log_id"),
        WireEnums.ParseSentiment(Json.Text(node, "sentiment")),
        WireEnums.ParseCreatorType(Json.Text(node, "creator_type")),
        Json.Text(node, "creator_unique_id"),
        Json.Text(node, "workload_id"),
        Json.Text(node, "explanation"),
        Json.Text(node, "revised_output"),
        Json.Text(node, "llm_provider_unique_id"),
        Json.Text(node, "original_output"),
        Json.Text(node, "client_unique_id"),
        Json.Text(node, "client_id"),
        Json.Text(node, "collector"),
        Json.Text(node, "coolhand_fingerprint_id"),
        WireEnums.Warnings(node),
        Json.Text(node, "created_partial_id"),
        Json.Text(node, "created_at"),
        Json.Text(node, "updated_at"),
        node);
}

/// <summary>The log record the server created for a submission.</summary>
/// <param name="Id">The log id, as text.</param>
/// <param name="SourceApi">Detected source API.</param>
/// <param name="SourceApiResult">Detected source API result.</param>
/// <param name="LlmProviderUniqueId">The provider's id for the response, for matching feedback.</param>
/// <param name="Warnings">Server warnings: the log was saved, but not exactly as declared.</param>
/// <param name="Metadata">Metadata as stored, or null.</param>
/// <param name="Raw">The full JSON response.</param>
public sealed record LogResponse(
    string? Id,
    string? SourceApi,
    string? SourceApiResult,
    string? LlmProviderUniqueId,
    IReadOnlyList<string> Warnings,
    JsonNode? Metadata,
    JsonNode Raw)
{
    /// <summary>Reads a log record from its JSON.</summary>
    public static LogResponse FromJson(JsonNode node) => new(
        Json.Text(node, "id"),
        Json.Text(node, "source_api"),
        Json.Text(node, "source_api_result"),
        Json.Text(node, "llm_provider_unique_id"),
        WireEnums.Warnings(node),
        node is JsonObject o && o["metadata"] is { } m ? m : null,
        node);
}

/// <summary>
/// One captured LLM request and its response: the <c>raw_request</c> of a log submission
/// (coolhand-node's <c>CoolhandCallData</c>). Headers are already redacted when the SDK builds the
/// record itself; bodies are parsed JSON or JSON strings.
/// </summary>
public sealed record CallData
{
    /// <summary>Per-client call number.</summary>
    public long Id { get; init; } = 1;

    /// <summary>When the call started, ISO-8601 UTC with milliseconds.</summary>
    public string Timestamp { get; init; } = JsText.IsoTimestamp(DateTimeOffset.UtcNow);

    /// <summary>HTTP method.</summary>
    public string Method { get; init; } = "POST";

    /// <summary>The request URL (credential query parameters and userinfo redacted when captured).</summary>
    public string? Url { get; init; }

    /// <summary>Request headers (lowercase names, credentials redacted when captured).</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();

    /// <summary>Parsed request body, or null.</summary>
    public JsonNode? RequestBody { get; init; }

    /// <summary>Parsed response body, or null.</summary>
    public JsonNode? ResponseBody { get; init; }

    /// <summary>Response headers, or null.</summary>
    public IReadOnlyDictionary<string, string>? ResponseHeaders { get; init; }

    /// <summary>HTTP status, or null.</summary>
    public int? StatusCode { get; init; }

    /// <summary><c>https</c> or <c>http</c> for intercepted calls.</summary>
    public string Protocol { get; init; } = "https";

    /// <summary>
    /// True when the response was abandoned before it ended and <see cref="ResponseBody"/> holds what was
    /// read so far. Only a true value is ever sent.
    /// </summary>
    public bool StreamIncomplete { get; init; }

    /// <summary>The wire form (<c>raw_request</c>).</summary>
    public JsonObject ToJson()
    {
        var obj = new JsonObject
        {
            ["id"] = Id,
            ["timestamp"] = Timestamp,
            ["method"] = Method,
            ["url"] = Url,
            ["headers"] = HeadersJson(Headers),
            ["request_body"] = Json.Clone(RequestBody),
            ["response_body"] = Json.Clone(ResponseBody),
            ["response_headers"] = ResponseHeaders is null ? null : HeadersJson(ResponseHeaders),
            ["status_code"] = StatusCode,
            ["protocol"] = Protocol,
        };
        if (StreamIncomplete) obj["stream_incomplete"] = true;
        return obj;
    }

    private static JsonObject HeadersJson(IReadOnlyDictionary<string, string> headers)
    {
        var obj = new JsonObject();
        foreach (var pair in headers) obj[pair.Key] = pair.Value;
        return obj;
    }
}

/// <summary>A provider pattern from <c>api-patterns.json</c> (or a custom patterns file).</summary>
/// <param name="Id">Optional id.</param>
/// <param name="Name">Display name, e.g. <c>Anthropic</c>.</param>
/// <param name="Domains">Hostnames (subdomains match too; <c>*</c> stands for one DNS label).</param>
/// <param name="Paths">Path prefixes, used with <paramref name="RequiresPathMatch"/> or <paramref name="AllowPathMatchAcrossDomains"/>.</param>
/// <param name="AllowPathMatchAcrossDomains">Let <paramref name="Paths"/> match on hosts no pattern's domains matched.</param>
/// <param name="RequiresPathMatch">A domain or port match also needs a path match.</param>
/// <param name="Ports">Ports that match on any host (with <paramref name="RequiresPathMatch"/>).</param>
/// <param name="Headers">Header name to the value it is logged as.</param>
public sealed record ApiPattern(
    string? Id,
    string? Name,
    IReadOnlyList<string> Domains,
    IReadOnlyList<string>? Paths = null,
    bool? AllowPathMatchAcrossDomains = null,
    bool? RequiresPathMatch = null,
    IReadOnlyList<int>? Ports = null,
    IReadOnlyDictionary<string, string>? Headers = null);

/// <summary>Which pattern a URL matched, and how.</summary>
/// <param name="Pattern">The pattern.</param>
/// <param name="MatchType"><c>domain</c> or <c>path</c>.</param>
/// <param name="MatchValue">The domain or path entry that matched.</param>
public sealed record MatchedPattern(ApiPattern Pattern, string MatchType, string MatchValue);

/// <summary>Monitoring counters for one client.</summary>
/// <param name="TotalRequests">Requests seen by the client's handlers.</param>
/// <param name="InterceptedCalls">Requests captured as LLM calls.</param>
/// <param name="ApiEndpoint">Where logs are sent.</param>
public sealed record CoolhandStats(long TotalRequests, long InterceptedCalls, string ApiEndpoint);

/// <summary>Pagination of a list result.</summary>
/// <param name="CurrentPage">The page returned (1-based).</param>
/// <param name="PerPage">Page size.</param>
/// <param name="TotalCount">Total matching records (a lower bound when the server sent no totals).</param>
/// <param name="TotalPages">Total pages (likewise).</param>
/// <param name="HasNextPage">Whether a next page exists.</param>
/// <param name="HasPrevPage">Whether a previous page exists.</param>
public sealed record Pagination(int CurrentPage, int PerPage, long TotalCount, long TotalPages, bool HasNextPage, bool HasPrevPage)
{
    /// <summary>Reads a body pagination envelope (<c>current_page</c>, <c>per_page</c>, ...); zeros when absent.</summary>
    public static Pagination FromJson(JsonNode? node)
    {
        if (node is not JsonObject obj) return new Pagination(1, 0, 0, 0, false, false);
        static long Num(JsonObject o, string key, long fallback) =>
            o[key] is JsonValue v && v.TryGetValue<long>(out var n) ? n
            : o[key] is JsonValue d && d.TryGetValue<double>(out var x) ? (long)x : fallback;
        static bool Bool(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
        return new Pagination(
            (int)Num(obj, "current_page", 1),
            (int)Num(obj, "per_page", 0),
            Num(obj, "total_count", 0),
            Num(obj, "total_pages", 0),
            Bool(obj, "has_next_page"),
            Bool(obj, "has_prev_page"));
    }
}

/// <summary>A failed read or admin call to the Coolhand API: network failure, redirect, or a non-JSON body.</summary>
public class CoolhandException : Exception
{
    /// <summary>Creates the exception.</summary>
    public CoolhandException(string message) : base(message) { }

    /// <summary>Creates the exception with its cause.</summary>
    public CoolhandException(string message, Exception? inner) : base(message, inner) { }
}

/// <summary>A non-2xx response from the Coolhand API (coolhand-node's <c>HttpError</c>).</summary>
public sealed class CoolhandHttpException : CoolhandException
{
    /// <summary>Creates the exception.</summary>
    public CoolhandHttpException(string message, int statusCode) : base(message) => StatusCode = statusCode;

    /// <summary>The HTTP status code.</summary>
    public int StatusCode { get; }
}
