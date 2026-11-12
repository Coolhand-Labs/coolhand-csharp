using System.Text.Json.Nodes;
using CoolhandLabs.Internal;
using CoolhandLabs.Models;
using CoolhandLabs.Services;
using CoolhandLabs.Tests.Support;

namespace CoolhandLabs.Tests;

public class LoggingServiceTests
{
    private static LoggingService Service(FakeHandler api, bool dryRun = false, bool silent = true) =>
        new(new ServiceConfig { ApiKey = "ch_key", BaseUrl = "https://coolhand.test", HttpMessageHandler = api, DryRun = dryRun, Silent = silent });

    private static CallData Call() => new()
    {
        Id = 7,
        Timestamp = "2026-01-01T00:00:00.000Z",
        Url = "https://api.openai.com/v1/chat/completions",
        Headers = new Dictionary<string, string> { ["content-type"] = "application/json" },
        RequestBody = JsonNode.Parse("{\"model\":\"gpt-4o\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}"),
        ResponseBody = JsonNode.Parse("{\"id\":\"chatcmpl-1\"}"),
        ResponseHeaders = new Dictionary<string, string> { ["x-request-id"] = "req_1" },
        StatusCode = 200,
    };

    [Fact]
    public async Task PostsTheNodeWireFormat()
    {
        var api = new FakeHandler { Responder = (_, _) => FakeHandler.Json(201, "{\"id\":\"log_1\",\"llm_provider_unique_id\":\"req_1\",\"warnings\":[]}") };
        var result = await Service(api).LogRequestToApiAsync(Call(), null, CollectionMethod.Manual,
            metadata: new Dictionary<string, object?> { ["project_path"] = "/srv/app", ["n"] = 2 },
            declared: new DeclaredContext { Reporter = "r" }.WithWorkload("w", "W"));
        var request = api.Requests.Single();
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/v2/llm_request_logs", request.Uri.AbsolutePath);
        Assert.Equal("application/json", request.Header("content-type"));
        Assert.Equal(
            "{\"llm_request_log\":{\"raw_request\":{\"id\":7,\"timestamp\":\"2026-01-01T00:00:00.000Z\",\"method\":\"POST\","
            + "\"url\":\"https://api.openai.com/v1/chat/completions\",\"headers\":{\"content-type\":\"application/json\"},"
            + "\"request_body\":{\"model\":\"gpt-4o\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]},"
            + "\"response_body\":{\"id\":\"chatcmpl-1\"},\"response_headers\":{\"x-request-id\":\"req_1\"},\"status_code\":200,"
            + "\"protocol\":\"https\"},\"collector\":\"coolhand-csharp-" + CoolhandVersion.PackageVersion + "-manual\","
            + "\"metadata\":{\"project_path\":\"/srv/app\",\"n\":2},"
            + "\"declared\":{\"reporter\":\"r\",\"workload\":{\"key\":\"w\",\"name\":\"W\"}}}}",
            request.Body);
        Assert.Equal("log_1", result!.Id);
        Assert.Equal("req_1", result.LlmProviderUniqueId);
    }

    [Fact]
    public async Task SendsStreamIncompleteOnlyWhenTrueAndNullsExplicitly()
    {
        var api = new FakeHandler();
        await Service(api).LogRequestToApiAsync(new CallData { Url = "https://x" }, null, CollectionMethod.GlobalMonitoring);
        await Service(api).LogRequestToApiAsync(new CallData { Url = "https://x", StreamIncomplete = true }, null, CollectionMethod.GlobalMonitoring);
        var first = (JsonObject)api.Requests[0].Json["llm_request_log"]!["raw_request"]!;
        Assert.False(first.ContainsKey("stream_incomplete"));
        Assert.True(first.ContainsKey("request_body"));
        Assert.Null(first["request_body"]);
        Assert.True(api.Requests[1].Json["llm_request_log"]!["raw_request"]!["stream_incomplete"]!.GetValue<bool>());
        Assert.EndsWith("-global-monitoring", api.Requests[0].Json["llm_request_log"]!["collector"]!.GetValue<string>());
    }

    [Fact]
    public async Task ACollectorOverrideWinsAndEmptyDeclarationsAreNotSent()
    {
        var api = new FakeHandler();
        await Service(api).LogRequestToApiAsync(Call(), null, CollectionMethod.Manual, collector: "my-cli-1.0", declared: new DeclaredContext());
        var log = (JsonObject)api.Requests.Single().Json["llm_request_log"]!;
        Assert.Equal("my-cli-1.0", log["collector"]!.GetValue<string>());
        Assert.False(log.ContainsKey("declared"));
        Assert.False(log.ContainsKey("metadata"));
    }

