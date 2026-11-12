using System.Text;
using System.Text.Json.Nodes;
using CoolhandLabs.Models;
using CoolhandLabs.Services;
using CoolhandLabs.Tests.Support;

namespace CoolhandLabs.Tests;

public class ReadServicesTests
{
    private static ServiceConfig Config(FakeHandler api, bool dryRun = false) =>
        new() { ApiKey = "ch_key", BaseUrl = "https://coolhand.test", HttpMessageHandler = api, DryRun = dryRun };

    private static FakeHandler Array(string json = "[]", params (string, string)[] headers) =>
        new() { Responder = (_, _) => FakeHandler.Json(200, json, headers) };

    [Fact]
    public async Task SearchTemplatesSendsFiltersAndMetricsWindow()
    {
        var api = Array("[{\"id\":\"tpl_1\"}]");
        var page = await new TemplateService(Config(api)).SearchTemplatesAsync(new SearchTemplatesParams
        {
            Search = "summar", WorkloadId = "w", Status = "published", IncludeDeprecated = true, IncludeSystem = false,
            Metrics = new MetricsWindow { IncludeMetrics = true, DaysBack = 30, Since = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero) },
            Page = 1, Per = 25,
        });
        Assert.Equal("/api/v2/llm_request_templates", api.Requests[0].Uri.AbsolutePath);
        Assert.Equal("?search=summar&workload_id=w&status=published&include_deprecated=true&include_system=false&include_metrics=true"
                     + "&days_back=30&since=2026-01-01T00%3A00%3A00.000Z&page=1&per=25", api.Requests[0].Uri.Query);
        Assert.Equal("tpl_1", page.Items[0]["id"]!.GetValue<string>());
    }

    [Fact]
    public async Task GetTemplateSendsTheMetricsOptOut()
    {
        var api = Array("{\"id\":\"tpl_1\",\"prompt_patterns\":[]}");
        var template = await new TemplateService(Config(api)).GetTemplateAsync("tpl_1", new MetricsWindow { IncludeMetrics = false });
        Assert.Equal("/api/v2/llm_request_templates/tpl_1?include_metrics=false", api.Requests[0].Uri.PathAndQuery);
        Assert.NotNull(template["prompt_patterns"]);
    }

    [Fact]
    public async Task StepTemplates()
    {
        var api = Array("[{\"id\":42}]", ("X-Total-Count", "1"));
        var page = await new StepTemplateService(Config(api)).ListStepTemplatesAsync(new ListStepTemplatesParams
        {
            Search = "s", StepType = "prompt_step", IncludeMetrics = true, Page = 1, Per = 25,
        });
        Assert.Equal("/api/v2/step_templates?search=s&step_type=prompt_step&include_metrics=true&page=1&per=25", api.Requests[0].Uri.PathAndQuery);
        Assert.Equal(1, page.Pagination.TotalCount);
        api.Responder = (_, _) => FakeHandler.Json(200, "{\"id\":42,\"metrics\":{\"notes\":\"n\"}}");
        var one = await new StepTemplateService(Config(api)).GetStepTemplateAsync(42, includeMetrics: false);
        Assert.Equal("/api/v2/step_templates/42?include_metrics=false", api.Requests[1].Uri.PathAndQuery);
        Assert.Equal("n", one["metrics"]!["notes"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(9_007_199_254_740_992L)]
    public void StepTemplateIdsMustBePositiveSafeIntegers(long id) =>
        Assert.Throws<ArgumentException>(() => { _ = new StepTemplateService(Config(new FakeHandler())).GetStepTemplateAsync(id); });

    [Fact]
    public async Task SearchWorkloads()
    {
        var api = Array("[{\"id\":\"wkl_1\",\"metrics\":{\"total_cost\":1.5}}]");
        var page = await new WorkloadService(Config(api)).SearchWorkloadsAsync(new SearchWorkloadsParams
        {
            Search = "qa", IncludeArchived = false, IncludeSystem = true, IncludeTemplates = true,
            Metrics = new MetricsWindow { IncludeMetrics = true, Until = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero) },
        });
        Assert.Equal("?search=qa&include_archived=false&include_system=true&include_templates=true&include_metrics=true"
                     + "&until=2026-02-01T00%3A00%3A00.000Z", api.Requests[0].Uri.Query);
        Assert.Equal(1.5, page.Items[0]["metrics"]!["total_cost"]!.GetValue<double>());
    }

    [Fact]
    public async Task UpsertWorkloadSendsOnlyGivenFieldsAndEncodesEachKeySegment()
    {
        var api = new FakeHandler { Responder = (_, _) => FakeHandler.Json(201, "{\"workload\":{\"id\":\"wkl_1\"}}") };
        var result = await new WorkloadService(Config(api)).UpsertAsync(new UpsertWorkloadParams("qa/test plans") { Name = "QA", Reporter = "agent" });
        var request = api.Requests.Single();
        Assert.Equal(HttpMethod.Put, request.Method);
        Assert.Equal("/api/v2/workloads/by_key/qa/test%20plans", request.Uri.AbsolutePath);
        Assert.Equal("{\"name\":\"QA\",\"reporter\":\"agent\"}", request.Body);
        Assert.True(result!.Created);
        api.Responder = (_, _) => FakeHandler.Json(200, "{}");
        Assert.False((await new WorkloadService(Config(api)).UpsertAsync(new UpsertWorkloadParams("k")))!.Created);
    }

    [Theory]
    [InlineData("a/../b")]
    [InlineData("a/./b")]
    [InlineData("/lead")]
    [InlineData("a//b")]
    [InlineData("")]
    public void UpsertKeysAreValidatedBeforeAnyRequestEvenInDryRun(string key)
    {
        var api = new FakeHandler();
        Assert.Throws<ArgumentException>(() => { _ = new WorkloadService(Config(api, dryRun: true)).UpsertAsync(new UpsertWorkloadParams(key)); });
        Assert.Empty(api.Requests);
    }

    [Fact]
    public async Task UpsertIsSkippedInDryRun()
    {
        var api = new FakeHandler();
        Assert.Null(await new TemplateService(Config(api, dryRun: true)).UpsertAsync(new UpsertTemplateParams("t") { WorkloadKey = "w" }));
        Assert.Empty(api.Requests);
    }

    [Fact]
    public void UpsertWorkloadValidatesTheDescription() =>
        Assert.Throws<ArgumentException>(() =>
        {
            _ = new WorkloadService(Config(new FakeHandler())).UpsertAsync(new UpsertWorkloadParams("k") { Description = new string('d', 1001) });
        });

    [Fact]
    public async Task UpsertTemplateFlattensTheWorkload()
    {
        var api = new FakeHandler { Responder = (_, _) => FakeHandler.Json(200, "{\"template\":{}}") };
        var service = new TemplateService(Config(api));
        await service.UpsertAsync(new UpsertTemplateParams("t") { WorkloadId = "wkl_1", WorkloadKey = "ignored", Name = "T" });
        await service.UpsertAsync(new UpsertTemplateParams("t") { WorkloadId = " ", WorkloadKey = "w/k" });
        Assert.Equal("{\"name\":\"T\",\"workload_id\":\"wkl_1\"}", api.Requests[0].Body);
        Assert.Equal("{\"workload_key\":\"w/k\"}", api.Requests[1].Body);
        Assert.Throws<ArgumentException>(() => { _ = service.UpsertAsync(new UpsertTemplateParams("t") { WorkloadKey = " " }); });
        Assert.Throws<ArgumentException>(() => { _ = service.UpsertAsync(new UpsertTemplateParams("t") { WorkloadKey = "/bad" }); });
    }

    [Fact]
    public async Task ReferencedFiles()
    {
        var api = Array("[{\"file_path\":\"src/routes.ts\",\"reference_count\":3}]");
        var service = new LlmReferenceService(Config(api));
        await service.SearchReferencedFilesAsync(new SearchReferencedFilesParams { FilePathContains = "routes", CreatedAtGteq = "2026-01-01", Per = 10 });
        Assert.Equal("/api/v2/llm_references?q%5Bfile_path_cont%5D=routes&q%5Bcreated_at_gteq%5D=2026-01-01&per=10", api.Requests[0].Uri.PathAndQuery);
        await service.ListReferencedFileSessionsAsync(new ListReferencedFileSessionsParams("src/a b.ts") { Page = 2 });
        Assert.Equal("/api/v2/llm_references/sessions?file_path=src%2Fa+b.ts&page=2", api.Requests[1].Uri.PathAndQuery);
        Assert.Throws<ArgumentException>(() => { _ = service.ListReferencedFileSessionsAsync(null!); });
    }

    [Fact]
    public async Task HelpCenterSearchesWithAndWithoutACategory()
    {
        var api = Array("[{\"slug\":\"feedback-api\"}]");
        var service = new HelpCenterDocsService(Config(api));
        await service.SearchHelpArticlesAsync(new SearchHelpArticlesParams { Query = "  match rate ", PerPage = 5 });
        await service.SearchHelpArticlesAsync(new SearchHelpArticlesParams { Query = "logs", Category = "guides", Page = 2 });
        await service.SearchHelpArticlesAsync(new SearchHelpArticlesParams { Query = "   " });
        Assert.Equal("/api/v2/public_content?q=match+rate&per_page=5", api.Requests[0].Uri.PathAndQuery);
        Assert.Equal("?q%5Bhelp_category_slug_eq%5D=guides&q%5Bfull_text%5D=logs&page=2", api.Requests[1].Uri.Query);
        Assert.Equal("", api.Requests[2].Uri.Query);
        api.Responder = (_, _) => FakeHandler.Json(200, "{\"slug\":\"feedback-api\",\"body\":\"# Feedback\"}");
        Assert.Equal("# Feedback", (await service.GetHelpArticleAsync("feedback-api"))["body"]!.GetValue<string>());
        api.Responder = (_, _) => FakeHandler.Json(200, "[{\"slug\":\"guides\"}]");
        Assert.Single(await service.ListHelpCategoriesAsync());
        Assert.Equal("/api/v2/public_content/categories", api.Requests[4].Uri.AbsolutePath);
    }

    [Fact]
    [Trait("Category", "Environment")]
    public async Task HelpCenterIsAnonymousWithAnEmptyKey()
    {
        var api = Array();
        await new HelpCenterDocsService(new ServiceConfig { ApiKey = "", BaseUrl = "https://coolhand.test", HttpMessageHandler = api }).SearchHelpArticlesAsync();
        Assert.Null(api.Requests[0].Header("x-api-key"));
        await new HelpCenterDocsService(Config(api)).SearchHelpArticlesAsync();
        Assert.Equal("ch_key", api.Requests[1].Header("x-api-key"));
    }

    [Fact]
    public async Task HelpCenterRejectsABadKeyRatherThanRetryingAnonymously()
    {
        var api = new FakeHandler { Responder = (_, _) => FakeHandler.Json(401, "{}") };
        var e = await Assert.ThrowsAsync<CoolhandHttpException>(() => new HelpCenterDocsService(Config(api)).SearchHelpArticlesAsync());
        Assert.Equal(401, e.StatusCode);
        Assert.Single(api.Requests);
    }
}

