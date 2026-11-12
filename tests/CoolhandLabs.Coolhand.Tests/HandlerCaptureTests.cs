using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using CoolhandLabs.Http;
using CoolhandLabs.Tests.Support;

namespace CoolhandLabs.Tests;

public class HandlerCaptureTests
{
    private const string OpenAiUrl = "https://api.openai.com/v1/chat/completions?api-key=sk-query";

    private static StringContent JsonContent(string json) => new(json, Encoding.UTF8, "application/json");

    [Fact]
    public async Task CapturesAnLlmCallAndLeavesItUntouched()
    {
        await using var coolhand = new FakeCoolhand();
        var provider = new FakeHandler
        {
            Responder = (_, _) => FakeHandler.Json(200, "{\"id\":\"chatcmpl-1\",\"choices\":[]}", ("x-request-id", "req_123"), ("set-cookie", "s=1")),
        };
        using var http = coolhand.Http(provider);
        var request = new HttpRequestMessage(HttpMethod.Post, OpenAiUrl)
        {
            Content = JsonContent("{\"model\":\"gpt-4o\",\"api_key\":\"sk-body\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}"),
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "sk-header");
        using var response = await http.SendAsync(request);

        Assert.Equal("{\"id\":\"chatcmpl-1\",\"choices\":[]}", await response.Content.ReadAsStringAsync());
        Assert.Equal("req_123", response.Headers.GetValues("x-request-id").Single());
        Assert.Contains("sk-body", provider.Requests.Single().Body);  // the provider got the real body
        Assert.Equal("Bearer sk-header", provider.Requests.Single().Header("authorization"));

        var log = (await coolhand.WaitForLogsAsync(1)).Single();
        var raw = (JsonObject)log["raw_request"]!;
        Assert.Equal("https://api.openai.com/v1/chat/completions?api-key=%5BREDACTED%5D", raw["url"]!.GetValue<string>());
        Assert.Equal("POST", raw["method"]!.GetValue<string>());
        Assert.Equal("https", raw["protocol"]!.GetValue<string>());
        Assert.Equal(200, raw["status_code"]!.GetValue<int>());
        Assert.Equal(1, raw["id"]!.GetValue<long>());
        Assert.Equal("[REDACTED]", raw["headers"]!["authorization"]!.GetValue<string>());
        Assert.Equal("application/json; charset=utf-8", raw["headers"]!["content-type"]!.GetValue<string>());
        Assert.Equal("[REDACTED]", raw["request_body"]!["api_key"]!.GetValue<string>());
        Assert.Equal("gpt-4o", raw["request_body"]!["model"]!.GetValue<string>());
        Assert.Equal("chatcmpl-1", raw["response_body"]!["id"]!.GetValue<string>());
        Assert.Equal("req_123", raw["response_headers"]!["x-request-id"]!.GetValue<string>());
        Assert.Equal("[REDACTED]", raw["response_headers"]!["set-cookie"]!.GetValue<string>());
        Assert.False(raw.ContainsKey("stream_incomplete"));
        Assert.EndsWith("-global-monitoring", log["collector"]!.GetValue<string>());
        Assert.False(log.ContainsKey("declared"));
        Assert.DoesNotContain("sk-body", coolhand.Api.Requests.Single().Body);
        Assert.DoesNotContain("sk-header", coolhand.Api.Requests.Single().Body);
        Assert.DoesNotContain("sk-query", coolhand.Api.Requests.Single().Body);
        Assert.Equal(new CoolhandStats(1, 1, FakeCoolhand.BaseUrl + "/api/v2/llm_request_logs"), coolhand.Client.GetStats());
    }