    [Fact]
    public async Task ServerWarningsAreLoggedNotThrown()
    {
        using var logs = new LogCapture();
        var api = new FakeHandler { Responder = (_, _) => FakeHandler.Json(201, "{\"id\":\"1\",\"warnings\":[{\"message\":\"workload cap reached\"},\"plain\"]}") };
        var result = await Service(api, silent: false).LogRequestToApiAsync(Call(), null, CollectionMethod.Manual);
        Assert.Equal(new[] { "workload cap reached", "plain" }, result!.Warnings);
        Assert.Contains("Coolhand warning: workload cap reached", logs.All);
    }

    [Fact]
    public async Task FailuresResolveToNull()
    {
        using var logs = new LogCapture();
        var api = new FakeHandler { Responder = (_, _) => FakeHandler.Json(500, "{\"error\":\"boom ch_key\"}") };
        Assert.Null(await Service(api).LogRequestToApiAsync(Call(), null, CollectionMethod.Manual));
        Assert.Contains("Request failed: 500", logs.All);
        Assert.DoesNotContain("ch_key", logs.All);
        api.Responder = (_, _) => throw new HttpRequestException("down");
        Assert.Null(await Service(api).LogRequestToApiAsync(Call(), null, CollectionMethod.Manual));
        api.Responder = (_, _) => FakeHandler.Json(200, "not json");
        Assert.Null(await Service(api).LogRequestToApiAsync(Call(), null, CollectionMethod.Manual));
    }

    [Fact]
    public async Task DryRunSendsNothing()
    {
        using var logs = new LogCapture();
        var api = new FakeHandler();
        Assert.Null(await Service(api, dryRun: true, silent: false).LogRequestToApiAsync(Call(), null, CollectionMethod.Manual));
        Assert.Empty(api.Requests);
        Assert.Contains("DRY RUN: Would send payload", logs.All);
        Assert.Contains("🤖 Model: gpt-4o", logs.All);
        Assert.Contains("💬 Messages: 1", logs.All);
    }

    [Fact]
    public async Task DebugLogsTheEndpointAndPayloadSize()
    {
        using var logs = new LogCapture();
        var api = new FakeHandler();
        var service = new LoggingService(new ServiceConfig { ApiKey = "k", BaseUrl = "https://coolhand.test", HttpMessageHandler = api, Debug = true, Silent = false });
        await service.LogRequestToApiAsync(Call(), null, CollectionMethod.Manual);
        Assert.Single(api.Requests);
        Assert.Contains("DEBUG: Sending to https://coolhand.test/api/v2/llm_request_logs", logs.All);
        Assert.Contains("DEBUG: Payload size:", logs.All);
    }