public class WriteServicesTests
{
    private static ServiceConfig Config(FakeHandler api, bool dryRun = false) =>
        new() { ApiKey = "ch_key", BaseUrl = "https://coolhand.test", HttpMessageHandler = api, DryRun = dryRun };

    [Fact]
    public async Task UploadsAMultipartDraft()
    {
        var api = new FakeHandler { Responder = (_, _) => FakeHandler.Json(201, "{\"id\":\"cf_1\",\"name\":\"Deck\",\"status\":\"draft\",\"metadata\":{\"q\":3}}") };
        var file = await new ClientFileService(Config(api)).CreateClientFileAsync(new ClientFileUpload("Deck", Encoding.UTF8.GetBytes("PPTX"), "deck\".pptx")
        {
            FileType = "slide_deck", Description = "Q3",
            Metadata = new Dictionary<string, object?> { ["quarter"] = "Q3", ["pages"] = 12, ["tags"] = new[] { "a" } },
        });
        var request = api.Requests.Single();
        var contentType = request.Header("content-type")!;
        Assert.StartsWith("multipart/form-data; boundary=----coolhand-csharp-", contentType);
        var boundary = contentType.Split("boundary=")[1];
        var body = request.Body!;
        Assert.Contains("--" + boundary + "\r\nContent-Disposition: form-data; name=\"client_file[name]\"\r\n\r\nDeck\r\n", body);
        Assert.Contains("name=\"client_file[file_type]\"\r\n\r\nslide_deck\r\n", body);
        Assert.Contains("name=\"client_file[description]\"\r\n\r\nQ3\r\n", body);
        Assert.Contains("name=\"client_file[file]\"; filename=\"deck%22.pptx\"\r\nContent-Type: application/octet-stream\r\n\r\nPPTX\r\n", body);
        Assert.Contains("name=\"client_file[metadata][quarter]\"\r\n\r\nQ3\r\n", body);
        Assert.Contains("name=\"client_file[metadata][pages]\"\r\n\r\n12\r\n", body);
        Assert.Contains("name=\"client_file[metadata][tags]\"\r\n\r\n[\"a\"]\r\n", body);
        Assert.EndsWith("--" + boundary + "--\r\n", body);
        Assert.Equal("cf_1", file!.Id);
        Assert.Equal("draft", file.Status);
    }