    [Fact]
    public async Task SnapshotsTheDeclaredContextWhenTheRequestStarts()
    {
        await using var coolhand = new FakeCoolhand();
        using var http = coolhand.Http(new FakeHandler());
        var ctx = new DeclaredContext { Reporter = "qa-agent" }.WithWorkload("qa/plans", "QA").WithTemplate("qa/plans/generate", "generate");
        await Coolhand.WithContextAsync(ctx, async () =>
        {
            await Task.Yield();
            using var r = await http.PostAsync("https://api.anthropic.com/v1/messages", JsonContent("{}"));
            return r.StatusCode;
        });
        var log = (await coolhand.WaitForLogsAsync(1)).Single();
        Assert.Equal("{\"reporter\":\"qa-agent\",\"workload\":{\"key\":\"qa/plans\",\"name\":\"QA\"},\"template\":{\"key\":\"qa/plans/generate\",\"name\":\"generate\"}}",
            log["declared"]!.ToJsonString());
    }

    [Theory]
    [InlineData("https://example.com/v1/chat/completions")]                     // not an LLM host
    [InlineData("https://myres.openai.azure.com/openai/files")]                 // default exclusion
    [InlineData("https://api.anthropic.com/api/event_logging/batch")]           // non-inference
    [InlineData("https://coolhand.test/api/v2/llm_request_logs")]              // the Coolhand API itself
    public async Task PassesOtherRequestsStraightThrough(string url)
    {
        await using var coolhand = new FakeCoolhand();
        var provider = new FakeHandler();
        using var http = coolhand.Http(provider);
        using var response = await http.PostAsync(url, JsonContent("{\"a\":1}"));
        Assert.Equal("{\"a\":1}", provider.Requests.Single().Body);
        await coolhand.Client.FlushAsync(TimeSpan.FromSeconds(2));
        Assert.Empty(coolhand.Logs);
        Assert.Equal(1, coolhand.Client.GetStats().TotalRequests);
        Assert.Equal(0, coolhand.Client.GetStats().InterceptedCalls);
    }

    [Fact]
    public async Task CustomExclusionsReplaceTheDefaults()
    {
        await using var coolhand = new FakeCoolhand(o => o with { ExcludeApiPatterns = new[] { "/v1/messages" } });
        using var http = coolhand.Http(new FakeHandler());
        (await http.PostAsync("https://api.anthropic.com/v1/messages", JsonContent("{}"))).Dispose();
        (await http.PostAsync("https://api.openai.com/v1/files", JsonContent("{}"))).Dispose();
        var log = (await coolhand.WaitForLogsAsync(1)).Single();
        Assert.Equal("https://api.openai.com/v1/files", log["raw_request"]!["url"]!.GetValue<string>());
        coolhand.Client.ExcludeApiPatterns = Array.Empty<string>();
        Assert.Empty(coolhand.Client.ExcludeApiPatterns);
    }