    [Fact]
    public async Task SearchLogsSendsEveryFilterInNodeOrder()
    {
        var api = new FakeHandler { Responder = (_, _) => FakeHandler.Json(200, "[{\"id\":\"l1\"}]", ("X-Total-Count", "1")) };
        var page = await Service(api).SearchLogsAsync(new SearchLogsParams
        {
            TemplateId = "t", WorkloadId = "w", SystemPromptContains = "s", UserPromptContains = "u", ErrorContains = "rate limit",
            Model = "gpt-4", SourceApi = "openai", SourceApiResult = "failed", SourceApplication = "app", ProjectPath = "/p",
            UnmatchedOnly = true, DaysBack = 7, Since = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero),
            Until = new DateTimeOffset(2026, 9, 2, 0, 0, 0, TimeSpan.Zero), MinCost = 0.5m, Order = "cost_desc", IncludePrompts = false,
            Sort = "created_at desc", Page = 2, Per = 10, IncludeTotal = true,
        });
        Assert.Equal("?template_id=t&workload_id=w&system_prompt_contains=s&user_prompt_contains=u&error_contains=rate+limit&model=gpt-4"
                     + "&source_api=openai&source_api_result=failed&source_application=app&project_path=%2Fp&unmatched_only=true&days_back=7"
                     + "&since=2026-09-01T00%3A00%3A00.000Z&until=2026-09-02T00%3A00%3A00.000Z&min_cost=0.5&order=cost_desc"
                     + "&include_prompts=false&q%5Bs%5D=created_at+desc&page=2&per=10&include_total=true",
            api.Requests.Single().Uri.Query);
        Assert.Equal("l1", page.Items.Single()["id"]!.GetValue<string>());
        Assert.Equal(1, page.Pagination.TotalCount);
    }

    [Fact]
    public async Task SearchLogsWithNoFiltersHasNoQuery()
    {
        var api = new FakeHandler { Responder = (_, _) => FakeHandler.Json(200, "[]") };
        var page = await Service(api).SearchLogsAsync();
        Assert.Equal("", api.Requests.Single().Uri.Query);
        Assert.Empty(page.Items);
    }

    [Fact]
    public async Task SearchLogsRejectsNonArrayBodies()
    {
        var api = new FakeHandler { Responder = (_, _) => FakeHandler.Json(200, "{\"logs\":[]}") };
        var e = await Assert.ThrowsAsync<CoolhandException>(() => Service(api).SearchLogsAsync());
        Assert.Contains("Log response was not a JSON array", e.Message);
    }

    [Fact]
    public async Task GetLogContentSendsOptions()
    {
        var api = new FakeHandler { Responder = (_, _) => FakeHandler.Json(200, "{\"id\":\"l1\"}") };
        await Service(api).GetLogContentAsync("l1", new GetLogContentOptions
        {
            Section = LogContentSection.End, MaxChars = 500, IncludeThinking = true, IncludeTurnMetrics = true,
        });
        Assert.Equal("/api/v2/llm_request_logs/l1", api.Requests[0].Uri.AbsolutePath);
        Assert.Equal("?section=end&max_chars=500&include_thinking=true&include_turn_metrics=true", api.Requests[0].Uri.Query);
        await Service(api).GetLogContentAsync("l1", new GetLogContentOptions { SearchQuery = "refund" });
        Assert.Equal("?search_query=refund", api.Requests[1].Uri.Query);
    }

    [Fact]
    public void GetLogContentValidatesTheSearchQuery()
    {
        var service = Service(new FakeHandler());
        Assert.Throws<ArgumentException>(() => { _ = service.GetLogContentAsync("l1", new GetLogContentOptions { SearchQuery = "  " }); });
        Assert.Throws<ArgumentException>(() => { _ = service.GetLogContentAsync("l1", new GetLogContentOptions { SearchQuery = "x", MaxChars = 5 }); });
        Assert.Throws<ArgumentException>(() => { _ = service.GetLogContentAsync("", null); });
    }

    [Fact]
    public async Task FailureSummarySendsDaysBackOnlyWhenGiven()
    {
        var api = new FakeHandler { Responder = (_, _) => FakeHandler.Json(200, "{\"total_failed_count\":3,\"error_breakdown\":[{\"error_pattern\":\"rate limit\"}]}") };
        var summary = await Service(api).FailureSummaryAsync(new FailureSummaryParams { DaysBack = 14 });
        Assert.Equal("/api/v2/llm_request_logs/failure_summary", api.Requests[0].Uri.AbsolutePath);
        Assert.Equal("?days_back=14", api.Requests[0].Uri.Query);
        Assert.Equal("rate limit", summary["error_breakdown"]![0]!["error_pattern"]!.GetValue<string>());
        await Service(api).FailureSummaryAsync();
        Assert.Equal("", api.Requests[1].Uri.Query);
    }

    [Fact]
    public async Task FailureSummaryPassesServerValidationThrough()
    {
        var api = new FakeHandler { Responder = (_, _) => FakeHandler.Json(422, "{\"error\":\"days_back must be positive\"}") };
        var e = await Assert.ThrowsAsync<CoolhandHttpException>(() => Service(api).FailureSummaryAsync(new FailureSummaryParams { DaysBack = 0 }));
        Assert.Equal(422, e.StatusCode);
    }
}

public class FeedbackServiceTests
{
    private static FeedbackService Service(FakeHandler api, bool dryRun = false) =>
        new(new ServiceConfig { ApiKey = "ch_key", BaseUrl = "https://coolhand.test", HttpMessageHandler = api, DryRun = dryRun });

    [Fact]
    public void WireFieldsFollowNodeOrderAndOmitNulls()
    {
        var wire = FeedbackService.ToWire(new LlmRequestLogFeedback
        {
            ClientUniqueId = "c", OriginalOutput = "o", LlmProviderUniqueId = "req_1", RevisedOutput = "r", Explanation = "e",
            WorkloadHashid = "w", CreatorUniqueId = "u", CreatorType = CreatorType.Human, Sentiment = Sentiment.Dislike, LlmRequestLogId = "abc",
        }, CollectionMethod.Manual);
        Assert.Equal("{\"llm_request_log_id\":\"abc\",\"sentiment\":\"dislike\",\"creator_type\":\"human\",\"creator_unique_id\":\"u\","
                     + "\"workload_hashid\":\"w\",\"explanation\":\"e\",\"revised_output\":\"r\",\"llm_provider_unique_id\":\"req_1\","
                     + "\"original_output\":\"o\",\"client_unique_id\":\"c\",\"collector\":\"coolhand-csharp-" + CoolhandVersion.PackageVersion + "-manual\"}",
            Json.Stringify(wire));
    }

#pragma warning disable CS0618 // Like is deprecated
    [Theory]
    [InlineData(true, null, "\"sentiment\":\"like\"")]
    [InlineData(false, null, "\"sentiment\":\"dislike\"")]
    [InlineData(true, Sentiment.Neutral, "\"like\":true,\"sentiment\":\"neutral\"")]
    public void LikeConvertsToSentimentUnlessOneIsGiven(bool like, Sentiment? sentiment, string expected)
    {
        var wire = Json.Stringify(FeedbackService.ToWire(new LlmRequestLogFeedback { Like = like, Sentiment = sentiment }, CollectionMethod.Manual));
        Assert.StartsWith("{" + expected + ",\"collector\"", wire);
    }
#pragma warning restore CS0618

