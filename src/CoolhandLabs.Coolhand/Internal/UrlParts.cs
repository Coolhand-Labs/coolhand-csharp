using System.Text;
using System.Text.RegularExpressions;

namespace CoolhandLabs.Internal;

/// <summary>
/// A lenient absolute-URL parser modelled on the parts of the WHATWG <c>URL</c> class coolhand-node
/// relies on: lowercased host, default port dropped, empty path read as <c>/</c>. Unlike
/// <see cref="Uri"/> it accepts the characters real request URLs carry unencoded (spaces, <c>|</c>,
/// brackets in queries), so matching and redaction never give up on a URL a client was happy to send.
/// </summary>
internal sealed class UrlParts
{
    private static readonly Regex SchemePattern = new("^[a-z][a-z0-9+.-]*$", RegexOptions.CultureInvariant);

    public string Scheme { get; }
    public string? Userinfo { get; }
    /// <summary>Lowercase; IPv6 literals keep their brackets, as <c>URL#hostname</c> does.</summary>
    public string Hostname { get; }
    /// <summary>Null when absent or the scheme's default.</summary>
    public int? Port { get; }
    /// <summary>Raw, never empty.</summary>
    public string Path { get; }
    /// <summary>Raw, without '?'; null when absent.</summary>
    public string? Query { get; }
    /// <summary>Raw, without '#'; null when absent.</summary>
    public string? Fragment { get; }

    private UrlParts(string scheme, string? userinfo, string hostname, int? port, string path, string? query, string? fragment)
    {
        Scheme = scheme;
        Userinfo = userinfo;
        Hostname = hostname;
        Port = port;
        Path = path;
        Query = query;
        Fragment = fragment;
    }

    /// <summary>Parses <paramref name="url"/>, or returns null when it is not an absolute <c>scheme://host</c> URL.</summary>
    public static UrlParts? Parse(string? url)
    {
        if (url is null) return null;
        var s = JsText.Trim(url);
        var colon = s.IndexOf(':');
        if (colon <= 0) return null;
        var scheme = s.Substring(0, colon).ToLowerInvariant();
        if (!SchemePattern.IsMatch(scheme) || string.CompareOrdinal(s, colon + 1, "//", 0, 2) != 0) return null;
        var authStart = colon + 3;
        var authEnd = s.Length;
        for (var i = authStart; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '/' || c == '?' || c == '#' || c == '\\')
            {
                authEnd = i;
                break;
            }
        }
        var authority = s.Substring(authStart, authEnd - authStart);
        string? userinfo = null;
        var at = authority.LastIndexOf('@');
        if (at >= 0)
        {
            userinfo = authority.Substring(0, at);
            authority = authority.Substring(at + 1);
        }
        string host;
        string? portText = null;
        if (authority.StartsWith('['))
        {
            var close = authority.IndexOf(']');
            if (close < 0) return null;
            host = authority.Substring(0, close + 1);
            var rest = authority.Substring(close + 1);
            if (rest.Length > 0)
            {
                if (!rest.StartsWith(':')) return null;
                portText = rest.Substring(1);
            }
        }
        else
        {
            var pc = authority.LastIndexOf(':');
            if (pc >= 0)
            {
                host = authority.Substring(0, pc);
                portText = authority.Substring(pc + 1);
            }
            else
            {
                host = authority;
            }
        }
        if (host.Length == 0 || host.Any(ch => ch <= ' ')) return null;
        int? port = null;
        if (!string.IsNullOrEmpty(portText))
        {
            if (portText.Length > 5 || !portText.All(char.IsAsciiDigit)) return null;
            var p = int.Parse(portText, System.Globalization.CultureInfo.InvariantCulture);
            if (p > 65535) return null;
            if (p != DefaultPort(scheme)) port = p;
        }
        var remainder = s.Substring(authEnd).Replace('\\', '/');
        string? fragment = null;
        var hash = remainder.IndexOf('#');
        if (hash >= 0)
        {
            fragment = remainder.Substring(hash + 1);
            remainder = remainder.Substring(0, hash);
        }
        string? query = null;
        var q = remainder.IndexOf('?');
        if (q >= 0)
        {
            query = remainder.Substring(q + 1);
            remainder = remainder.Substring(0, q);
        }
        var path = remainder.Length == 0 ? "/" : RemoveDotSegments(remainder);
        return new UrlParts(scheme, userinfo, host.ToLowerInvariant(), port, path, query, fragment);
    }

    public static int DefaultPort(string scheme) => scheme switch
    {
        "https" or "wss" => 443,
        "http" or "ws" => 80,
        "ftp" => 21,
        _ => -1,
    };

    /// <summary>The port in use: the explicit one, else the scheme default, as text.</summary>
    public string EffectivePort => Port?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? (Scheme == "https" ? "443" : "80");

    public UrlParts WithHostname(string hostname) => new(Scheme, Userinfo, hostname, Port, Path, Query, Fragment);

    public bool HasCredentials => !string.IsNullOrEmpty(Userinfo);

    /// <summary>Serializes like <c>URL#href</c>; the arguments let callers rewrite parts.</summary>
    public string Href(bool dropUserinfo = false, string? newQuery = null)
    {
        var output = new StringBuilder();
        output.Append(Scheme).Append("://");
        if (!dropUserinfo && HasCredentials) output.Append(Userinfo).Append('@');
        output.Append(Hostname);
        if (Port is not null) output.Append(':').Append(Port.Value);
        output.Append(Path);
        var q = newQuery ?? Query;
        if (!string.IsNullOrEmpty(q)) output.Append('?').Append(q);
        if (Fragment is not null) output.Append('#').Append(Fragment);
        return output.ToString();
    }

    /// <summary>RFC 3986 dot-segment removal, also treating <c>%2e</c> as a dot as WHATWG parsing does.</summary>
    public static string RemoveDotSegments(string path)
    {
        var parts = path.Split('/');
        var output = new List<string>();
        var trailingSlash = false;
        for (var i = 1; i < parts.Length; i++)
        {
            var seg = parts[i];
            var lower = seg.ToLowerInvariant();
            var last = i == parts.Length - 1;
            if (lower is "." or "%2e")
            {
                trailingSlash = last;
                continue;
            }
            if (lower is ".." or ".%2e" or "%2e." or "%2e%2e")
            {
                if (output.Count > 0) output.RemoveAt(output.Count - 1);
                trailingSlash = last;
                continue;
            }
            output.Add(seg);
            trailingSlash = false;
        }
        var joined = "/" + string.Join("/", output);
        if (trailingSlash && !joined.EndsWith('/')) joined += "/";
        return joined;
    }
}
