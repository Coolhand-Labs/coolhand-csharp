using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using CoolhandLabs.Internal;
using CoolhandLabs.Services;

namespace CoolhandLabs.Tests;

public class SanitizationTests
{
    private readonly PatternMatchingService _service = new(silent: true);

    private JsonNode? Body(string json) => _service.SanitizeBody(Json.Parse(json));

    private string Text(string raw) => Json.AsString(_service.SanitizeBody(JsonValue.Create(raw)))!;

    private static string At(JsonNode? node, params object[] path)
    {
        foreach (var step in path) node = step is int i ? node![i] : node![(string)step];
        return node is JsonValue v ? Json.ScalarText(v)! : Json.Stringify(node);
    }

    // ------------------------------------------------------------------ headers

    [Fact]
    public void RedactsDefaultCredentialHeadersAndLowercasesNames()
    {
        var output = _service.SanitizeHeaders(new Dictionary<string, string>
        {
            ["Authorization"] = "Bearer sk-live", ["X-API-Key"] = "k", ["Cookie"] = "c=1", ["Content-Type"] = "application/json",
        });
        Assert.Equal(new Dictionary<string, string>
        {
            ["authorization"] = "[REDACTED]", ["x-api-key"] = "[REDACTED]", ["cookie"] = "[REDACTED]", ["content-type"] = "application/json",
        }, output);
    }

    [Fact]
    public void RedactsCredentialLookingNamesButNotRateLimitTelemetry()
    {
        var output = _service.SanitizeHeaders(new Dictionary<string, string>
        {
            ["x-auth-token"] = "t", ["cf-access-client-secret"] = "s", ["x-csrf-token"] = "c",
            ["anthropic-ratelimit-tokens-remaining"] = "1000", ["x-request-id"] = "req_1", ["request-id"] = "req_2",
        });
        Assert.Equal("[REDACTED]", output["x-auth-token"]);
        Assert.Equal("[REDACTED]", output["cf-access-client-secret"]);
        Assert.Equal("[REDACTED]", output["x-csrf-token"]);
        Assert.Equal("1000", output["anthropic-ratelimit-tokens-remaining"]);
        Assert.Equal("req_1", output["x-request-id"]);
        Assert.Equal("req_2", output["request-id"]);
    }

    [Fact]
    public void JoinsRepeatedValuesAcrossCasesOfOneName()
    {
        var output = _service.SanitizeHeaders(new[]
        {
            new KeyValuePair<string, IEnumerable<string>>("Accept", new[] { "a", "b" }),
            new KeyValuePair<string, IEnumerable<string>>("accept", new[] { "c" }),
            new KeyValuePair<string, IEnumerable<string>>("X-One", new[] { "1" }),
        });
        Assert.Equal("a, b, c", output["accept"]);
        Assert.Equal("1", output["x-one"]);
    }

    [Fact]
    public void AppliesPatternSpecificRulesToPresentNonEmptyHeaders()
    {
        var pattern = new ApiPattern(null, "P", new[] { "x" }, Headers: new Dictionary<string, string> { ["X-Custom"] = "[HIDDEN]", ["absent"] = "[HIDDEN]", ["empty"] = "[HIDDEN]" });
        var output = _service.SanitizeHeaders(new Dictionary<string, string> { ["x-custom"] = "v", ["empty"] = "" }, pattern);
        Assert.Equal("[HIDDEN]", output["x-custom"]);
        Assert.Equal("", output["empty"]);
        Assert.False(output.ContainsKey("absent"));
    }

    // ------------------------------------------------------------------ URLs

    [Theory]
    [InlineData("https://generativelanguage.googleapis.com/v1/models/g:generateContent?key=AIza123&alt=sse",
        "https://generativelanguage.googleapis.com/v1/models/g:generateContent?key=%5BREDACTED%5D&alt=sse")]
    [InlineData("https://x.example/p?X-Amz-Signature=abc&ok=1", "https://x.example/p?X-Amz-Signature=%5BREDACTED%5D&ok=1")]
    [InlineData("https://x.example/p?api-Key=1", "https://x.example/p?api-Key=%5BREDACTED%5D")]
    [InlineData("https://x.example/p?oauth_token=1&passwd=2&secret_access_key=3",
        "https://x.example/p?oauth_token=%5BREDACTED%5D&passwd=%5BREDACTED%5D&secret_access_key=%5BREDACTED%5D")]
    [InlineData("https://user:pass@host.example/v1", "https://host.example/v1")]
    public void RedactsCredentialQueryParametersAndUserinfo(string input, string expected) =>
        Assert.Equal(expected, _service.SanitizeUrl(input));