    [Theory]
    [InlineData("")]
    [InlineData("a[b]")]
    [InlineData("a\nb")]
    public void RejectsBadMetadataKeys(string key) =>
        Assert.Throws<ArgumentException>(() =>
        {
            _ = new ClientFileService(Config(new FakeHandler())).CreateClientFileAsync(new ClientFileUpload("n", new byte[1], "f")
            {
                Metadata = new Dictionary<string, object?> { [key] = "v" },
            });
        });

    [Fact]
    public async Task UploadFailuresResolveToNull()
    {
        using var _ = new LogCapture();
        var api = new FakeHandler { Responder = (_, _) => FakeHandler.Json(401, "{}") };
        Assert.Null(await new ClientFileService(Config(api)).CreateClientFileAsync(new ClientFileUpload("n", new byte[1], "f")));
        var dry = new FakeHandler();
        Assert.Null(await new ClientFileService(Config(dry, dryRun: true)).CreateClientFileAsync(new ClientFileUpload("n", new byte[1], "f")));
        Assert.Empty(dry.Requests);
    }

    [Fact]
    public async Task ListsDownloadsAndPublishesFiles()
    {
        var api = new FakeHandler { Responder = (_, _) => FakeHandler.Json(200, "[{\"id\":\"cf_1\",\"name\":\"Deck\"}]", ("X-Total-Count", "1")) };
        var service = new ClientFileService(Config(api));
        var page = await service.ListClientFilesAsync(new ListClientFilesParams { Status = "published", FileType = "report", Per = 5 });
        Assert.Equal("/api/v2/client_files?status=published&file_type=report&per=5", api.Requests[0].Uri.PathAndQuery);
        Assert.Equal("Deck", page.Items.Single().Name);

        api.Responder = (_, _) => FakeHandler.Bytes(200, new byte[] { 0, 1, 2 }, "application/pdf",
            ("Content-Disposition", "attachment; filename=\"r.pdf\"; filename*=UTF-8''r%C3%A9port.pdf"));
        var downloaded = await service.DownloadClientFileAsync("cf_1");
        Assert.Equal("/api/v2/client_files/cf_1/content", api.Requests[1].Uri.AbsolutePath);
        Assert.Equal(new byte[] { 0, 1, 2 }, downloaded.Content);
        Assert.Equal("application/pdf", downloaded.ContentType);
        Assert.Equal("réport.pdf", downloaded.Filename);

        api.Responder = (_, _) => FakeHandler.Json(200, "{\"id\":\"cf_1\",\"status\":\"published\"}");
        var updated = await service.UpdateClientFileStatusAsync("cf_1", "published");
        Assert.Equal(HttpMethod.Patch, api.Requests[2].Method);
        Assert.Equal("{\"client_file\":{\"status\":\"published\"}}", api.Requests[2].Body);
        Assert.Equal("published", updated!.Status);
    }

