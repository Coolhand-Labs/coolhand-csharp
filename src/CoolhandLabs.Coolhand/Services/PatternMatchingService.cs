using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CoolhandLabs.Internal;

namespace CoolhandLabs.Services;

/// <summary>
/// Recognises LLM API requests and redacts credentials from what gets logged: a port of coolhand-node's
/// <c>PatternMatchingService</c>. Patterns come from the bundled <c>api-patterns.json</c> (identical to
/// coolhand-node's) or a custom patterns file.
/// </summary>
/// <remarks>
/// Redaction covers request/response headers (credential names and pattern-specific headers), URL
/// query parameters and userinfo, and bodies: JSON (credential keys, Azure "On Your Data"
/// <c>data_sources</c>, MCP server credentials), NDJSON, <c>application/x-www-form-urlencoded</c>,
/// <c>multipart/form-data</c> and unparseable or truncated JSON-like text.
/// </remarks>
public sealed class PatternMatchingService
{
    private const string BundledPatternsResource = "CoolhandLabs.api-patterns.json";
    private const string Redacted = "[REDACTED]";

    private static readonly string[] DefaultRedactedHeaders =
    {
        "authorization", "api-key", "x-api-key", "cookie", "set-cookie", "proxy-authorization",
        "openai-api-key", "x-goog-api-key", "cf-aig-authorization", "x-amz-security-token",
        "ocp-apim-subscription-key", "subscription-key", "xi-api-key",
    };

    private static readonly Regex CredentialHeaderPattern = new("auth|key|token|secret|cookie|passw|credential|bearer|jwt|signature");
    private static readonly Regex CredentialHeaderExemptPattern = new("rate-?limit");
    private static readonly Regex WildcardOnlyDomain = new(@"^[*.\s]*$");

    private static readonly HashSet<string> SensitiveQueryParams = NormalizedSet(
        "key", "api_key", "apikey", "token", "access_token", "secret", "password", "signature", "sig",
        "x-goog-api-key", "x-amz-signature", "x-amz-credential", "x-amz-security-token", "subscription-key",
        "ocp-apim-subscription-key", "x-api-key", "client_secret", "refresh_token", "id_token", "authorization",
        "api_token", "auth_token", "bearer_token", "secret_key", "private_key", "access_key", "auth", "bearer",
        "pwd", "x-goog-signature", "x-goog-credential", "session_token", "oauth_token", "passwd",
        "secret_access_key");

    private static readonly string[] CredentialKeyFragments =
        { "key", "secret", "password", "passwd", "pwd", "token", "credential", "connectionstring" };

    private static readonly HashSet<string> AlwaysRedactedBodyKeys = new() { "authorizationtoken", "clientsecret" };

    private static readonly HashSet<string> AlwaysRedactedBodyStringKeys = new()
    {
        "accesstoken", "refreshtoken", "idtoken", "apikey", "xapikey", "password", "secret", "secretkey",
        "privatekey", "apitoken", "authtoken", "bearertoken", "accesskey", "sessiontoken", "passwd",
        "secretaccesskey", "oauthtoken", "newpassword", "oldpassword", "currentpassword", "confirmpassword",
        "passwordconfirmation", "openaiapikey", "anthropicapikey", "awssessiontoken", "awssecretaccesskey",
        "googleapikey", "anthropicauthtoken", "githubtoken", "apisecret",
    };

    private static readonly HashSet<string> FormOnlyCredentialKeys = new() { "token", "assertion", "clientassertion" };
    private static readonly HashSet<string> McpCredentialKeys = new() { "authorization", "headers" };
    private const int MaxBodyDepth = 64;
    private const int MaxKeyAffix = 200;

    private static readonly Regex[] MultipartParamPatterns =
    {
        new(@";\s*([A-Za-z0-9_*-]+)\s*=\s*(?:""((?:\\.|[^""\\])*)""|([^;\s]*))"),
        new(@";\s*([A-Za-z0-9_*-]+)\s*=\s*(?:""([^""]*)""|([^;\s]*))"),
    };

    private static readonly Regex MultipartDelimiter = new(
        @"(?:^\s*|\n)(--[^\r\n]{1,200})\r?\n(?:[^\r\n]*\r?\n){0,4}?content-disposition:\s*form-data", RegexOptions.IgnoreCase);