    [Theory]
    [InlineData("https://API.example.com/v1/x?model=a%20b&z=1")]
    [InlineData("not a url")]
    [InlineData("https://x.example/p?")]
    public void LeavesUrlsWithNothingToRedactUntouched(string url) => Assert.Equal(url, _service.SanitizeUrl(url));

    // ------------------------------------------------------------------ JSON bodies

    [Fact]
    public void RedactsAzureOnYourDataCredentials()
    {
        var output = Body("{\"messages\":[{\"role\":\"user\",\"content\":\"hello\"}],\"data_sources\":[{\"type\":\"azure_search\","
                          + "\"parameters\":{\"authentication\":{\"type\":\"api_key\",\"key\":\"live\"},\"connectionString\":\"AccountKey=x\","
                          + "\"encoded_api_key\":\"e\"}}]}");
        Assert.Equal("[REDACTED]", At(output, "data_sources", 0, "parameters", "authentication", "key"));
        Assert.Equal("[REDACTED]", At(output, "data_sources", 0, "parameters", "connectionString"));
        Assert.Equal("[REDACTED]", At(output, "data_sources", 0, "parameters", "encoded_api_key"));
        Assert.Equal("hello", At(output, "messages", 0, "content"));
    }

    [Fact]
    public void LeavesMessageContentAndToolSchemasAlone()
    {
        const string json = "{\"messages\":[{\"role\":\"user\",\"content\":\"what is my api_key?\"}],\"tools\":[{\"type\":\"function\","
                            + "\"function\":{\"name\":\"get_key\",\"parameters\":{\"key\":{\"type\":\"string\"},\"password\":{\"type\":\"string\"},"
                            + "\"headers\":\"ok\",\"authorization\":\"ok\"}}}],\"pwd\":\"/home/me\"}";
        Assert.Equal(json, Json.Stringify(Body(json)));
    }

    [Fact]
    public void RedactsMcpAndRealtimeCredentials()
    {
        var output = Body("{\"mcp_servers\":[{\"type\":\"url\",\"url\":\"https://mcp.example.com\",\"authorization_token\":\"t\"}],"
                          + "\"tools\":[{\"type\":\"mcp\",\"server_label\":\"d\",\"authorization\":\"Bearer a\",\"headers\":{\"X-Api-Key\":\"a\"}}],"
                          + "\"client_secret\":{\"value\":\"ek\"}}");
        Assert.Equal("[REDACTED]", At(output, "mcp_servers", 0, "authorization_token"));
        Assert.Equal("https://mcp.example.com", At(output, "mcp_servers", 0, "url"));
        Assert.Equal("[REDACTED]", At(output, "tools", 0, "authorization"));
        Assert.Equal("[REDACTED]", At(output, "tools", 0, "headers"));
        Assert.Equal("d", At(output, "tools", 0, "server_label"));
        Assert.Equal("[REDACTED]", At(output, "client_secret"));
    }

    [Fact]
    public void RedactsGenericCredentialStringsAnywhere()
    {
        var output = Body("{\"user\":{\"password\":\"hunter2\",\"accessToken\":\"a\",\"refresh-token\":\"r\",\"secrets\":[\"x\"],"
                          + "\"API_KEY\":[1,\"k\"],\"user[password]\":\"p\",\"token\":\"logprob\",\"GITHUB_TOKEN\":\"g\",\"apiSecret\":\"s\"}}");
        Assert.Equal("[REDACTED]", At(output, "user", "password"));
        Assert.Equal("[REDACTED]", At(output, "user", "accessToken"));
        Assert.Equal("[REDACTED]", At(output, "user", "refresh-token"));
        Assert.Equal("[REDACTED]", At(output, "user", "API_KEY"));
        Assert.Equal("[REDACTED]", At(output, "user", "user[password]"));
        Assert.Equal("[REDACTED]", At(output, "user", "GITHUB_TOKEN"));
        Assert.Equal("[REDACTED]", At(output, "user", "apiSecret"));
        Assert.Equal("x", At(output, "user", "secrets", 0));
        Assert.Equal("logprob", At(output, "user", "token"));
    }

    [Fact]
    public void KeepsNumbersExactlyAsWritten()
    {
        const string json = "{\"temperature\":0.70,\"big\":12345678901234567890,\"e\":1e3}";
        Assert.Equal(json, Json.Stringify(Body(json)));
    }