    [Theory]
    [InlineData("attachment; filename=\"a \\\"b\\\".pdf\"", "a \"b\".pdf")]
    [InlineData("attachment; filename=plain.txt", "plain.txt")]
    [InlineData("attachment; filename*=UTF-8''%zz; filename=\"fallback.txt\"", "fallback.txt")]
    [InlineData("attachment", null)]
    [InlineData(null, null)]
    public void ParsesContentDispositionFilenames(string? header, string? expected) =>
        Assert.Equal(expected, ClientFileService.FilenameFromContentDisposition(header));

    [Fact]
    public async Task AdminReadsThrowOnErrors()
    {
        var api = new FakeHandler { Responder = (_, _) => FakeHandler.Json(403, "{}") };
        var e = await Assert.ThrowsAsync<CoolhandHttpException>(() => new ClientFileService(Config(api)).DownloadClientFileAsync("cf_1"));
        Assert.Equal(403, e.StatusCode);
        Assert.Null(await new ClientFileService(Config(api, dryRun: true)).UpdateClientFileStatusAsync("cf_1", "archived"));
    }

    [Fact]
    public async Task LinksFeedbackWithAndWithoutANote()
    {
        var api = new FakeHandler { Responder = (_, _) => FakeHandler.Json(201, "{\"id\":\"lnk_1\",\"optimization_id\":\"opt 1\",\"feedback_id\":\"fb_1\",\"note\":null}") };
        var service = new OptimizationFeedbackLinkService(Config(api));
        var link = await service.LinkFeedbackAsync("opt 1", "fb_1");
        await service.LinkFeedbackAsync("opt 1", "fb_1", null);
        await service.LinkFeedbackAsync("opt 1", "fb_1", "evidence");
        Assert.Equal("/api/v2/optimizations/opt%201/feedback_links", api.Requests[0].Uri.AbsolutePath);
        Assert.Equal("{\"feedback_id\":\"fb_1\"}", api.Requests[0].Body);
        Assert.Equal("{\"feedback_id\":\"fb_1\",\"note\":null}", api.Requests[1].Body);
        Assert.Equal("{\"feedback_id\":\"fb_1\",\"note\":\"evidence\"}", api.Requests[2].Body);
        Assert.Equal("lnk_1", link!.Id);
    }

