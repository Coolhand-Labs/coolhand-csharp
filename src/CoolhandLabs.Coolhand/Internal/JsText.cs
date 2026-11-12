using System.Globalization;
using System.Text;

namespace CoolhandLabs.Internal;

/// <summary>
/// JavaScript string semantics the wire format depends on, so this SDK produces what coolhand-node
/// produces: <c>String.prototype.trim</c>, <c>encodeURIComponent</c>, <c>URLSearchParams</c>
/// encoding and <c>Date.prototype.toISOString</c>.
/// </summary>
internal static class JsText
{
    /// <summary>Whitespace and line terminators as <c>String.prototype.trim</c> defines them.</summary>
    public static bool IsJsWhitespace(char c) => c switch
    {
        '\t' or '\n' or '\u000B' or '\f' or '\r' or ' ' or '\u00A0' or '\u1680' or '\u2028' or '\u2029'
            or '\u202F' or '\u205F' or '\u3000' or '\uFEFF' => true,
        _ => c >= '\u2000' && c <= '\u200A',
    };

    public static string Trim(string s)
    {
        var start = 0;
        var end = s.Length;
        while (start < end && IsJsWhitespace(s[start])) start++;
        while (end > start && IsJsWhitespace(s[end - 1])) end--;
        return s.Substring(start, end - start);
    }

    public static string TrimEnd(string s)
    {
        var end = s.Length;
        while (end > 0 && IsJsWhitespace(s[end - 1])) end--;
        return s.Substring(0, end);
    }

    public static bool IsBlank(string? s) => s is null || Trim(s).Length == 0;

    /// <summary><c>encodeURIComponent</c>: everything but <c>A-Z a-z 0-9 - _ . ! ~ * ' ( )</c> is percent-encoded.</summary>
    public static string EncodeUriComponent(string s) => PercentEncode(s, "-_.!~*'()", false);

    /// <summary><c>application/x-www-form-urlencoded</c> as <c>URLSearchParams</c> writes it (space becomes <c>+</c>).</summary>
    public static string FormEncode(string s) => PercentEncode(s, "*-._", true);

    private static string PercentEncode(string s, string safe, bool spaceAsPlus)
    {
        var bytes = Utf8(s);
        var output = new StringBuilder(s.Length + 16);
        foreach (var b in bytes)
        {
            var c = (char)b;
            if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || safe.IndexOf(c) >= 0)
            {
                output.Append(c);
            }
            else if (c == ' ' && spaceAsPlus)
            {
                output.Append('+');
            }
            else
            {
                output.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
            }
        }
        return output.ToString();
    }

    // Lone surrogates become U+FFFD, as JavaScript's encoders do (they would throw; the server never sees them).
    private static byte[] Utf8(string s) => new UTF8Encoding(false, false).GetBytes(s);

    private static int Hex(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1,
    };

    /// <summary>Decodes <c>%XX</c> escapes (and <c>+</c> when <paramref name="plusAsSpace"/>); throws <see cref="FormatException"/> on a malformed escape.</summary>
    public static string Decode(string s, bool plusAsSpace)
    {
        if (s.IndexOf('%') < 0 && !(plusAsSpace && s.IndexOf('+') >= 0)) return s;
        var bytes = new List<byte>();
        var output = new StringBuilder();
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '%')
            {
                if (i + 2 >= s.Length) throw new FormatException("URI malformed");
                int hi = Hex(s[i + 1]), lo = Hex(s[i + 2]);
                if (hi < 0 || lo < 0) throw new FormatException("URI malformed");
                bytes.Add((byte)((hi << 4) | lo));
                i += 2;
                continue;
            }
            if (bytes.Count > 0)
            {
                output.Append(StrictUtf8(bytes));
                bytes.Clear();
            }
            output.Append(plusAsSpace && c == '+' ? ' ' : c);
        }
        if (bytes.Count > 0) output.Append(StrictUtf8(bytes));
        return output.ToString();
    }

    private static string StrictUtf8(List<byte> bytes)
    {
        try
        {
            return new UTF8Encoding(false, true).GetString(bytes.ToArray());
        }
        catch (DecoderFallbackException)
        {
            throw new FormatException("URI malformed");
        }
    }

    /// <summary>
    /// Percent-decoding as <c>URLSearchParams</c> parses a query: <c>+</c> is a space and an invalid
    /// escape is kept literally rather than rejected.
    /// </summary>
    public static string DecodeFormLenient(string s)
    {
        var bytes = new List<byte>(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '%' && i + 2 < s.Length && Hex(s[i + 1]) >= 0 && Hex(s[i + 2]) >= 0)
            {
                bytes.Add((byte)((Hex(s[i + 1]) << 4) | Hex(s[i + 2])));
                i += 2;
            }
            else if (c == '+')
            {
                bytes.Add((byte)' ');
            }
            else
            {
                var len = char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]) ? 2 : 1;
                bytes.AddRange(Utf8(s.Substring(i, len)));
                i += len - 1;
            }
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    /// <summary><c>new Date().toISOString()</c>: UTC with millisecond precision, e.g. <c>2026-01-01T00:00:00.000Z</c>.</summary>
    public static string IsoTimestamp(DateTimeOffset instant) =>
        instant.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    /// <summary><c>String(value)</c> as JavaScript prints numbers and booleans.</summary>
    public static string JsString(object value) => value switch
    {
        bool b => b ? "true" : "false",
        string s => s,
        double d => JsNumber(d),
        float f => JsNumber(f),
        decimal m => m.ToString(CultureInfo.InvariantCulture).Contains('.')
            ? m.ToString(CultureInfo.InvariantCulture).TrimEnd('0').TrimEnd('.')
            : m.ToString(CultureInfo.InvariantCulture),
        DateTimeOffset dto => IsoTimestamp(dto),
        DateTime dt => IsoTimestamp(new DateTimeOffset(dt.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(dt, DateTimeKind.Utc) : dt)),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };

    private static string JsNumber(double d)
    {
        if (double.IsNaN(d)) return "NaN";
        if (double.IsPositiveInfinity(d)) return "Infinity";
        if (double.IsNegativeInfinity(d)) return "-Infinity";
        if (d == Math.Round(d) && Math.Abs(d) < 1e21) return ((decimal)d).ToString(CultureInfo.InvariantCulture);
        return d.ToString("R", CultureInfo.InvariantCulture);
    }

    /// <summary>Number of Unicode code points (what <c>Array.from(s).length</c> counts).</summary>
    public static int CodePointLength(string s)
    {
        var count = 0;
        for (var i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) i++;
            count++;
        }
        return count;
    }
}