    [Fact]
    public void ReplacesSubtreesNestedTooDeeply()
    {
        var json = string.Concat(Enumerable.Repeat("{\"a\":", 70)) + "1" + new string('}', 70);
        Assert.Contains("[REDACTED: nested too deeply]", Json.Stringify(Body(json)));
    }

    [Fact]
    public void PassesScalarsAndNullThrough()
    {
        Assert.Null(_service.SanitizeBody(null));
        Assert.Equal("42", Json.Stringify(Body("42")));
        Assert.Equal("plain text body", Text("plain text body"));
        Assert.Equal("", Text(""));
    }

    [Fact]
    public void DoesNotMutateItsInput()
    {
        var input = JsonNode.Parse("{\"password\":\"p\"}")!;
        _service.SanitizeBody(input);
        Assert.Equal("p", At(input, "password"));
    }

    // ------------------------------------------------------------------ string bodies

    [Fact]
    public void RedactsJsonCarriedAsAString() =>
        Assert.Equal("{\"api_key\":\"[REDACTED]\",\"a\":1}", Text("{\"api_key\":\"sk\",\"a\":1}"));

    [Fact]
    public void RedactsNdjson()
    {
        var output = Text("{\"data_sources\":[{\"parameters\":{\"key\":\"live-key\"}}]}\n{\"ok\":true}");
        Assert.DoesNotContain("live-key", output);
        Assert.Equal(2, output.Split('\n').Length);
    }

    [Fact]
    public void RedactsABomPrefixedDocument() =>
        Assert.DoesNotContain("\"tok\"", Text("\uFEFF{\"mcp_servers\":[{\"authorization_token\":\"tok\"}]}"));

    [Fact]
    public void ScrubsCredentialValuesFromATruncatedBody()
    {
        var output = Text("{\"data_sources\":[{\"parameters\":{\"key\":\"live-key\",\"endpoint\":\"https://x\"}}],\"messages\":[{\"content\":\"trunc");
        Assert.DoesNotContain("live-key", output);
        Assert.Contains("\"key\":\"[REDACTED]\"", output);
        Assert.Contains("https://x", output);
    }

    [Theory]
    [InlineData("say \"hi {\"password\":\"hunter2\"}")]
    [InlineData("{\"a\":\"b\"}\n{\"x\":\"unterminated, {\"password\":\"hunter2\"}")]
    [InlineData("\"stray quote {\"api_key\": \"hunter2\", \"other\": \"ok\"} tail\"")]
    [InlineData("{\"api\\u005fkey\":\"hunter2\", \"x\":")]
    [InlineData("{\"pass\\u0077ord\":\"hunter2\", \"x\":")]
    [InlineData("{\"a\":\"b\",\"password\":\"hunter2-trunc")]
    [InlineData("{\"connection_string\":\"hunter2\",")]
    public void RedactsAroundUnbalancedQuotesAndEscapes(string raw)
    {
        var output = Text(raw);
        Assert.DoesNotContain("hunter2", output);
        Assert.Contains("[REDACTED]", output);
    }

    [Fact]
    public void KeepsNonCredentialValues()
    {
        var output = Text("{\"token\":\"aaa\",\"note\":\"keep me\",\"client_secret\":\"bbb\" ,");
        Assert.DoesNotContain("aaa", output);
        Assert.DoesNotContain("bbb", output);
        Assert.Contains("keep me", output);
    }

