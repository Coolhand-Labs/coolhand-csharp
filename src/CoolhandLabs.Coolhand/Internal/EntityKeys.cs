using System.Globalization;

namespace CoolhandLabs.Internal;

/// <summary>
/// Client-side mirror of the server's workload/template key and description rules. A violation
/// server-side drops the whole log, so these are checked before anything is sent.
/// </summary>
internal static class EntityKeys
{
    public const int MaxKeyLength = 255;
    public const int MaxDescriptionLength = 1000;

    /// <summary>Why <paramref name="key"/> is not an acceptable workload/template key, or null if it is.</summary>
    public static string? KeyProblem(string key)
    {
        if (key.Length == 0 || JsText.Trim(key).Length == 0) return "must not be blank";
        if (JsText.CodePointLength(key) > MaxKeyLength) return "must be at most " + MaxKeyLength + " characters";
        if (HasLoneSurrogate(key)) return "must be valid Unicode (contains a lone surrogate)";
        if (HasInvisible(key)) return "must not contain control or invisible characters";
        if (key != JsText.Trim(key)) return "must not have leading or trailing whitespace";
        if (key.StartsWith('/') || key.EndsWith('/')) return "must not start or end with \"/\"";
        if (key.Contains("//", StringComparison.Ordinal)) return "must not contain \"//\"";
        return null;
    }

    /// <summary>Why <paramref name="description"/> is unacceptable, or null. Length is counted in code points.</summary>
    public static string? DescriptionProblem(string description) =>
        JsText.CodePointLength(description) > MaxDescriptionLength
            ? "must be at most " + MaxDescriptionLength + " characters"
            : null;

    private static bool HasLoneSurrogate(string s)
    {
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                {
                    i++;
                    continue;
                }
                return true;
            }
            if (char.IsLowSurrogate(c)) return true;
        }
        return false;
    }

    // \p{Cc}, \p{Cf} and the line/paragraph separators.
    private static bool HasInvisible(string s)
    {
        for (var i = 0; i < s.Length; i++)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(s, i);
            if (category is UnicodeCategory.Control or UnicodeCategory.Format || s[i] is '\u2028' or '\u2029') return true;
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) i++;
        }
        return false;
    }
}
