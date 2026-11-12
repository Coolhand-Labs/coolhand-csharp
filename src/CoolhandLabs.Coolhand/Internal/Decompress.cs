using System.IO.Compression;
using System.Text;

namespace CoolhandLabs.Internal;

/// <summary>
/// Decodes a captured response body according to its <c>Content-Encoding</c> for logging: gzip,
/// x-gzip, deflate (zlib-wrapped, falling back to raw deflate) and br (Brotli). Unknown or absent
/// encodings are returned as UTF-8 text unchanged. Output is capped at <see cref="CappedBuffer.MaxBytes"/>.
/// </summary>
internal static class Decompress
{
    /// <param name="buffer">The captured bytes.</param>
    /// <param name="encoding">The response's <c>Content-Encoding</c>, or null.</param>
    /// <param name="onWarn">Receives a message when decoding fails and the raw bytes are used instead.</param>
    /// <param name="partial">The bytes are a prefix of the body (an abandoned stream): decode as much as the
    /// bytes allow instead of failing on the missing end and logging the compressed bytes as text.</param>
    public static string Run(byte[] buffer, string? encoding, Action<string>? onWarn, bool partial)
    {
        if (string.IsNullOrEmpty(encoding)) return Utf8(buffer);
        var enc = JsText.Trim(encoding).ToLowerInvariant();
        switch (enc)
        {
            case "gzip":
            case "x-gzip":
                return Decode(buffer, enc, partial, onWarn, s => new GZipStream(s, CompressionMode.Decompress));
            case "deflate":
                return TryDecode(buffer, partial, s => new ZLibStream(s, CompressionMode.Decompress))
                       ?? Decode(buffer, enc, partial, onWarn, s => new DeflateStream(s, CompressionMode.Decompress));
            case "br":
                return Decode(buffer, enc, partial, onWarn, s => new BrotliStream(s, CompressionMode.Decompress));
            default:
                return Utf8(buffer);
        }
    }

    private static string Decode(byte[] buffer, string enc, bool partial, Action<string>? onWarn, Func<Stream, Stream> open)
    {
        var output = TryDecode(buffer, partial, open);
        if (output is not null) return output;
        onWarn?.Invoke("⚠️ Decompression failed for encoding '" + enc + "'");
        return Utf8(buffer);
    }

    /// <summary>Decoded text, or null on failure (with <paramref name="partial"/>, whatever decoded before the failure counts).</summary>
    private static string? TryDecode(byte[] buffer, bool partial, Func<Stream, Stream> open)
    {
        var output = new MemoryStream();
        var chunk = new byte[16 * 1024];
        try
        {
            using var stream = open(new MemoryStream(buffer, writable: false));
            int n;
            while ((n = stream.Read(chunk, 0, chunk.Length)) > 0)
            {
                var room = CappedBuffer.MaxBytes - (int)output.Length;
                if (n >= room)
                {
                    output.Write(chunk, 0, room);
                    break;
                }
                output.Write(chunk, 0, n);
            }
            return Utf8(output.ToArray());
        }
        catch (Exception e) when (e is InvalidDataException or IOException or InvalidOperationException)
        {
            if (partial && output.Length > 0) return Utf8(output.ToArray());
            return null;
        }
    }

    private static string Utf8(byte[] bytes) => Encoding.UTF8.GetString(bytes);
}