    private static readonly Regex CredentialWordPattern = new(string.Join("|",
        EscapeAwareWord("key"), EscapeAwareWord("secret"), EscapeAwareWord("password"),
        EscapeAwareWord("token"), EscapeAwareWord("authorization"),
        EscapeAwareWord("connection") + @"(?:_|\\u005f)?" + EscapeAwareWord("string")), RegexOptions.IgnoreCase);

    private static readonly Regex KeyTail = new(@"\G\s*:\s*""");
    private static readonly Regex Brackets = new(@"[\[\]]+");
    private static readonly Regex FormJsonStart = new(@"^[{\[""]");
    private static readonly Regex Pct5B = new("%5B", RegexOptions.IgnoreCase);
    private static readonly Regex Pct5D = new("%5D", RegexOptions.IgnoreCase);
    private static readonly Regex PartSeparator = new(@"\r?\n\r?\n");
    private static readonly Regex TrailingNewline = new(@"\r?\n\z");
    private static readonly Regex LeadingWs = new(@"^\s*");
    private static readonly Regex SingleQuoted = new("^'(.*)'$", RegexOptions.Singleline);
    private static readonly Regex BackslashEscape = new(@"\\(.)", RegexOptions.Singleline);
    private static readonly Regex Rfc5987Prefix = new("^[^']*'[^']*'");
    private static readonly Regex MultiSlash = new("/{2,}");

    private readonly Log _log;
    private readonly IReadOnlyList<ApiPattern> _apiPatterns;
    private readonly ConcurrentDictionary<string, Regex> _wildcardDomainCache = new();

    /// <summary>
    /// Loads <paramref name="customPatternsFile"/> when given, falling back to the bundled patterns (with a
    /// log message) when it is missing, unreadable or not shaped <c>{ patterns: [{ domains: [...] }] }</c>.
    /// </summary>
    public PatternMatchingService(string? customPatternsFile = null, bool silent = true)
    {
        _log = new Log(silent);
        IReadOnlyList<ApiPattern>? loaded = null;
        if (customPatternsFile is not null) loaded = LoadCustomPatterns(customPatternsFile);
        _apiPatterns = loaded ?? LoadDefaultPatterns();
    }

    // ------------------------------------------------------------------ loading

    private IReadOnlyList<ApiPattern> LoadDefaultPatterns()
    {
        var patterns = BundledPatterns();
        _log.Info("📋 Loaded " + patterns.Count + " default API patterns");
        return patterns;
    }

    private static readonly Lazy<IReadOnlyList<ApiPattern>> Bundled = new(() =>
    {
        using var stream = typeof(PatternMatchingService).Assembly.GetManifestResourceStream(BundledPatternsResource)
                           ?? throw new InvalidOperationException("api-patterns.json missing from the coolhand-csharp assembly");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var root = Json.Parse(reader.ReadToEnd());
        return ToPatterns((JsonArray)root!["patterns"]!);
    });

    /// <summary>The bundled <c>api-patterns.json</c>.</summary>
    public static IReadOnlyList<ApiPattern> BundledPatterns() => Bundled.Value;

    private IReadOnlyList<ApiPattern>? LoadCustomPatterns(string file)
    {
        var resolved = Path.GetFullPath(file);
        try
        {
            if (!File.Exists(resolved))
            {
                _log.WarnUnlessSilent("⚠️  API patterns file not found: " + resolved + ". Falling back to default patterns.");
                return null;
            }
            var data = Json.Parse(File.ReadAllText(resolved, Encoding.UTF8));
            var patterns = ValidatePatternsShape(data, resolved);
            _log.Info("📋 Loaded " + patterns.Count + " API patterns from custom patterns file");
            return patterns;
        }
        catch (Exception e)
        {
            _log.WarnUnlessSilent("❌ Error loading API patterns: " + e.Message);
            return null;
        }
    }

    private static bool IsStringArray(JsonNode? node) => node is JsonArray array && array.All(Json.IsString);

    private static bool AllIntegers(JsonArray array) =>
        array.All(item => item is JsonValue v && v.TryGetValue<double>(out var d) && d == Math.Floor(d) && !double.IsInfinity(d));

