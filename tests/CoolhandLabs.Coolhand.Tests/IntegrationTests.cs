using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Anthropic;
using Anthropic.Core;
using Anthropic.Models.Messages;
using CoolhandLabs.Anthropic;
using CoolhandLabs.Http;
using CoolhandLabs.Testing;
using CoolhandLabs.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace CoolhandLabs.Tests;

public class AnthropicIntegrationTests
{
    private const string MessageJson =
        "{\"id\":\"msg_01\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"claude-sonnet-5-5\",\"content\":[{\"type\":\"text\",\"text\":\"Hi\"}],"
        + "\"stop_reason\":\"end_turn\",\"stop_sequence\":null,\"usage\":{\"input_tokens\":3,\"output_tokens\":1}}";

    private static MessageCreateParams Params() => new()
    {
        MaxTokens = 64,
        Messages = [new UserMessageParam("Hello, Claude")],
        Model = "claude-sonnet-5-5",
    };

    private static AnthropicClient Client(Coolhand? coolhand, FakeHandler provider) =>
        CoolhandAnthropic.CreateClient(coolhand, o => o with
        {
            ApiKey = "sk-ant-test",
            BaseUrl = "https://api.anthropic.com",
            MaxRetries = 0,
            HttpClient = new HttpClient(provider),
        });

    [Fact]
    public async Task LogsSdkCallsWithTheRequestIdAndDeclaredContext()
    {
        await using var coolhand = new FakeCoolhand();
        var provider = new FakeHandler { Responder = (_, _) => FakeHandler.Json(200, MessageJson, ("request-id", "req_011CABC")) };
        var client = Client(coolhand.Client, provider);
        var ctx = new DeclaredContext { Reporter = "qa-agent" }.WithWorkload("qa/plans", "QA").WithTemplate("qa/plans/generate", "generate");

        var raw = await Coolhand.WithContextAsync(ctx, () => client.WithRawResponse.Messages.Create(Params()));
        var message = await raw.Deserialize();
        Assert.Equal("msg_01", message.ID);
        Assert.Equal("req_011CABC", raw.RequestID);

        var log = (await coolhand.WaitForLogsAsync(1)).Single();
        var call = log["raw_request"]!;
        Assert.Equal("https://api.anthropic.com/v1/messages", call["url"]!.GetValue<string>());
        Assert.Equal("[REDACTED]", call["headers"]!["authorization"]!.GetValue<string>()); // the SDK sends the key as a bearer token
        Assert.Equal("Bearer sk-ant-test", provider.Requests.Single().Header("authorization"));
        Assert.Equal("claude-sonnet-5-5", call["request_body"]!["model"]!.GetValue<string>());
        Assert.Equal("msg_01", call["response_body"]!["id"]!.GetValue<string>());
        Assert.Equal("req_011CABC", call["response_headers"]!["request-id"]!.GetValue<string>());
        Assert.Equal("qa/plans/generate", log["declared"]!["template"]!["key"]!.GetValue<string>());
        Assert.DoesNotContain("sk-ant-test", coolhand.Api.Requests.Single().Body);

        // Feedback on that call matches on the request id.
        coolhand.Api.Responder = (_, _) => FakeHandler.Json(201, "{\"id\":\"fb_1\",\"llm_request_log_id\":\"log_1\"}");
        var feedback = await coolhand.Client.CreateFeedbackAsync(new LlmRequestLogFeedback
        {
            LlmProviderUniqueId = raw.RequestID, Sentiment = Sentiment.Like, CreatorType = CreatorType.Human,
        });
        Assert.Equal("log_1", feedback!.LlmRequestLogId);
        Assert.Equal("req_011CABC", coolhand.Api.Requests.Last().Json["llm_request_log_feedback"]!["llm_provider_unique_id"]!.GetValue<string>());
    }

    [Fact]
    public async Task LogsStreamedMessages()
    {
        await using var coolhand = new FakeCoolhand();
        var sse = new[]
        {
            "event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_02\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"claude-sonnet-5-5\","
            + "\"content\":[],\"stop_reason\":null,\"stop_sequence\":null,\"usage\":{\"input_tokens\":3,\"output_tokens\":0}}}\n\n",
            "event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}\n\n",
            "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"Hi\"}}\n\n",
            "event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":0}\n\n",
            "event: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\",\"stop_sequence\":null},\"usage\":{\"output_tokens\":1}}\n\n",
            "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n",
        };
        var provider = new FakeHandler { Responder = (_, _) => FakeHandler.Stream(200, new ChunkedStream(sse), "text/event-stream") };
        var client = Client(coolhand.Client, provider);
        var events = 0;
        await foreach (var _ in client.Messages.CreateStreaming(Params())) events++;
        Assert.Equal(6, events);
        var call = (await coolhand.WaitForLogsAsync(1)).Single()["raw_request"]!;
        Assert.Contains("message_stop", call["response_body"]!.GetValue<string>());
    }

    [Fact]
    public void WithCoolhandAppendsTheHandlerLast()
    {
        var existing = new PassThrough();
        var options = new ClientOptions { Handlers = [existing] };
        var coolhand = new Coolhand(new CoolhandOptions { ApiKey = "k" });
        var configured = options.WithCoolhand(coolhand);
        Assert.Equal(2, configured.Handlers.Count);
        Assert.Same(existing, configured.Handlers[0]);
        Assert.IsType<CoolhandHandler>(configured.Handlers[1]);
    }

    [Fact]
    [Trait("Category", "Environment")]
    public void WithoutAClientTheOptionsAreUnchanged()
    {
        Coolhand.ResetGlobalMonitoring();
        if (Coolhand.Global is not null) return; // COOLHAND_API_KEY set in this environment
        var options = new ClientOptions();
        Assert.Empty(options.WithCoolhand().Handlers);
        Assert.Empty(new ClientOptions().WithCoolhand(null).Handlers);
    }

    private sealed class PassThrough : DelegatingHandler { }
}

