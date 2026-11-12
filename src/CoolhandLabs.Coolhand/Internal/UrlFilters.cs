using System.Text.RegularExpressions;

namespace CoolhandLabs.Internal;

/// <summary>
/// URL-level filters shared by every interception path: <c>ExcludeApiPatterns</c> substring
/// matching, the self-endpoint guard and the non-inference filter (ports of coolhand-node's
/// <c>exclude-patterns.ts</c>, <c>self-endpoint.ts</c> and <c>non-inference-filter.ts</c>).
/// </summary>
internal static class UrlFilters
{
    internal const string ExcludePatternsDocsUrl =
        "https://github.com/Coolhand-Labs/coolhand-csharp/blob/main/docs/exclude-patterns.md";

    private static readonly Regex[] IgnoredGetPathPatterns =
    {
        new("^/api/directory/servers(\\?.*)?$"), // MCP server directory listing
        new("^/v1/environments/[^/]+/work/poll(\\?.*)?$"), // managed-agents polling
    };

    private static readonly Regex[] IgnoredAnyMethodPathPatterns =
    {
        new("^/api/event_logging/"), // Claude Code telemetry batches
    };

    /// <summary>Drops a single trailing dot and lowercases, so <c>API.example.com.</c> equals <c>api.example.com</c>.</summary>
    public static string NormalizeHostname(string hostname)
    {
        var lower = hostname.ToLowerInvariant();
        return lower.EndsWith('.') ? lower.Substring(0, lower.Length - 1) : lower;
    }

    private static List<string> CanonicalCandidates(string url)
    {
        var parsed = UrlParts.Parse(url);
        if (parsed is null) return new List<string>();
        var output = new List<string> { parsed.Href() };
        if (parsed.Hostname.EndsWith('.')) output.Add(parsed.WithHostname(NormalizeHostname(parsed.Hostname)).Href());
        return output;
    }

    /// <summary>
    /// Substring (not glob) exclusion: an entry matches the URL as given, or its canonical form (host
    /// lowercased, one trailing host dot dropped, empty path as <c>/</c>). Empty entries are ignored,
    /// since they would match every URL.
    /// </summary>
    public static bool MatchesExcludePattern(string url, IReadOnlyList<string?>? patterns)
    {
        if (patterns is null || patterns.Count == 0) return false;
        var valid = patterns.Where(p => !string.IsNullOrEmpty(p)).Select(p => p!).ToList();
        if (valid.Any(p => url.Contains(p, StringComparison.Ordinal))) return true;
        if (valid.Count == 0) return false;
        var candidates = CanonicalCandidates(url);
        return valid.Any(p => candidates.Any(c => c.Contains(p, StringComparison.Ordinal)));
    }

    /// <summary>Warns (whatever <c>silent</c> says) about entries containing <c>*</c>, which only match literally.</summary>
    public static void WarnOnGlobLikePatterns(IEnumerable<string?>? patterns)
    {
        if (patterns is null) return;
        foreach (var p in patterns)
        {
            if (p is null)
            {
                Log.Warn("[coolhand-csharp] excludeApiPatterns entry null is ignored: entries must be strings, matched as "
                         + "literal substrings of the full URL (not regular expressions or globs). See " + ExcludePatternsDocsUrl);
            }
            else if (p.Contains('*'))
            {
                Log.Warn("[coolhand-csharp] excludeApiPatterns entry \"" + p + "\" contains \"*\", but entries are matched as "
                         + "literal substrings of the full URL, not globs — it will only match URLs that contain \"*\" "
                         + "verbatim. See " + ExcludePatternsDocsUrl);
            }
        }
    }

    /// <summary>The Coolhand API's own host and effective port, never intercepted.</summary>
    public sealed record SelfEndpoint(string Hostname, string Port);

    public static SelfEndpoint? ComputeSelfEndpoint(string apiEndpoint)
    {
        var url = UrlParts.Parse(apiEndpoint);
        return url is null ? null : new SelfEndpoint(NormalizeHostname(url.Hostname), url.EffectivePort);
    }

    public static bool IsSelfEndpointUrl(string url, SelfEndpoint? self)
    {
        if (self is null) return false;
        var parsed = UrlParts.Parse(url);
        return parsed is not null && NormalizeHostname(parsed.Hostname) == self.Hostname && parsed.EffectivePort == self.Port;
    }

    public static bool IsSelfOrExcluded(string url, SelfEndpoint? self, IReadOnlyList<string?>? excludePatterns) =>
        IsSelfEndpointUrl(url, self) || MatchesExcludePattern(url, excludePatterns);

    /// <summary>Anthropic endpoints that are not inference (telemetry, MCP directory, agent polling).</summary>
    public static bool IsNonInferenceUrl(string url, string method)
    {
        var u = UrlParts.Parse(url);
        if (u is null || u.Hostname != "api.anthropic.com") return false;
        if (IgnoredAnyMethodPathPatterns.Any(p => p.IsMatch(u.Path))) return true;
        if (!string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase)) return false;
        var pathAndSearch = u.Path + (string.IsNullOrEmpty(u.Query) ? "" : "?" + u.Query);
        return IgnoredGetPathPatterns.Any(p => p.IsMatch(pathAndSearch));
    }
}