    private IReadOnlyList<ApiPattern> ValidatePatternsShape(JsonNode? data, string sourceFile)
    {
        var patterns = data is JsonObject obj ? obj["patterns"] as JsonArray : null;
        var valid = patterns is not null && patterns.All(p =>
            p is JsonObject o
            && IsStringArray(o["domains"])
            && (o["paths"] is null || IsStringArray(o["paths"]))
            && (o["ports"] is null || (o["ports"] is JsonArray ports && AllIntegers(ports))));
        if (!valid)
        {
            throw new ArgumentException("Coolhand: patterns file \"" + sourceFile + "\" is not shaped correctly "
                                        + "(expected { patterns: [{ domains: string[], paths?: string[], ports?: number[], ... }] })");
        }
        var output = new List<ApiPattern>();
        foreach (var pattern in ToPatterns(patterns!))
        {
            var domains = new List<string>();
            foreach (var domain in pattern.Domains)
            {
                if (WildcardOnlyDomain.IsMatch(domain))
                {
                    _log.WarnUnlessSilent("⚠️  Coolhand: ignoring domain \"" + domain + "\" in \"" + pattern.Name + "\" ("
                                          + sourceFile + ") — it would match every host");
                }
                else
                {
                    domains.Add(domain);
                }
            }
            output.Add(domains.Count == pattern.Domains.Count ? pattern : pattern with { Domains = domains });
        }
        return output;
    }

    private static IReadOnlyList<ApiPattern> ToPatterns(JsonArray array)
    {
        var output = new List<ApiPattern>();
        foreach (var node in array)
        {
            if (node is not JsonObject o) continue;
            static List<string>? Strings(JsonNode? n) => n is JsonArray a ? a.Select(x => Json.AsString(x) ?? "").ToList() : null;
            static bool? Bool(JsonNode? n) => n is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;
            Dictionary<string, string>? headers = null;
            if (o["headers"] is JsonObject h)
            {
                headers = new Dictionary<string, string>();
                foreach (var pair in h)
                {
                    if (pair.Value is JsonValue v) headers[pair.Key] = Json.ScalarText(v) ?? "";
                }
            }
            output.Add(new ApiPattern(
                Json.Text(o, "id"),
                Json.Text(o, "name"),
                Strings(o["domains"]) ?? new List<string>(),
                Strings(o["paths"]),
                Bool(o["allowPathMatchAcrossDomains"]),
                Bool(o["requiresPathMatch"]),
                o["ports"] is JsonArray ports ? ports.Select(p => (int)p!.GetValue<double>()).ToList() : null,
                headers));
        }
        return output;
    }

    /// <summary>The patterns in use.</summary>
    public IReadOnlyList<ApiPattern> LoadedPatterns => _apiPatterns;

    /// <summary>How many patterns are in use.</summary>
    public int PatternsCount => _apiPatterns.Count;

    // ------------------------------------------------------------------ matching

    // Canonical path for paths/requiresPathMatch matching: query dropped, dot-segments resolved,
    // duplicate slashes collapsed.
    internal static string NormalizeMatchPath(string path)
    {
        var cut = path.IndexOfAny(new[] { '?', '#' });
        var raw = cut >= 0 ? path.Substring(0, cut) : path;
        var resolved = raw.TrimStart('/');
        return MultiSlash.Replace(UrlParts.RemoveDotSegments("/" + resolved), "/");
    }

    private bool HostnameMatchesDomain(string hostname, string domain)
    {
        if (domain.Contains('*'))
        {
            return !WildcardOnlyDomain.IsMatch(domain) && WildcardDomainRegex(domain).IsMatch(hostname);
        }
        var lowerDomain = domain.ToLowerInvariant();
        return hostname == lowerDomain || hostname.EndsWith("." + lowerDomain, StringComparison.Ordinal);
    }

    // A '*' stands for exactly one DNS label; like plain domains, it also matches subdomains in front.
    private Regex WildcardDomainRegex(string domain) => _wildcardDomainCache.GetOrAdd(domain, d =>
    {
        var collapsed = Regex.Replace(d, @"\*+", "*");
        var body = string.Join("[a-z0-9-]+", collapsed.Split('*').Select(Regex.Escape));
        return new Regex("(^|\\.)" + body + "$", RegexOptions.IgnoreCase);
    });

    private static bool PathnameMatchesEntry(string pathname, string entry)
    {
        if (!pathname.StartsWith(entry, StringComparison.Ordinal)) return false;
        return entry.EndsWith('/') || pathname.Length == entry.Length || pathname[entry.Length] == '/';
    }