    [Fact]
    public void ScansAdversarialBodiesInLinearTime()
    {
        var watch = Stopwatch.StartNew();
        Text("\"" + string.Concat(Enumerable.Repeat("keykey", 400_000)) + " {");
        Text(string.Concat(Enumerable.Repeat("key", 800_000)) + " {");
        Text(string.Concat(Enumerable.Repeat("\"key ", 500_000)) + " {");
        Assert.DoesNotContain("hunter2", Text("[\"" + string.Concat(Enumerable.Repeat("\\\"a", 120_000)) + "\", {\"password\":\"hunter2\"} "));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), "took " + watch.Elapsed);
    }

    [Fact]
    public void RedactsFormEncodedCredentials()
    {
        Assert.Equal("grant_type=refresh_token&refresh_token=[REDACTED]&client_id=abc&token=[REDACTED]&user%5Bpassword%5D=[REDACTED]"
                     + "&client_assertion=[REDACTED]",
            Text("grant_type=refresh_token&refresh_token=r1&client_id=abc&token=t&user%5Bpassword%5D=p&client_assertion=x"));
        Assert.Equal("a=1&b=2", Text("a=1&b=2"));
        Assert.Equal("pwd=/home", Text("pwd=/home"));
        Assert.Equal("bad%zzname=1&password=[REDACTED]", Text("bad%zzname=1&password=p"));
    }

    // ------------------------------------------------------------------ multipart

    private static string Multipart(string eol, string boundary, bool close, params string[][] parts)
    {
        var output = new StringBuilder();
        foreach (var p in parts)
        {
            output.Append("--").Append(boundary).Append(eol).Append("Content-Disposition: form-data; name=\"").Append(p[0]).Append('"');
            if (p.Length > 2) output.Append("; filename=\"").Append(p[2]).Append('"');
            output.Append(eol).Append(eol).Append(p[1]).Append(eol);
        }
        if (close) output.Append("--").Append(boundary).Append("--").Append(eol);
        return output.ToString();
    }

    [Fact]
    public void RedactsCredentialPartsAndKeepsTheRestVerbatim()
    {
        var input = Multipart("\r\n", "XBOUNDARYX", true, new[] { "model", "whisper-1" }, new[] { "api_key", "sk-SECRET123" },
            new[] { "file", "audio-bytes", "a.wav" });
        var expected = Multipart("\r\n", "XBOUNDARYX", true, new[] { "model", "whisper-1" }, new[] { "api_key", "[REDACTED]" },
            new[] { "file", "audio-bytes", "a.wav" });
        Assert.Equal(expected, Text(input));
    }

    [Fact]
    public void HandlesLfLineEndings() =>
        Assert.Equal(Multipart("\n", "B", true, new[] { "password", "[REDACTED]" }, new[] { "note", "ok" }),
            Text(Multipart("\n", "B", true, new[] { "password", "hunter2" }, new[] { "note", "ok" })));

    [Fact]
    public void RedactsUnquotedBracketedAndBareTokenNames()
    {
        var input = string.Join("\r\n", "--B", "Content-Disposition: form-data; name=client_secret", "", "cs-live",
            "--B", "Content-Disposition: form-data; name=\"user[password]\"", "", "hunter2",
            "--B", "Content-Disposition: form-data; name=\"token\"", "", "tk-live",
            "--B", "Content-Disposition: form-data; name=\"scope\"", "", "read", "--B--", "");
        var output = Text(input);
        Assert.DoesNotContain("cs-live", output);
        Assert.DoesNotContain("hunter2", output);
        Assert.DoesNotContain("tk-live", output);
        Assert.Contains("read", output);
        Assert.Equal(3, output.Split("[REDACTED]").Length - 1);
    }

    [Fact]
    public void UsesThePartNameNotTheFilename()
    {
        var output = Text(Multipart("\r\n", "B", true, new[] { "upload", "file-body", "api_key.txt" }, new[] { "secret_key", "file-secret", "k.pem" }));
        Assert.Contains("file-body", output);
        Assert.DoesNotContain("file-secret", output);
        Assert.DoesNotContain("SECRET",
            Text("--B\r\nContent-Disposition: form-data; filename=\"x;name=foo\"; name=\"password\"\r\n\r\nSECRET\r\n--B--\r\n"));
    }

    [Fact]
    public void HandlesRfc5987Names() =>
        Assert.DoesNotContain("SECRET", Text("--B\r\nContent-Disposition: form-data; name*=UTF-8''pass%77ord\r\n\r\nSECRET\r\n--B--\r\n"));

    [Fact]
    public void RedactsFormEncodedValuesInsideAPart()
    {
        var output = Text(Multipart("\r\n", "B", true, new[] { "payload", "a=1&password=hunter2" }));
        Assert.Contains("a=1&password=[REDACTED]", output);
    }

    [Fact]
    public void RedactsATruncatedCredentialPart()
    {
        var truncated = Multipart("\r\n", "XBOUNDARYX", false, new[] { "model", "m" }, new[] { "api_key", "sk-SECRET123" });
        truncated = truncated.Substring(0, truncated.Length - 4);
        Assert.DoesNotContain("sk-SECRET", Text(truncated));
    }

    [Fact]
    public void ReturnsNullForTextThatIsNotMultipart() => Assert.Null(PatternMatchingService.RedactMultipartFields("hello"));
}