public class HttpClientFactoryTests
{
    [Fact]
    public async Task AddCoolhandMonitorsEveryFactoryClient()
    {
        var api = new FakeHandler();
        var provider = new FakeHandler { Responder = (_, _) => FakeHandler.Json(200, "{\"ok\":true}") };
        var services = new ServiceCollection();
        services.AddCoolhand(new CoolhandOptions { ApiKey = "k", BaseUrl = FakeCoolhand.BaseUrl, HttpMessageHandler = api });
        services.AddHttpClient("llm").ConfigurePrimaryHttpMessageHandler(() => provider);
        services.AddHttpClient<TypedClient>().ConfigurePrimaryHttpMessageHandler(() => provider);
        await using var sp = services.BuildServiceProvider();
        var coolhand = sp.GetRequiredService<Coolhand>();

        var named = sp.GetRequiredService<IHttpClientFactory>().CreateClient("llm");
        (await named.PostAsync("https://api.openai.com/v1/chat/completions", new StringContent("{}"))).Dispose();
        await sp.GetRequiredService<TypedClient>().CallAsync();

        await coolhand.FlushAsync(TimeSpan.FromSeconds(5));
        for (var i = 0; i < 100 && api.Requests.Count < 2; i++) await Task.Delay(20);
        Assert.Equal(2, api.Requests.Count);
        Assert.Equal(2, coolhand.GetStats().InterceptedCalls);
    }

    [Fact]
    public async Task AddCoolhandHandlerMonitorsOneClient()
    {
        var api = new FakeHandler();
        var coolhand = new Coolhand(new CoolhandOptions { ApiKey = "k", BaseUrl = FakeCoolhand.BaseUrl, HttpMessageHandler = api });
        var provider = new FakeHandler();
        var services = new ServiceCollection();
        services.AddHttpClient("monitored").AddCoolhandHandler(coolhand).ConfigurePrimaryHttpMessageHandler(() => provider);
        services.AddHttpClient("plain").ConfigurePrimaryHttpMessageHandler(() => provider);
        await using var sp = services.BuildServiceProvider();
        var factory = sp.GetRequiredService<IHttpClientFactory>();
        (await factory.CreateClient("plain").PostAsync("https://api.openai.com/v1/chat/completions", new StringContent("{}"))).Dispose();
        (await factory.CreateClient("monitored").PostAsync("https://api.openai.com/v1/chat/completions", new StringContent("{}"))).Dispose();
        Assert.Equal(1, coolhand.GetStats().InterceptedCalls);
    }

    public sealed class TypedClient(HttpClient http)
    {
        public async Task CallAsync() => (await http.PostAsync("https://api.anthropic.com/v1/messages", new StringContent("{}"))).Dispose();
    }
}

/// <summary>A real socket round trip: HttpClient's SocketsHttpHandler streaming a chunked SSE response from HttpListener.</summary>
public class RealSocketTests
{
    [Fact]
    public async Task CapturesAChunkedStreamOverARealConnection()
    {
        var port = FreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add("http://127.0.0.1:" + port + "/");
        listener.Start();
        var serve = Task.Run(async () =>
        {
            var context = await listener.GetContextAsync();
            using var reader = new StreamReader(context.Request.InputStream);
            var requestBody = await reader.ReadToEndAsync();
            context.Response.ContentType = "text/event-stream";
            context.Response.SendChunked = true;
            context.Response.AddHeader("request-id", "req_socket");
            foreach (var chunk in new[] { "data: {\"echo\":" + requestBody + "}\n\n", "data: [DONE]\n\n" })
            {
                var bytes = Encoding.UTF8.GetBytes(chunk);
                await context.Response.OutputStream.WriteAsync(bytes);
                await context.Response.OutputStream.FlushAsync();
                await Task.Delay(20);
            }
            context.Response.Close();
        });

        var patterns = Path.Combine(Path.GetTempPath(), "coolhand-local-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(patterns, "{\"patterns\":[{\"name\":\"Local\",\"domains\":[\"127.0.0.1\"]}]}");
        await using var coolhand = new FakeCoolhand(o => o with { PatternsFile = patterns });
        using var http = coolhand.Client.CreateHttpClient();
        var before = CoolhandTestUtils.CaptureInterceptionSnapshot(coolhand.Client);
        using var response = await http.SendAsync(new HttpRequestMessage(HttpMethod.Post, "http://127.0.0.1:" + port + "/v1/stream")
        {
            Content = new StringContent("{\"n\":1}", Encoding.UTF8, "application/json"),
        }, HttpCompletionOption.ResponseHeadersRead);
        var text = await response.Content.ReadAsStringAsync();
        await serve;
        CoolhandTestUtils.AssertInterceptionOccurred(coolhand.Client, before);

        Assert.Equal("data: {\"echo\":{\"n\":1}}\n\ndata: [DONE]\n\n", text);
        var call = (await coolhand.WaitForLogsAsync(1)).Single()["raw_request"]!.AsObject();
        Assert.Equal("http", call["protocol"]!.GetValue<string>());
        Assert.Equal(1, call["request_body"]!["n"]!.GetValue<int>());
        Assert.Equal(text, call["response_body"]!.GetValue<string>());
        Assert.Equal("req_socket", call["response_headers"]!["request-id"]!.GetValue<string>());
        Assert.False(call.ContainsKey("stream_incomplete"));
    }

    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