    private MatchedPattern? FindDomainMatch(string hostname, int? port, string? path)
    {
        string? pathname = null;
        var lowerHostname = UrlFilters.NormalizeHostname(hostname);
        foreach (var pattern in _apiPatterns)
        {
            var matchedDomain = pattern.Domains.FirstOrDefault(d => HostnameMatchesDomain(lowerHostname, d));
            var requiresPath = pattern.RequiresPathMatch == true;
            var portMatches = requiresPath && port is not null && pattern.Ports is not null && pattern.Ports.Contains(port.Value);
            if (matchedDomain is null && !portMatches) continue;
            if (!requiresPath)
            {
                if (matchedDomain is null) continue;
                return new MatchedPattern(pattern, "domain", matchedDomain);
            }
            if (path is null) continue;
            pathname ??= NormalizeMatchPath(path);
            var matchedPath = pattern.Paths?.FirstOrDefault(entry => PathnameMatchesEntry(pathname, entry));
            if (matchedPath is null) continue;
            return matchedDomain is not null
                ? new MatchedPattern(pattern, "domain", matchedDomain)
                : new MatchedPattern(pattern, "path", matchedPath);
        }
        return null;
    }

    /// <summary>
    /// The provider pattern <paramref name="url"/> belongs to, or null when it is not a recognised LLM
    /// endpoint (including when it does not parse). Never throws.
    /// </summary>
    public MatchedPattern? MatchesApiPatternFromUrl(string? url)
    {
        try
        {
            var parsed = UrlParts.Parse(url);
            if (parsed is null) return null;
            var domainMatch = FindDomainMatch(parsed.Hostname, parsed.Port, parsed.Path);
            if (domainMatch is not null) return domainMatch;
            foreach (var pattern in _apiPatterns)
            {
                if (pattern.Paths is null || pattern.AllowPathMatchAcrossDomains != true) continue;
                var normalized = NormalizeMatchPath(parsed.Path);
                var hit = pattern.Paths.FirstOrDefault(p => normalized.Contains(p, StringComparison.Ordinal));
                if (hit is not null) return new MatchedPattern(pattern, "path", hit);
            }
            return null;
        }
        catch (Exception e)
        {
            _log.WarnUnlessSilent("⚠️  Coolhand: pattern matching failed, request will not be monitored: " + e.Message);
            return null;
        }
    }

    // ------------------------------------------------------------------ headers

    private static bool IsCredentialHeaderName(string lowerName) =>
        CredentialHeaderPattern.IsMatch(lowerName) && !CredentialHeaderExemptPattern.IsMatch(lowerName);

    /// <summary>
    /// Headers as they are logged: names lowercased, repeated values joined with <c>", "</c>, credential
    /// headers replaced with <c>[REDACTED]</c>, then the pattern's own header rules applied.
    /// </summary>
    /// <param name="headers">Header names (any case) to their values.</param>
    /// <param name="pattern">The matched provider pattern, or null.</param>
    public IReadOnlyDictionary<string, string> SanitizeHeaders(IEnumerable<KeyValuePair<string, IEnumerable<string>>>? headers, ApiPattern? pattern = null)
    {
        var collected = new Dictionary<string, List<string>>();
        var order = new List<string>();
        if (headers is not null)
        {
            foreach (var pair in headers)
            {
                if (pair.Key is null) continue;
                var name = pair.Key.ToLowerInvariant();
                if (!collected.TryGetValue(name, out var values))
                {
                    values = new List<string>();
                    collected[name] = values;
                    order.Add(name);
                }
                if (pair.Value is not null) values.AddRange(pair.Value.Where(v => v is not null));
            }
        }
        var sanitized = new Dictionary<string, string>();
        foreach (var name in order) sanitized[name] = string.Join(", ", collected[name]);
        foreach (var name in DefaultRedactedHeaders)
        {
            if (sanitized.ContainsKey(name)) sanitized[name] = Redacted;
        }
        foreach (var name in order)
        {
            if (IsCredentialHeaderName(name)) sanitized[name] = Redacted;
        }
        if (pattern?.Headers is not null)
        {
            foreach (var rule in pattern.Headers)
            {
                var lowerKey = rule.Key.ToLowerInvariant();
                if (sanitized.TryGetValue(lowerKey, out var current) && current.Length > 0) sanitized[lowerKey] = rule.Value;
            }
        }
        return sanitized;
    }