    [Fact]
    public async Task BulkLinksInBatchesOf100AndMergesCounts()
    {
        var api = new FakeHandler
        {
            Responder = (_, body) =>
            {
                var ids = JsonNode.Parse(body!)!["feedback_ids"]!.AsArray();
                return FakeHandler.Json(200, "{\"linked\":" + (ids.Count - 1) + ",\"already_linked\":1,\"errored\":0,\"not_found\":[\"" + ids[0] + "\"]}");
            },
        };
        var ids = Enumerable.Range(0, 250).Select(i => "fb_" + i).ToList();
        var result = await new OptimizationFeedbackLinkService(Config(api)).BulkLinkFeedbackAsync("opt_1", ids, "n");
        Assert.Equal(3, api.Requests.Count);
        Assert.Equal(new[] { 100, 100, 50 }, api.Requests.Select(r => r.Json["feedback_ids"]!.AsArray().Count));
        Assert.Equal("n", api.Requests[0].Json["note"]!.GetValue<string>());
        Assert.Equal(new BulkLinkFeedbackResult(247, 3, 0, new[] { "fb_0", "fb_100", "fb_200" }).NotFound, result!.NotFound);
        Assert.Equal(247, result.Linked);
        Assert.Equal(3, result.AlreadyLinked);
    }

