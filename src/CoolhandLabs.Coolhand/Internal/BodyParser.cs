using System.Text.Json.Nodes;

namespace CoolhandLabs.Internal;

/// <summary>coolhand-node's <c>parseBody</c>: JSON documents become trees, anything else stays text.</summary>
internal static class BodyParser
{
    /// <summary>
    /// Null for empty input; the parsed document for JSON (a top-level array is re-joined as
    /// newline-delimited JSON text, one element per line); otherwise the text itself.
    /// </summary>
    public static JsonNode? ParseBody(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        if (!Json.TryParse(text, out var parsed) || parsed is null) return Json.Str(text);
        if (parsed is JsonArray array)
        {
            return Json.Str(string.Join("\n", array.Select(Json.Stringify)));
        }
        return parsed;
    }

    /// <summary>Parses then sanitizes; fails closed (returns null) if either step throws.</summary>
    public static JsonNode? ParseAndSanitize(string? text, Func<JsonNode?, JsonNode?>? sanitize, Action<Exception>? onError)
    {
        try
        {
            var parsed = ParseBody(text);
            return sanitize is null ? parsed : sanitize(parsed);
        }
        catch (Exception e)
        {
            onError?.Invoke(e);
            return null;
        }
    }
}