    /// <summary>Single-valued convenience overload of <see cref="SanitizeHeaders(IEnumerable{KeyValuePair{string, IEnumerable{string}}}, ApiPattern)"/>.</summary>
    public IReadOnlyDictionary<string, string> SanitizeHeaders(IEnumerable<KeyValuePair<string, string>>? headers, ApiPattern? pattern = null) =>
        SanitizeHeaders(headers?.Select(h => new KeyValuePair<string, IEnumerable<string>>(h.Key, new[] { h.Value })), pattern);

    // ------------------------------------------------------------------ URLs

    private static string NormalizeKey(string key) => key.ToLowerInvariant().Replace("_", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal);

    private static HashSet<string> NormalizedSet(params string[] names) => new(names.Select(NormalizeKey));

    /// <summary>
    /// <paramref name="url"/> with credential query parameters replaced by <c>[REDACTED]</c> and any
    /// <c>user:pass@</c> removed; returned unchanged when there is nothing to redact.
    /// </summary>
    public string SanitizeUrl(string url)
    {
        try
        {
            var parts = UrlParts.Parse(url);
            if (parts is null) return url;
            var redacted = parts.HasCredentials;
            string? newQuery = null;
            if (!string.IsNullOrEmpty(parts.Query))
            {
                var rebuilt = new List<string>();
                var paramRedacted = false;
                foreach (var pair in parts.Query.Split('&'))
                {
                    if (pair.Length == 0) continue;
                    var eq = pair.IndexOf('=');
                    var name = JsText.DecodeFormLenient(eq < 0 ? pair : pair.Substring(0, eq));
                    var value = eq < 0 ? "" : JsText.DecodeFormLenient(pair.Substring(eq + 1));
                    if (SensitiveQueryParams.Contains(NormalizeKey(name)))
                    {
                        value = Redacted;
                        paramRedacted = true;
                    }
                    rebuilt.Add(JsText.FormEncode(name) + "=" + JsText.FormEncode(value));
                }
                if (paramRedacted)
                {
                    newQuery = string.Join("&", rebuilt);
                    redacted = true;
                }
            }
            return redacted ? parts.Href(dropUserinfo: true, newQuery: newQuery) : url;
        }
        catch (Exception)
        {
            return url;
        }
    }

    // ------------------------------------------------------------------ bodies

    private static bool IsStringOrStringArray(JsonNode? val, int depth)
    {
        if (Json.IsString(val)) return true;
        if (val is not JsonArray array || depth >= MaxBodyDepth) return false;
        return array.Any(item => IsStringOrStringArray(item, depth + 1));
    }

    private static bool MatchesCredentialName(string name, HashSet<string> names, string normalizedName)
    {
        if (names.Contains(normalizedName)) return true;
        if (!name.Contains('[')) return false;
        foreach (var segment in Brackets.Split(name))
        {
            var trimmed = JsText.Trim(segment);
            if (trimmed.Length > 0 && names.Contains(NormalizeKey(trimmed))) return true;
        }
        return false;
    }

    private static bool IsCredentialStringKey(string name, string normalizedName) =>
        MatchesCredentialName(name, AlwaysRedactedBodyStringKeys, normalizedName);

    private static bool IsFormCredentialKey(string name)
    {
        var normalizedName = NormalizeKey(name);
        return IsCredentialStringKey(name, normalizedName)
               || MatchesCredentialName(name, AlwaysRedactedBodyKeys, normalizedName)
               || MatchesCredentialName(name, FormOnlyCredentialKeys, normalizedName);
    }

    internal static string RedactFormEncodedFields(string rawText)
    {
        var text = JsText.Trim(rawText);
        if (!text.Contains('=') || FormJsonStart.IsMatch(text)) return rawText;
        var changed = false;
        var pairs = text.Split('&');
        for (var i = 0; i < pairs.Length; i++)
        {
            var pair = pairs[i];
            var eq = pair.IndexOf('=');
            if (eq == -1) continue;
            var name = pair.Substring(0, eq).Replace('+', ' ');
            try
            {
                name = JsText.Decode(name, false);
            }
            catch (FormatException)
            {
                name = Pct5D.Replace(Pct5B.Replace(name, "["), "]");
            }
            if (!IsFormCredentialKey(JsText.Trim(name))) continue;
            changed = true;
            pairs[i] = pair.Substring(0, eq + 1) + Redacted;
        }
        return changed ? string.Join("&", pairs) : rawText;
    }