    [Fact]
    public async Task BulkLinkRejectsMalformedResults()
    {
        var api = new FakeHandler { Responder = (_, _) => FakeHandler.Json(200, "{\"linked\":\"1\"}") };
        var e = await Assert.ThrowsAsync<CoolhandException>(() => new OptimizationFeedbackLinkService(Config(api)).BulkLinkFeedbackAsync("o", new[] { "f" }));
        Assert.Contains("not a valid bulk result", e.Message);
    }

    [Fact]
    public void LinkArgumentsAreValidated()
    {
        var service = new OptimizationFeedbackLinkService(Config(new FakeHandler()));
        Assert.Throws<ArgumentException>(() => { _ = service.LinkFeedbackAsync("..", "f"); });
        Assert.Throws<ArgumentException>(() => { _ = service.LinkFeedbackAsync("o", " "); });
        Assert.Throws<ArgumentException>(() => { _ = service.BulkLinkFeedbackAsync("o", System.Array.Empty<string>()); });
        Assert.Throws<ArgumentException>(() => { _ = service.BulkLinkFeedbackAsync("o", new[] { "f", "" }); });
        Assert.Throws<ArgumentException>(() => { _ = service.UnlinkFeedbackAsync("o", "."); });
    }

    [Fact]
    public async Task UnlinksByLinkIdAndSkipsInDryRun()
    {
        var api = new FakeHandler { Responder = (_, _) => FakeHandler.Json(200, "{}") };
        await new OptimizationFeedbackLinkService(Config(api)).UnlinkFeedbackAsync("opt_1", "lnk/1");
        Assert.Equal(HttpMethod.Delete, api.Requests[0].Method);
        Assert.Equal("/api/v2/optimizations/opt_1/feedback_links/lnk%2F1", api.Requests[0].Uri.AbsolutePath);
        await new OptimizationFeedbackLinkService(Config(api, dryRun: true)).UnlinkFeedbackAsync("opt_1", "lnk_1");
        Assert.Single(api.Requests);
    }

    [Fact]
    public async Task McpCallsATool()
    {
        var api = new FakeHandler { Responder = (_, _) => FakeHandler.Json(200, "{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{\"content\":[]}}") };
        var result = await new McpService(Config(api)).McpCallAsync("search_logs", new Dictionary<string, object?> { ["q"] = "x" });
        Assert.Equal("/mcp", api.Requests[0].Uri.AbsolutePath);
        Assert.Equal("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"search_logs\",\"arguments\":{\"q\":\"x\"}}}", api.Requests[0].Body);
        Assert.NotNull(result!["content"]);
    }

    [Theory]
    [InlineData("{\"error\":{\"message\":\"bad tool ch_key\"}}", "MCP error: bad tool [redacted]")]
    [InlineData("{\"result\":{\"error\":\"tool failed\"}}", "tool failed")]
    [InlineData("[1]", "MCP response was not a JSON object")]
    [InlineData("nope", "MCP response was not valid JSON")]
    public async Task McpErrorsThrow(string body, string message)
    {
        var api = new FakeHandler { Responder = (_, _) => FakeHandler.Json(200, body) };
        var e = await Assert.ThrowsAsync<CoolhandException>(() => new McpService(Config(api)).McpCallAsync("t"));
        Assert.Contains(message, e.Message);
    }

    [Fact]
    public async Task McpTreatsErrorFalseAsNoErrorAndSkipsInDryRun()
    {
        var api = new FakeHandler { Responder = (_, _) => FakeHandler.Json(200, "{\"error\":false,\"result\":{\"ok\":true}}") };
        Assert.True((await new McpService(Config(api)).McpCallAsync("t"))!["ok"]!.GetValue<bool>());
        var dry = new FakeHandler();
        Assert.Null(await new McpService(Config(dry, dryRun: true)).McpCallAsync("t"));
        Assert.Empty(dry.Requests);
    }
}
