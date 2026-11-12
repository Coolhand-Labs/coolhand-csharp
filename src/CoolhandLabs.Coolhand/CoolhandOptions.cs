using CoolhandLabs.Internal;

namespace CoolhandLabs;

/// <summary>Configuration for a <see cref="Coolhand"/> client (coolhand-node's <c>CoolhandOptions</c>).</summary>
public sealed record CoolhandOptions
{
    /// <summary>The default <see cref="ExcludeApiPatterns"/>: batch, file, fine-tuning and model-listing control-plane paths.</summary>
    public static readonly IReadOnlyList<string> DefaultExcludeApiPatterns = new[]
    {
        "/batchPredictionJobs/",
        "/openai/files", "/openai/v1/files",
        "/openai/batches", "/openai/v1/batches",
        "/openai/fine_tuning", "/openai/v1/fine_tuning",
        "/openai/models", "/openai/v1/models",
    };

    /// <summary>Default for <see cref="CaptureIdleTimeout"/>.</summary>
    public static readonly TimeSpan DefaultCaptureIdleTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Coolhand API key (required to construct a client).</summary>
    public string? ApiKey { get; init; }

    /// <summary>Suppress progress logging; default true.</summary>
    public bool Silent { get; init; } = true;

    /// <summary>A custom provider-patterns JSON file replacing the bundled <c>api-patterns.json</c>.</summary>
    public string? PatternsFile { get; init; }

    /// <summary>Verbose logging (endpoint and payload size before each call). Never suppresses submissions; use <see cref="DryRun"/> for that.</summary>
    public bool Debug { get; init; }

    /// <summary>Skip every write to the Coolhand API; the payload is logged instead when not silent.</summary>
    public bool DryRun { get; init; }

    /// <summary>The API origin; default <c>https://coolhandlabs.com</c>. Must be https, except <c>http://localhost</c>, <c>http://127.0.0.1</c> and <c>http://[::1]</c>.</summary>
    public string? BaseUrl { get; init; }

    /// <summary>Timeout for Coolhand API calls; default 30 seconds. File uploads use a separate 10-minute limit.</summary>
    public TimeSpan? RequestTimeout { get; init; }

    /// <summary>URL substrings (not globs) never captured; null means <see cref="DefaultExcludeApiPatterns"/>, an empty list excludes nothing.</summary>
    public IReadOnlyList<string>? ExcludeApiPatterns { get; init; }

    /// <summary>How long a response body may go unread before the call is logged with what was read so far; default 30 seconds (<see cref="TimeSpan.Zero"/> disables it).</summary>
    public TimeSpan CaptureIdleTimeout { get; init; } = DefaultCaptureIdleTimeout;

    /// <summary>
    /// The handler used to reach the Coolhand API (tests, proxies); null for a shared default that never
    /// follows redirects. A custom handler must not follow redirects either, or the API key could be
    /// carried to another host. It is not disposed by the client.
    /// </summary>
    public HttpMessageHandler? HttpMessageHandler { get; init; }

    /// <summary>True when an API key is present (not blank).</summary>
    public bool HasApiKey => !JsText.IsBlank(ApiKey);

    /// <summary>Options read from the process environment; see <see cref="FromEnvironment(IReadOnlyDictionary{string, string?})"/>.</summary>
    public static CoolhandOptions FromEnvironment()
    {
        var env = new Dictionary<string, string?>();
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            env[(string)entry.Key] = entry.Value as string;
        }
        return FromEnvironment(env);
    }

    /// <summary>
    /// Options read from <paramref name="env"/>, using the variables coolhand-node's auto-monitor reads:
    /// <c>COOLHAND_API_KEY</c>, <c>COOLHAND_SILENT</c> (anything but <c>false</c> is silent),
    /// <c>COOLHAND_PATTERNS_FILE</c>, <c>COOLHAND_DEBUG</c>, <c>COOLHAND_DRY_RUN</c> (<c>true</c>
    /// enables), <c>COOLHAND_BASE_URL</c> and <c>COOLHAND_EXCLUDE_API_PATTERNS</c> (comma-separated;
    /// a value with no non-blank entry keeps the defaults).
    /// </summary>
    public static CoolhandOptions FromEnvironment(IReadOnlyDictionary<string, string?> env)
    {
        string? Get(string name) => env.TryGetValue(name, out var v) ? v : null;
        var key = Get("COOLHAND_API_KEY");
        var patterns = Get("COOLHAND_PATTERNS_FILE");
        var baseUrl = Get("COOLHAND_BASE_URL");
        IReadOnlyList<string>? exclude = null;
        var excludeRaw = Get("COOLHAND_EXCLUDE_API_PATTERNS");
        if (!string.IsNullOrEmpty(excludeRaw))
        {
            var parsed = excludeRaw.Split(',').Select(JsText.Trim).Where(p => p.Length > 0).ToList();
            if (parsed.Count > 0) exclude = parsed;
        }
        return new CoolhandOptions
        {
            ApiKey = key is not null && !JsText.IsBlank(key) ? key : null,
            Silent = Get("COOLHAND_SILENT") != "false",
            PatternsFile = string.IsNullOrEmpty(patterns) ? null : patterns,
            Debug = Get("COOLHAND_DEBUG") == "true",
            DryRun = Get("COOLHAND_DRY_RUN") == "true",
            BaseUrl = string.IsNullOrEmpty(baseUrl) ? null : baseUrl,
            ExcludeApiPatterns = exclude,
        };
    }
}