    /// <summary>Multipart redaction; null when <paramref name="text"/> is not multipart.</summary>
    internal static string? RedactMultipartFields(string text)
    {
        var delimiterMatch = MultipartDelimiter.Match(text);
        if (!delimiterMatch.Success) return null;
        var delimiter = JsText.TrimEnd(delimiterMatch.Groups[1].Value);
        var leading = LeadingWs.Match(text).Value;
        var delimiterPattern = new Regex("(?<=^|\\n)" + Regex.Escape(delimiter) + "(?=(?:--)?[ \\t]*(?:\\r?\\n|\\z))");
        var changed = false;
        var pieces = delimiterPattern.Split(text.Substring(leading.Length));
        for (var index = 0; index < pieces.Length; index++)
        {
            var piece = pieces[index];
            if (index == 0 || piece.StartsWith("--", StringComparison.Ordinal)) continue;
            var separator = PartSeparator.Match(piece);
            if (!separator.Success) continue;
            var headers = piece.Substring(0, separator.Index);
            var isCredentialPart = false;
            for (var pass = 0; pass < MultipartParamPatterns.Length && !isCredentialPart; pass++)
            {
                foreach (Match param in MultipartParamPatterns[pass].Matches(headers))
                {
                    var key = param.Groups[1].Value.ToLowerInvariant();
                    if (key is not ("name" or "name*")) continue;
                    var name = param.Groups[2].Success ? param.Groups[2].Value : SingleQuoted.Replace(param.Groups[3].Value, "$1");
                    if (pass == 0) name = BackslashEscape.Replace(name, "$1");
                    if (key == "name*") name = Rfc5987Prefix.Replace(name, "", 1);
                    try
                    {
                        name = JsText.Decode(name, false);
                    }
                    catch (FormatException)
                    {
                        // keep the raw name
                    }
                    if (IsFormCredentialKey(JsText.Trim(name)))
                    {
                        isCredentialPart = true;
                        break;
                    }
                }
            }
            var valueStart = separator.Index + separator.Length;
            var value = piece.Substring(valueStart);
            var tn = TrailingNewline.Match(value);
            var trailingNewline = tn.Success ? tn.Value : "";
            if (isCredentialPart)
            {
                changed = true;
                pieces[index] = piece.Substring(0, valueStart) + Redacted + trailingNewline;
                continue;
            }
            var plainValue = value.Substring(0, value.Length - trailingNewline.Length);
            var scrubbed = RedactFormEncodedFields(plainValue);
            if (scrubbed != plainValue)
            {
                changed = true;
                pieces[index] = piece.Substring(0, valueStart) + scrubbed + trailingNewline;
            }
        }
        return changed ? leading + string.Join(delimiter, pieces) : text;
    }

    private static string EscapeAwareWord(string word)
    {
        var output = new StringBuilder();
        foreach (var ch in word)
        {
            output.Append("(?:").Append(ch)
                .Append(@"|\\u").Append(((int)char.ToLowerInvariant(ch)).ToString("x4", CultureInfo.InvariantCulture))
                .Append(@"|\\u").Append(((int)char.ToUpperInvariant(ch)).ToString("x4", CultureInfo.InvariantCulture))
                .Append(')');
        }
        return output.ToString();
    }

