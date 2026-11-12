using System.IO.Compression;
using System.Text;
using CoolhandLabs.Internal;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace CoolhandLabs.Tests;

public class JsTextTests
{
    [Theory]
    [InlineData("  a b \t\n", "a b")]
    [InlineData("\u00A0\uFEFFx\u2028", "x")]
    [InlineData("\u200Ax\u3000", "x")]
    [InlineData("", "")]
    public void TrimFollowsStringPrototypeTrim(string input, string expected) => Assert.Equal(expected, JsText.Trim(input));

    [Fact]
    public void EncodeUriComponentKeepsTheJavaScriptSafeSet()
    {
        Assert.Equal("a%20b%2Fc!~*'()-_.", JsText.EncodeUriComponent("a b/c!~*'()-_."));
        Assert.Equal("%C3%A9%F0%9F%98%80", JsText.EncodeUriComponent("é😀"));
    }

    [Fact]
    public void FormEncodeMatchesUrlSearchParams()
    {
        Assert.Equal("a+b%21%27%28%29*-._%7E", JsText.FormEncode("a b!'()*-._~"));
        Assert.Equal("q%5Bs%5D", JsText.FormEncode("q[s]"));
    }

    [Fact]
    public void DecodeRejectsMalformedEscapes()
    {
        Assert.Equal("a b", JsText.Decode("a%20b", false));
        Assert.Equal("a b", JsText.Decode("a+b", true));
        Assert.Equal("a+b", JsText.Decode("a+b", false));
        Assert.Throws<FormatException>(() => JsText.Decode("%zz", false));
        Assert.Throws<FormatException>(() => JsText.Decode("%e9", false)); // invalid UTF-8
        Assert.Throws<FormatException>(() => JsText.Decode("abc%2", false));
    }

    [Fact]
    public void DecodeFormLenientKeepsBadEscapes()
    {
        Assert.Equal("100% sure", JsText.DecodeFormLenient("100%+sure"));
        Assert.Equal("é", JsText.DecodeFormLenient("%C3%A9"));
    }

    [Fact]
    public void IsoTimestampHasMilliseconds() =>
        Assert.Equal("2026-01-02T03:04:05.006Z", JsText.IsoTimestamp(new DateTimeOffset(2026, 1, 2, 3, 4, 5, 6, TimeSpan.Zero)));

    [Fact]
    public void JsStringPrintsNumbersAndBooleansLikeJavaScript()
    {
        Assert.Equal("true", JsText.JsString(true));
        Assert.Equal("2", JsText.JsString(2.0));
        Assert.Equal("0.5", JsText.JsString(0.5));
        Assert.Equal("0.5", JsText.JsString(0.50m));
        Assert.Equal("3", JsText.JsString(3.00m));
        Assert.Equal("42", JsText.JsString(42L));
    }

    [Fact]
    public void CodePointLengthCountsSurrogatePairsOnce() => Assert.Equal(2, JsText.CodePointLength("a😀"));
}

public class UrlPartsTests
{
    [Fact]
    public void ParsesLikeTheWhatwgUrlClass()
    {
        var u = UrlParts.Parse("HTTPS://User:Pw@API.Example.com:443/v1/../v2/./x?q=1#frag")!;
        Assert.Equal("https", u.Scheme);
        Assert.Equal("User:Pw", u.Userinfo);
        Assert.Equal("api.example.com", u.Hostname);
        Assert.Null(u.Port);
        Assert.Equal("/v2/x", u.Path);
        Assert.Equal("q=1", u.Query);
        Assert.Equal("frag", u.Fragment);
        Assert.Equal("https://api.example.com/v2/x?q=1#frag", u.Href(dropUserinfo: true));
    }

    [Fact]
    public void KeepsNonDefaultPortsAndIpv6Brackets()
    {
        var u = UrlParts.Parse("http://[::1]:11434/api/chat")!;
        Assert.Equal("[::1]", u.Hostname);
        Assert.Equal(11434, u.Port);
        Assert.Equal("11434", u.EffectivePort);
        Assert.Equal("443", UrlParts.Parse("https://x.example")!.EffectivePort);
        Assert.Equal("/", UrlParts.Parse("https://x.example")!.Path);
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("/relative/path")]
    [InlineData("https://")]
    [InlineData("https://host:99999/")]
    [InlineData("https://ho st/")]
    [InlineData(null)]
    public void RejectsWhatIsNotAnAbsoluteUrl(string? url) => Assert.Null(UrlParts.Parse(url));

    [Fact]
    public void AcceptsCharactersUriWouldReject()
    {
        var u = UrlParts.Parse("https://x.example/a b|c?x=[1]")!;
        Assert.Equal("/a b|c", u.Path);
        Assert.Equal("x=[1]", u.Query);
    }

    [Theory]
    [InlineData("/a/%2e%2E/b", "/b")]
    [InlineData("/a/b/..", "/a/")]
    [InlineData("/a/./b/.", "/a/b/")]
    [InlineData("/../../a", "/a")]
    public void RemovesDotSegments(string path, string expected) => Assert.Equal(expected, UrlParts.RemoveDotSegments(path));
}