    [Fact]
    public void ANumericLogIdIsSentAsANumber() =>
        Assert.StartsWith("{\"llm_request_log_id\":42,", Json.Stringify(FeedbackService.ToWire(new LlmRequestLogFeedback { LlmRequestLogNumericId = 42 }, CollectionMethod.Manual)));

    [Fact]
    public async Task CreatesFeedbackAndReadsTheMatchedLog()
    {
        var api = new FakeHandler
        {
            Responder = (_, _) => FakeHandler.Json(201, "{\"id\":\"fb_1\",\"llm_request_log_id\":\"log_9\",\"sentiment\":\"like\",\"creator_type\":\"human\","
                                                        + "\"collector\":\"x\",\"warnings\":[\"w1\"]}"),
        };
        var response = await Service(api).CreateFeedbackAsync(new LlmRequestLogFeedback { LlmProviderUniqueId = "req_1", Sentiment = Sentiment.Like });
        var request = api.Requests.Single();
        Assert.Equal("/api/v2/llm_request_log_feedbacks", request.Uri.AbsolutePath);
        Assert.Equal("req_1", request.Json["llm_request_log_feedback"]!["llm_provider_unique_id"]!.GetValue<string>());
        Assert.Equal("fb_1", response!.Id);
        Assert.Equal("log_9", response.LlmRequestLogId);
        Assert.Equal(Sentiment.Like, response.Sentiment);
        Assert.Equal(CreatorType.Human, response.CreatorType);
        Assert.Equal(new[] { "w1" }, response.Warnings);
    }

    [Fact]
    public async Task CreateNeverThrowsForApiOrNetworkFailures()
    {
        using var _ = new LogCapture();
        var api = new FakeHandler { Responder = (_, _) => FakeHandler.Json(422, "{}") };
        Assert.Null(await Service(api).CreateFeedbackAsync(new LlmRequestLogFeedback { Explanation = "x" }));
        api.Responder = (_, _) => throw new HttpRequestException("network down");
        Assert.Null(await Service(api).CreateFeedbackAsync(new LlmRequestLogFeedback { Explanation = "x" }));
        Assert.Null(await Service(api).CreateFeedbackAsync(null!));
    }

    [Fact]
    public async Task DryRunSkipsCreation()
    {
        var api = new FakeHandler();
        Assert.Null(await Service(api, dryRun: true).CreateFeedbackAsync(new LlmRequestLogFeedback { Explanation = "x" }));
        Assert.Empty(api.Requests);
    }

    [Fact]
    public async Task SearchSendsRansackPredicatesAndReadsTheEnvelope()
    {
        var api = new FakeHandler
        {
            Responder = (_, _) => FakeHandler.Json(200, "{\"feedback\":[{\"id\":\"f1\"}],\"pagination\":{\"current_page\":1,\"per_page\":50,\"total_count\":1,\"total_pages\":1}}"),
        };
        var page = await Service(api).SearchFeedbackAsync(new SearchFeedbackParams
        {
            Predicates = new Dictionary<string, object?> { ["sentiment_eq"] = 0, ["explanation_cont"] = "too long", ["s"] = "created_at desc", ["skip"] = null },
            Page = 1, Per = 50,
        });
        Assert.Equal("?q%5Bsentiment_eq%5D=0&q%5Bexplanation_cont%5D=too+long&q%5Bs%5D=created_at+desc&page=1&per=50", api.Requests.Single().Uri.Query);
        Assert.Equal("f1", page.Items.Single()["id"]!.GetValue<string>());
        Assert.Equal(50, page.Pagination.PerPage);
    }

    [Fact]
    public async Task SearchToleratesAMissingFeedbackKey()
    {
        var api = new FakeHandler { Responder = (_, _) => FakeHandler.Json(200, "{}") };
        Assert.Empty((await Service(api).SearchFeedbackAsync()).Items);
    }
}