    /// <summary>
    /// Replaces the string value of every <c>"&lt;key containing a credential word&gt;": "..."</c> in an
    /// unparseable (malformed or truncated) body, including an unterminated value.
    /// </summary>
    internal static string RedactCredentialStrings(string text)
    {
        var prevQuote = -1;
        var nextQuote = text.IndexOf('"');
        var output = new StringBuilder();
        var last = 0;
        var searchFrom = 0;
        while (searchFrom <= text.Length)
        {
            var words = CredentialWordPattern.Match(text, searchFrom);
            if (!words.Success) break;
            var index = words.Index;
            var wordEnd = words.Index + words.Length;
            searchFrom = wordEnd > index ? wordEnd : wordEnd + 1;
            while (nextQuote != -1 && nextQuote < index)
            {
                prevQuote = nextQuote;
                nextQuote = text.IndexOf('"', nextQuote + 1);
            }
            if (prevQuote == -1 || nextQuote == -1) continue;
            if (index - prevQuote - 1 > MaxKeyAffix || nextQuote - wordEnd > MaxKeyAffix) continue;
            var tail = KeyTail.Match(text, nextQuote + 1);
            if (!tail.Success) continue;
            var valueStart = tail.Index + tail.Length;
            var end = valueStart;
            while (end < text.Length && text[end] != '"') end += text[end] == '\\' ? 2 : 1;
            end = Math.Min(end, text.Length);
            output.Append(text, last, valueStart - last).Append(Redacted);
            last = end;
            searchFrom = end;
        }
        return output.Append(text, last, text.Length - last).ToString();
    }

    private static JsonNode? RedactBodyCredentials(JsonNode? value, bool insideDataSources, int depth)
    {
        if (depth > MaxBodyDepth) return JsonValue.Create("[REDACTED: nested too deeply]");
        switch (value)
        {
            case JsonArray array:
            {
                var output = new JsonArray();
                foreach (var item in array) output.Add(RedactBodyCredentials(item, insideDataSources, depth + 1));
                return output;
            }
            case JsonObject obj:
            {
                var isMcp = Json.AsString(obj["type"]) == "mcp"
                            || obj.ContainsKey("server_url") || obj.ContainsKey("serverUrl")
                            || obj.ContainsKey("server_label") || obj.ContainsKey("serverLabel");
                var output = new JsonObject();
                foreach (var field in obj)
                {
                    var key = field.Key;
                    var val = field.Value;
                    var normalizedKey = NormalizeKey(key);
                    var nowInside = insideDataSources || normalizedKey == "datasources";
                    var redact = (nowInside && CredentialKeyFragments.Any(f => normalizedKey.Contains(f, StringComparison.Ordinal)))
                                 || MatchesCredentialName(key, AlwaysRedactedBodyKeys, normalizedKey)
                                 || (IsCredentialStringKey(key, normalizedKey) && IsStringOrStringArray(val, 0))
                                 || (isMcp && McpCredentialKeys.Contains(normalizedKey));
                    output[key] = redact ? JsonValue.Create(Redacted) : RedactBodyCredentials(val, nowInside, depth + 1);
                }
                return output;
            }
            default:
                return value?.DeepClone();
        }
    }

    private static string SanitizeStringBody(string text)
    {
        var stripped = text.Length > 0 && text[0] == '\uFEFF' ? text.Substring(1) : text;
        if (Json.TryParse(stripped, out var parsed)) return Json.Stringify(RedactBodyCredentials(parsed, false, 0));
        var lines = stripped.Split('\n');
        if (lines.Length > 1)
        {
            var output = new List<string>(lines.Length);
            var allJson = true;
            foreach (var line in lines)
            {
                if (JsText.Trim(line).Length == 0)
                {
                    output.Add(line);
                    continue;
                }
                if (!Json.TryParse(line, out var lineNode))
                {
                    allJson = false;
                    break;
                }
                output.Add(Json.Stringify(RedactBodyCredentials(lineNode, false, 0)));
            }
            if (allJson) return string.Join("\n", output);
        }
        var multipart = RedactMultipartFields(stripped);
        if (multipart is not null) return RedactCredentialStrings(multipart);
        return RedactCredentialStrings(RedactFormEncodedFields(text));
    }

    /// <summary>
    /// A parsed body with credentials redacted. Text bodies are treated as JSON, NDJSON, multipart,
    /// form-encoded or, failing all of those, scrubbed by key name. Fails closed: returns null if
    /// redaction itself fails.
    /// </summary>
    public JsonNode? SanitizeBody(JsonNode? body)
    {
        if (body is null) return null;
        try
        {
            if (Json.AsString(body) is { } text) return text.Length == 0 ? JsonValue.Create(text) : JsonValue.Create(SanitizeStringBody(text));
            if (body is not (JsonObject or JsonArray)) return body.DeepClone();
            return RedactBodyCredentials(body, false, 0);
        }
        catch (Exception e)
        {
            _log.WarnUnlessSilent("⚠️  Coolhand: body sanitization failed, request body will not be captured: " + e.Message);
            return null;
        }
    }
}