public class UrlFiltersTests
{
    [Fact]
    public void ExcludePatternsAreSubstringsOfTheUrlOrItsCanonicalForm()
    {
        var patterns = new[] { "internal.corp" };
        Assert.True(UrlFilters.MatchesExcludePattern("https://internal.corp/v1", patterns));
        Assert.True(UrlFilters.MatchesExcludePattern("https://Internal.Corp/v1", patterns));
        Assert.True(UrlFilters.MatchesExcludePattern("https://internal.corp./", patterns));
        Assert.True(UrlFilters.MatchesExcludePattern("https://example.com", new[] { "example.com/" }));
        Assert.False(UrlFilters.MatchesExcludePattern("https://other.corp/v1", patterns));
    }

    [Fact]
    public void EmptyAndNullEntriesNeverMatchEverything()
    {
        Assert.False(UrlFilters.MatchesExcludePattern("https://x.example/", new string?[] { "", null }));
        Assert.False(UrlFilters.MatchesExcludePattern("https://x.example/", null));
        Assert.False(UrlFilters.MatchesExcludePattern("https://x.example/", Array.Empty<string>()));
    }

    [Fact]
    public void GlobsMatchOnlyLiterally()
    {
        Assert.False(UrlFilters.MatchesExcludePattern("https://x.example/internal-a", new[] { "/internal-*" }));
        Assert.True(UrlFilters.MatchesExcludePattern("https://x.example/internal-*", new[] { "/internal-*" }));
    }

    [Fact]
    public void WarnsAboutGlobLikeAndNullEntriesEvenWhenSilent()
    {
        using var logs = new Support.LogCapture();
        UrlFilters.WarnOnGlobLikePatterns(new[] { "/internal-*", null, "fine" });
        Assert.Equal(2, logs.Messages.Count(m => m.Level == CoolhandLogLevel.Warning));
        Assert.Contains("contains \"*\"", logs.All);
    }

    [Fact]
    public void SelfEndpointMatchesHostAndEffectivePort()
    {
        var self = UrlFilters.ComputeSelfEndpoint("https://CoolhandLabs.com/api/v2/llm_request_logs");
        Assert.True(UrlFilters.IsSelfEndpointUrl("https://coolhandlabs.com:443/anything", self));
        Assert.True(UrlFilters.IsSelfEndpointUrl("https://coolhandlabs.com./x", self));
        Assert.False(UrlFilters.IsSelfEndpointUrl("https://coolhandlabs.com:8443/x", self));
        Assert.False(UrlFilters.IsSelfEndpointUrl("https://api.coolhandlabs.com/x", self));
        Assert.False(UrlFilters.IsSelfEndpointUrl("https://x.example/", null));
    }

    [Theory]
    [InlineData("https://api.anthropic.com/api/event_logging/batch", "POST", true)]
    [InlineData("https://api.anthropic.com/api/directory/servers?limit=5", "GET", true)]
    [InlineData("https://api.anthropic.com/api/directory/servers", "POST", false)]
    [InlineData("https://api.anthropic.com/v1/environments/env_1/work/poll", "get", true)]
    [InlineData("https://api.anthropic.com/v1/messages", "POST", false)]
    [InlineData("https://other.example/api/event_logging/batch", "POST", false)]
    public void NonInferenceAnthropicEndpointsAreSkipped(string url, string method, bool expected) =>
        Assert.Equal(expected, UrlFilters.IsNonInferenceUrl(url, method));
}

public class EntityKeysTests
{
    [Theory]
    [InlineData("qa/test-plans", null)]
    [InlineData("", "must not be blank")]
    [InlineData("   ", "must not be blank")]
    [InlineData(" lead", "must not have leading or trailing whitespace")]
    [InlineData("/lead", "must not start or end with \"/\"")]
    [InlineData("trail/", "must not start or end with \"/\"")]
    [InlineData("a//b", "must not contain \"//\"")]
    [InlineData("a\u0000b", "must not contain control or invisible characters")]
    [InlineData("a\u200Bb", "must not contain control or invisible characters")]
    [InlineData("a\u2028b", "must not contain control or invisible characters")]
    public void KeyRules(string key, string? problem) => Assert.Equal(problem, EntityKeys.KeyProblem(key));

    [Fact]
    public void LoneSurrogatesAreRejected()
    {
        Assert.Equal("must be valid Unicode (contains a lone surrogate)", EntityKeys.KeyProblem("a" + (char)0xD800 + "b"));
        Assert.Equal("must be valid Unicode (contains a lone surrogate)", EntityKeys.KeyProblem("a" + (char)0xDC00));
    }

