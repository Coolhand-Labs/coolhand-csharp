using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CoolhandLabs.Internal;

/// <summary>JSON parsing and serialization with <c>JSON.parse</c>/<c>JSON.stringify</c>-like behaviour.</summary>
internal static class Json
{
    private const int MaxDepth = 1024;

    private static readonly JsonDocumentOptions DocumentOptions = new() { MaxDepth = MaxDepth };

    private static readonly JsonSerializerOptions Compact = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        MaxDepth = MaxDepth,
        WriteIndented = false,
    };

    private static readonly JsonSerializerOptions Indented = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        MaxDepth = MaxDepth,
        WriteIndented = true,
    };

    /// <summary>Parses a JSON document; throws <see cref="JsonException"/> for anything else (including empty text).</summary>
    public static JsonNode? Parse(string text) => JsonNode.Parse(text, documentOptions: DocumentOptions);

    /// <summary>Parses, or returns false.</summary>
    public static bool TryParse(string text, out JsonNode? node)
    {
        try
        {
            node = Parse(text);
            // JsonObject defers duplicate-key detection until it is first read; force it now.
            Touch(node);
            return true;
        }
        catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException)
        {
            node = null;
            return false;
        }
    }

    private static void Touch(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var pair in obj) Touch(pair.Value);
                break;
            case JsonArray arr:
                foreach (var item in arr) Touch(item);
                break;
        }
    }

    public static string Stringify(JsonNode? node) => node is null ? "null" : node.ToJsonString(Compact);

    public static string Pretty(JsonNode? node) => node is null ? "null" : node.ToJsonString(Indented);

    /// <summary>A string property, or the text of a scalar property, or null.</summary>
    public static string? Text(JsonNode? node, string key)
    {
        if (node is not JsonObject obj || !obj.TryGetPropertyValue(key, out var value) || value is null) return null;
        return value is JsonValue v ? ScalarText(v) : value.ToJsonString(Compact);
    }

    public static string? ScalarText(JsonValue value)
    {
        if (value.TryGetValue<string>(out var s)) return s;
        return value.GetValueKind() switch
        {
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => null,
            _ => value.ToJsonString(Compact),
        };
    }

    /// <summary>True when <paramref name="node"/> is a JSON string.</summary>
    public static bool IsString(JsonNode? node) => node is JsonValue v && v.GetValueKind() == JsonValueKind.String;

    public static bool IsNumber(JsonNode? node) => node is JsonValue v && v.GetValueKind() == JsonValueKind.Number;

    public static string? AsString(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>Deep copy, preserving property order and raw number text.</summary>
    public static JsonNode? Clone(JsonNode? node) => node?.DeepClone();

    /// <summary>A JSON string node.</summary>
    public static JsonNode Str(string value) => JsonValue.Create(value);

    /// <summary>
    /// Converts a plain .NET value (string, number, bool, dictionary, list, JsonNode, JsonElement) to a
    /// JsonNode, keeping dictionary insertion order.
    /// </summary>
    public static JsonNode? From(object? value)
    {
        switch (value)
        {
            case null:
                return null;
            case JsonNode node:
                return node.Parent is null ? node : node.DeepClone();
            case JsonElement element:
                return JsonNode.Parse(element.GetRawText(), documentOptions: DocumentOptions);
            case string s:
                return JsonValue.Create(s);
            case bool b:
                return JsonValue.Create(b);
            case int i:
                return JsonValue.Create(i);
            case long l:
                return JsonValue.Create(l);
            case double d:
                return JsonValue.Create(d);
            case float f:
                return JsonValue.Create(f);
            case decimal m:
                return JsonValue.Create(m);
            case short or byte or uint or ulong or ushort or sbyte:
                return JsonValue.Create(Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture));
            case DateTimeOffset dto:
                return JsonValue.Create(JsText.IsoTimestamp(dto));
            case DateTime dt:
                return JsonValue.Create(JsText.JsString(dt));
            case Enum e:
                return JsonValue.Create(e.ToString());
            case System.Collections.IDictionary dict:
            {
                var obj = new JsonObject();
                foreach (System.Collections.DictionaryEntry entry in dict)
                {
                    obj[Convert.ToString(entry.Key, System.Globalization.CultureInfo.InvariantCulture) ?? ""] = From(entry.Value);
                }
                return obj;
            }
            case System.Collections.IEnumerable list:
            {
                var arr = new JsonArray();
                foreach (var item in list) arr.Add(From(item));
                return arr;
            }
            default:
                return JsonSerializer.SerializeToNode(value, value.GetType(), Compact);
        }
    }
}