    [Fact]
    public async Task ProviderErrorsAreLoggedWithTheirStatus()
    {
        await using var coolhand = new FakeCoolhand();
        using var http = coolhand.Http(new FakeHandler { Responder = (_, _) => FakeHandler.Json(429, "{\"error\":{\"type\":\"rate_limit\"}}") });
        using var response = await http.PostAsync("https://api.anthropic.com/v1/messages", JsonContent("{}"));
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        var raw = (await coolhand.WaitForLogsAsync(1)).Single()["raw_request"]!;
        Assert.Equal(429, raw["status_code"]!.GetValue<int>());
        Assert.Equal("rate_limit", raw["response_body"]!["error"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public async Task TransportFailuresPropagateAndLogNothing()
    {
        await using var coolhand = new FakeCoolhand();
        using var http = coolhand.Http(new FakeHandler { Responder = (_, _) => throw new HttpRequestException("connection refused") });
        await Assert.ThrowsAsync<HttpRequestException>(() => http.PostAsync("https://api.anthropic.com/v1/messages", JsonContent("{}")));
        await coolhand.Client.FlushAsync(TimeSpan.FromSeconds(2));
        Assert.Empty(coolhand.Logs);
        Assert.Equal(1, coolhand.Client.GetStats().InterceptedCalls);
    }

    [Fact]
    public async Task StreamsServerSentEventsToTheCallerAndLogsTheWholeStream()
    {
        await using var coolhand = new FakeCoolhand();
        var events = new[] { "event: message_start\ndata: {\"type\":\"message_start\"}\n\n", "data: {\"type\":\"content_block_delta\"}\n\n", "data: [DONE]\n\n" };
        using var http = coolhand.Http(new FakeHandler { Responder = (_, _) => FakeHandler.Stream(200, new ChunkedStream(events), "text/event-stream") });
        var request = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages") { Content = JsonContent("{\"stream\":true}") };
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);
        var firstLine = await reader.ReadLineAsync();
        Assert.Equal("event: message_start", firstLine);
        Assert.Empty(coolhand.Logs); // not logged until the stream ends
        var rest = await reader.ReadToEndAsync();
        Assert.EndsWith("data: [DONE]\n\n", rest);
        var raw = (await coolhand.WaitForLogsAsync(1)).Single()["raw_request"]!.AsObject();
        Assert.Equal(string.Concat(events), raw["response_body"]!.GetValue<string>());
        Assert.False(raw.ContainsKey("stream_incomplete"));
    }

    [Fact]
    public async Task AnAbandonedStreamIsLoggedAsIncompleteWithThePartialBody()
    {
        await using var coolhand = new FakeCoolhand();
        var events = new[] { "data: one\n\n", "data: two\n\n", "data: three\n\n" };
        using var http = coolhand.Http(new FakeHandler { Responder = (_, _) => FakeHandler.Stream(200, new ChunkedStream(events), "text/event-stream") });
        using (var response = await http.SendAsync(new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages") { Content = JsonContent("{}") },
                   HttpCompletionOption.ResponseHeadersRead))
        {
            var stream = await response.Content.ReadAsStreamAsync();
            var buffer = new byte[11];
            Assert.Equal(11, await stream.ReadAsync(buffer));
        }
        var raw = (await coolhand.WaitForLogsAsync(1)).Single()["raw_request"]!.AsObject();
        Assert.True(raw["stream_incomplete"]!.GetValue<bool>());
        Assert.Equal("data: one\n\n", raw["response_body"]!.GetValue<string>());
    }

    [Fact]
    public async Task AStreamThatFailsMidwayIsLoggedAsIncomplete()
    {
        await using var coolhand = new FakeCoolhand();
        var stream = new ChunkedStream(new[] { "data: one\n\n" }, new IOException("connection reset"));
        using var http = coolhand.Http(new FakeHandler { Responder = (_, _) => FakeHandler.Stream(200, stream, "text/event-stream") });
        using var response = await http.SendAsync(new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages") { Content = JsonContent("{}") },
            HttpCompletionOption.ResponseHeadersRead);
        await Assert.ThrowsAsync<IOException>(async () => await new StreamReader(await response.Content.ReadAsStreamAsync()).ReadToEndAsync());
        var raw = (await coolhand.WaitForLogsAsync(1)).Single()["raw_request"]!.AsObject();
        Assert.True(raw["stream_incomplete"]!.GetValue<bool>());
        Assert.Equal("data: one\n\n", raw["response_body"]!.GetValue<string>());
    }

    [Fact]
    public async Task ANonStreamedBodyTheCallerStopsReadingIsDrainedAndLoggedComplete()
    {
        await using var coolhand = new FakeCoolhand();
        var body = new ChunkedStream(new[] { "{\"id\":", "\"msg_1\"", "}" });
        using var http = coolhand.Http(new FakeHandler { Responder = (_, _) => FakeHandler.Stream(200, body, "application/json") });
        using (var response = await http.SendAsync(new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages") { Content = JsonContent("{}") },
                   HttpCompletionOption.ResponseHeadersRead))
        {
            var stream = await response.Content.ReadAsStreamAsync();
            _ = await stream.ReadAsync(new byte[3]);
        }
        var raw = (await coolhand.WaitForLogsAsync(1)).Single()["raw_request"]!.AsObject();
        Assert.Equal("msg_1", raw["response_body"]!["id"]!.GetValue<string>());
        Assert.False(raw.ContainsKey("stream_incomplete"));
    }

    [Fact]
    public async Task AResponseNeverReadIsStillLogged()
    {
        await using var coolhand = new FakeCoolhand();
        using var http = coolhand.Http(new FakeHandler { Responder = (_, _) => FakeHandler.Stream(200, new ChunkedStream(new[] { "{\"ok\":true}" }), "application/json") });
        var response = await http.SendAsync(new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages") { Content = JsonContent("{}") },
            HttpCompletionOption.ResponseHeadersRead);
        response.Dispose();
        var raw = (await coolhand.WaitForLogsAsync(1)).Single()["raw_request"]!;
        Assert.True(raw["response_body"]!["ok"]!.GetValue<bool>());
    }

    [Fact]
    public async Task AnIdleResponseIsLoggedAfterTheIdleTimeout()
    {
        await using var coolhand = new FakeCoolhand(o => o with { CaptureIdleTimeout = TimeSpan.FromMilliseconds(100) });
        using var http = coolhand.Http(new FakeHandler { Responder = (_, _) => FakeHandler.Stream(200, new ChunkedStream(new[] { "data: a\n\n", "data: b\n\n" }), "text/event-stream") });
        using var response = await http.SendAsync(new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages") { Content = JsonContent("{}") },
            HttpCompletionOption.ResponseHeadersRead);
        var stream = await response.Content.ReadAsStreamAsync();
        Assert.Equal(9, await stream.ReadAsync(new byte[9]));
        var raw = (await coolhand.WaitForLogsAsync(1)).Single()["raw_request"]!.AsObject();
        Assert.True(raw["stream_incomplete"]!.GetValue<bool>());
        Assert.Equal("data: a\n\n", raw["response_body"]!.GetValue<string>());
        // Reading on afterwards still works and logs nothing more.
        Assert.Equal("data: b\n\n", await new StreamReader(stream).ReadToEndAsync());
        await coolhand.Client.FlushAsync(TimeSpan.FromSeconds(1));
        Assert.Single(coolhand.Logs);
    }

    [Fact]
    public async Task DecodesCompressedResponsesForTheLogOnly()
    {
        await using var coolhand = new FakeCoolhand();
        var gz = new MemoryStream();
        using (var z = new GZipStream(gz, CompressionLevel.Fastest, leaveOpen: true)) z.Write("{\"id\":\"zipped\"}"u8);
        var compressed = gz.ToArray();
        using var http = coolhand.Http(new FakeHandler { Responder = (_, _) => FakeHandler.Bytes(200, compressed, "application/json", ("Content-Encoding", "gzip")) });
        using var response = await http.PostAsync("https://api.anthropic.com/v1/messages", JsonContent("{}"));
        Assert.Equal(compressed, await response.Content.ReadAsByteArrayAsync()); // the caller gets what was sent
        var raw = (await coolhand.WaitForLogsAsync(1)).Single()["raw_request"]!;
        Assert.Equal("zipped", raw["response_body"]!["id"]!.GetValue<string>());
    }

    [Fact]
    public async Task CallsWithoutABodyAreCaptured()
    {
        await using var coolhand = new FakeCoolhand();
        using var http = coolhand.Http(new FakeHandler { Responder = (_, _) => FakeHandler.Json(200, "{\"data\":[]}") });
        using var response = await http.GetAsync("https://api.anthropic.com/v1/models");
        var raw = (await coolhand.WaitForLogsAsync(1)).Single()["raw_request"]!;
        Assert.Equal("GET", raw["method"]!.GetValue<string>());
        Assert.Null(raw["request_body"]);
    }

    [Fact]
    public async Task ConcurrentCallsAreEachLoggedOnce()
    {
        await using var coolhand = new FakeCoolhand();
        using var http = coolhand.Http(new FakeHandler { Responder = (req, body) => FakeHandler.Json(200, body!) });
        await Task.WhenAll(Enumerable.Range(0, 20).Select(async i =>
        {
            using var r = await http.PostAsync("https://api.anthropic.com/v1/messages", JsonContent("{\"n\":" + i + "}"));
            Assert.Equal("{\"n\":" + i + "}", await r.Content.ReadAsStringAsync());
        }));
        var logs = await coolhand.WaitForLogsAsync(20);
        Assert.Equal(20, logs.Count);
        Assert.Equal(Enumerable.Range(0, 20), logs.Select(l => l["raw_request"]!["request_body"]!["n"]!.GetValue<int>()).OrderBy(n => n));
        Assert.Equal(Enumerable.Range(1, 20).Select(i => (long)i), logs.Select(l => l["raw_request"]!["id"]!.GetValue<long>()).OrderBy(n => n));
    }

    [Fact]
    public async Task ALoggingFailureNeverReachesTheCaller()
    {
        using var _ = new LogCapture();
        await using var coolhand = new FakeCoolhand();
        coolhand.Api.Responder = (_, _) => throw new HttpRequestException("coolhand is down");
        using var http = coolhand.Http(new FakeHandler { Responder = (_, _) => FakeHandler.Json(200, "{\"ok\":true}") });
        using var response = await http.PostAsync("https://api.anthropic.com/v1/messages", JsonContent("{}"));
        Assert.Equal("{\"ok\":true}", await response.Content.ReadAsStringAsync());
        Assert.True(await coolhand.Client.FlushAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task TheSynchronousSendPathIsCapturedToo()
    {
        await using var coolhand = new FakeCoolhand();
        using var http = coolhand.Http(new FakeHandler { Responder = (_, _) => FakeHandler.Json(200, "{\"ok\":true}") });
        using var response = http.Send(new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages") { Content = JsonContent("{}") });
        Assert.Equal("{\"ok\":true}", await response.Content.ReadAsStringAsync());
        await coolhand.WaitForLogsAsync(1);
    }

    [Fact]
    public async Task DryRunCapturesButSendsNothing()
    {
        await using var coolhand = new FakeCoolhand(o => o with { DryRun = true });
        using var http = coolhand.Http(new FakeHandler());
        (await http.PostAsync("https://api.anthropic.com/v1/messages", JsonContent("{}"))).Dispose();
        await coolhand.Client.FlushAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, coolhand.Client.GetStats().InterceptedCalls);
        Assert.Empty(coolhand.Api.Requests);
    }
}

[Collection("global")]
public class GlobalHandlerTests
{
    [Fact]
    public async Task TheParameterlessHandlerUsesTheGlobalClientAndIsAPassThroughWithoutOne()
    {
        Coolhand.ResetGlobalMonitoring();
        var provider = new FakeHandler();
        using var http = new HttpClient(new CoolhandHandler(null, provider));
        (await http.PostAsync("https://api.anthropic.com/v1/messages", new StringContent("{}"))).Dispose();
        Assert.Single(provider.Requests);

        var api = new FakeHandler();
        var global = Coolhand.InitializeGlobalMonitoring(new CoolhandOptions { ApiKey = "k", BaseUrl = FakeCoolhand.BaseUrl, HttpMessageHandler = api });
        try
        {
            (await http.PostAsync("https://api.anthropic.com/v1/messages", new StringContent("{}"))).Dispose();
            await global.FlushAsync(TimeSpan.FromSeconds(5));
            for (var i = 0; i < 100 && api.Requests.Count == 0; i++) await Task.Delay(20);
            Assert.Single(api.Requests);
        }
        finally
        {
            Coolhand.ResetGlobalMonitoring();
        }
    }
}