    [Fact]
    public void LengthsCountCodePoints()
    {
        Assert.Null(EntityKeys.KeyProblem(string.Concat(Enumerable.Repeat("😀", 255))));
        Assert.Equal("must be at most 255 characters", EntityKeys.KeyProblem(new string('a', 256)));
        Assert.Null(EntityKeys.DescriptionProblem(new string('a', 1000)));
        Assert.Equal("must be at most 1000 characters", EntityKeys.DescriptionProblem(new string('a', 1001)));
    }
}

public class CappedBufferTests
{
    [Fact]
    public void TruncatesAtTheCapAndReportsOnce()
    {
        var calls = 0;
        var buffer = new CappedBuffer(5, () => calls++);
        buffer.Push(Encoding.ASCII.GetBytes("abc"));
        buffer.Push(Encoding.ASCII.GetBytes("defg"));
        buffer.Push(Encoding.ASCII.GetBytes("h"));
        Assert.Equal("abcde", Encoding.ASCII.GetString(buffer.ToArray()));
        Assert.True(buffer.IsTruncated);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void EmptyChunksNeverTruncate()
    {
        var buffer = new CappedBuffer(0);
        buffer.Push(Array.Empty<byte>());
        Assert.False(buffer.IsTruncated);
        Assert.Equal(0, buffer.Size);
    }

    [Fact]
    public void ACallbackThatThrowsIsIgnored()
    {
        var buffer = new CappedBuffer(1, () => throw new InvalidOperationException("boom"));
        buffer.Push(new byte[] { 1, 2 });
        Assert.True(buffer.IsTruncated);
    }
}

public class DecompressTests
{
    private static byte[] Compress(string text, Func<Stream, Stream> wrap)
    {
        var output = new MemoryStream();
        using (var z = wrap(output))
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            z.Write(bytes, 0, bytes.Length);
        }
        return output.ToArray();
    }

    [Theory]
    [InlineData("gzip")]
    [InlineData("x-gzip")]
    [InlineData(" GZIP ")]
    [InlineData("deflate")]
    [InlineData("br")]
    public void DecodesKnownEncodings(string encoding)
    {
        var name = encoding.Trim().ToLowerInvariant();
        Func<Stream, Stream> wrap = name switch
        {
            "deflate" => s => new ZLibStream(s, CompressionLevel.Fastest),
            "br" => s => new BrotliStream(s, CompressionLevel.Fastest),
            _ => s => new GZipStream(s, CompressionLevel.Fastest),
        };
        Assert.Equal("{\"ok\":true}", Decompress.Run(Compress("{\"ok\":true}", wrap), encoding, null, false));
    }

    [Fact]
    public void RawDeflateIsTheFallback() =>
        Assert.Equal("raw", Decompress.Run(Compress("raw", s => new DeflateStream(s, CompressionLevel.Fastest)), "deflate", null, false));

    [Fact]
    public void BadDataWarnsAndFallsBackToText()
    {
        var warnings = new List<string>();
        Assert.Equal("not gzip", Decompress.Run(Encoding.UTF8.GetBytes("not gzip"), "gzip", warnings.Add, false));
        Assert.Single(warnings);
    }

    [Fact]
    public void UnknownEncodingsAreText() => Assert.Equal("zz", Decompress.Run(Encoding.UTF8.GetBytes("zz"), "zstd", null, false));

    [Fact]
    public void APartialBodyDecodesAsFarAsTheBytesAllow()
    {
        var text = string.Concat(Enumerable.Range(0, 2000).Select(i => "data: chunk " + i + "\n"));
        var gz = Compress(text, s => new GZipStream(s, CompressionLevel.Fastest));
        var partial = gz.Take(gz.Length / 2).ToArray();
        var decoded = Decompress.Run(partial, "gzip", null, partial: true);
        Assert.StartsWith("data: chunk 0\n", decoded);
        Assert.True(decoded.Length < text.Length);
    }
}

public class BodyParserTests
{
    [Fact]
    public void ParsesJsonAndKeepsTextAsText()
    {
        Assert.Null(BodyParser.ParseBody(null));
        Assert.Null(BodyParser.ParseBody(""));
        Assert.Equal("{\"a\":1}", Json.Stringify(BodyParser.ParseBody("{\"a\":1}")));
        Assert.Equal("plain", Json.AsString(BodyParser.ParseBody("plain")));
        Assert.Equal("data: x\n\n", Json.AsString(BodyParser.ParseBody("data: x\n\n")));
    }

    [Fact]
    public void ATopLevelArrayBecomesNdjsonText() =>
        Assert.Equal("{\"a\":1}\n2", Json.AsString(BodyParser.ParseBody("[{\"a\":1},2]")));

    [Fact]
    public void DuplicateKeysFallBackToText() =>
        Assert.Equal("{\"a\":1,\"a\":2}", Json.AsString(BodyParser.ParseBody("{\"a\":1,\"a\":2}")));

    [Fact]
    public void ParseAndSanitizeFailsClosed()
    {
        Exception? seen = null;
        Assert.Null(BodyParser.ParseAndSanitize("{}", _ => throw new InvalidOperationException("x"), e => seen = e));
        Assert.IsType<InvalidOperationException>(seen);
    }
}
