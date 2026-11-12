using System.Net;
using CoolhandLabs.Models;
using CoolhandLabs.Services;
using CoolhandLabs.Tests.Support;

namespace CoolhandLabs.Tests;

public class BaseServiceTests
{
    private static ServiceConfig Config(FakeHandler api, string key = "ch_key") =>
        new() { ApiKey = key, BaseUrl = "https://coolhand.test", HttpMessageHandler = api };

    [Theory]
    [InlineData("https://coolhandlabs.com")]
    [InlineData("https://feedback.example.com/")]
    [InlineData("http://localhost:3000")]
    [InlineData("http://127.0.0.1:3000")]
    [InlineData("http://[::1]:3000")]
    public void AcceptsHttpsAndLocalHttpBaseUrls(string baseUrl) =>
        Assert.StartsWith(baseUrl.TrimEnd('/'), new LoggingService(new ServiceConfig { ApiKey = "k", BaseUrl = baseUrl }).ApiEndpoint);

    [Theory]
    [InlineData("http://example.com", "must use https://")]
    [InlineData("https://", "must include a hostname")]
    [InlineData("https://user:pw@example.com", "must not include credentials")]
    [InlineData("https://example.com?x=1", "must not include a query string or fragment")]
    [InlineData("https://example.com/#frag", "must not include a query string or fragment")]
    [InlineData("example.com", "is not a valid URL")]
    public void RejectsUnsafeBaseUrls(string baseUrl, string message)
    {
        var e = Assert.Throws<ArgumentException>(() => new LoggingService(new ServiceConfig { ApiKey = "k", BaseUrl = baseUrl }));
        Assert.Contains(message, e.Message);
    }

    [Fact]
    public void RedactsBaseUrlSecretsInMessages()
    {
        Assert.Equal("https://***@host/x", BaseService.RedactForMessage("https://user:pw@host/x"));
        Assert.Equal("https://host/x…", BaseService.RedactForMessage("https://host/x?token=abc"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bad\nkey")]
    [InlineData("bad\u0001key")]
    public void RejectsMissingOrMalformedKeys(string? key) =>
        Assert.Throws<ArgumentException>(() => new LoggingService(new ServiceConfig { ApiKey = key }));

    [Fact]
    public void TrimsHttpWhitespaceFromTheKey() => Assert.Equal("abc", BaseService.NormalizeApiKey(" \tabc\r\n", false));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(2_147_483_648L)]
    public void RejectsOutOfRangeTimeouts(long ms) =>
        Assert.Throws<ArgumentException>(() => new LoggingService(new ServiceConfig { ApiKey = "k", RequestTimeout = TimeSpan.FromMilliseconds(ms) }));

    [Fact]
    public void RejectsSubMillisecondTimeouts() =>
        Assert.Throws<ArgumentException>(() => new LoggingService(new ServiceConfig { ApiKey = "k", RequestTimeout = TimeSpan.FromTicks(15_000) }));

    [Fact]
    public async Task SendsTheKeyAndUserAgent()
    {
        var api = new FakeHandler { Responder = (_, _) => FakeHandler.Json(200, "[]") };
        await new TemplateService(Config(api)).SearchTemplatesAsync();
        var request = api.Requests.Single();
        Assert.Equal("ch_key", request.Header("x-api-key"));
        Assert.Equal("coolhand-csharp/" + CoolhandVersion.PackageVersion, request.Header("user-agent"));
        Assert.Equal("application/json", request.Header("accept"));
    }

    [Fact]
    public async Task ReadErrorsCarryTheStatusAndMaskTheKey()
    {
        var api = new FakeHandler { Responder = (_, _) => FakeHandler.Json(401, "{\"error\":\"bad key ch_key\"}") };
        var e = await Assert.ThrowsAsync<CoolhandHttpException>(() => new TemplateService(Config(api)).SearchTemplatesAsync());
        Assert.Equal(401, e.StatusCode);
        Assert.Contains("Template request failed (401)", e.Message);
        Assert.Contains("[redacted]", e.Message);
        Assert.DoesNotContain("ch_key", e.Message);
    }

    [Fact]
    public async Task RedirectsAreNotFollowed()
    {
        var api = new FakeHandler
        {
            Responder = (_, _) =>
            {
                var r = new HttpResponseMessage(HttpStatusCode.Found);
                r.Headers.Location = new Uri("https://elsewhere.example/");
                return r;
            },
        };
        var e = await Assert.ThrowsAsync<CoolhandException>(() => new TemplateService(Config(api)).GetTemplateAsync("tpl_1"));
        Assert.Contains("redirect (302) not followed", e.Message);
        Assert.IsNotType<CoolhandHttpException>(e);
    }

    [Fact]
    public async Task NonJsonBodiesThrow()
    {
        var api = new FakeHandler { Responder = (_, _) => FakeHandler.Bytes(200, "<html>"u8.ToArray(), "text/html") };
        var e = await Assert.ThrowsAsync<CoolhandException>(() => new TemplateService(Config(api)).GetTemplateAsync("tpl_1"));
        Assert.Contains("Template response was not valid JSON", e.Message);
    }

    [Fact]
    public async Task NetworkFailuresThrowWithoutLeakingTheKey()
    {
        var api = new FakeHandler { Responder = (_, _) => throw new HttpRequestException("connect failed with ch_key") };
        var e = await Assert.ThrowsAsync<CoolhandException>(() => new TemplateService(Config(api)).GetTemplateAsync("tpl_1"));
        Assert.Contains("Template request failed: connect failed with [redacted]", e.Message);
        Assert.Null(e.InnerException);
    }

    [Fact]
    public async Task TimeoutsSurfaceAsCoolhandExceptions()
    {
        var api = new FakeHandler { Responder = (_, _) => throw new TaskCanceledException("timed out") };
        var e = await Assert.ThrowsAsync<CoolhandException>(() =>
            new TemplateService(Config(api) with { RequestTimeout = TimeSpan.FromMilliseconds(50) }).GetTemplateAsync("tpl_1"));
        Assert.Contains("Template request failed", e.Message);
    }

    [Fact]
    public async Task CallerCancellationPropagates()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var api = new FakeHandler();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new TemplateService(Config(api)).GetTemplateAsync("tpl_1", ct: cts.Token));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(".")]
    [InlineData("..")]
    public void ResourceIdsThatWouldRetargetTheUrlAreRejected(string id) =>
        Assert.Throws<ArgumentException>(() => { _ = new TemplateService(Config(new FakeHandler())).GetTemplateAsync(id); });

    [Fact]
    public async Task ResourceIdsArePercentEncoded()
    {
        var api = new FakeHandler { Responder = (_, _) => FakeHandler.Json(200, "{}") };
        await new FeedbackService(Config(api)).GetFeedbackAsync("a/b c");
        Assert.Equal("/api/v2/llm_request_log_feedbacks/a%2Fb%20c", api.Requests.Single().Uri.AbsolutePath);
    }

    private static ResponseHeaders Headers(params (string, string)[] pairs) =>
        new(pairs.Select(p => new KeyValuePair<string, string>(p.Item1, p.Item2)));

    [Fact]
    public void PaginationComesFromHeadersWhenPresent()
    {
        var p = BaseService.PaginationFromHeaders(Headers(("X-Page", "2"), ("X-Per-Page", "10"), ("X-Total-Count", "35"), ("X-Total-Pages", "4")), 10, 2, 10);
        Assert.Equal(new Pagination(2, 10, 35, 4, true, true), p);
    }

    [Fact]
    public void PaginationComputesMissingTotalPages()
    {
        var p = BaseService.PaginationFromHeaders(Headers(("x-total-count", "21")), 10, null, 10);
        Assert.Equal(new Pagination(1, 10, 21, 3, true, false), p);
    }

    [Fact]
    public void PaginationIsEstimatedWithoutHeaders()
    {
        Assert.Equal(new Pagination(3, 25, 75, 3, true, true), BaseService.PaginationFromHeaders(Headers(), 25, 3, null));
        Assert.Equal(new Pagination(1, 100, 4, 1, false, false), BaseService.PaginationFromHeaders(Headers(), 4, 0, 500));
        Assert.Equal(new Pagination(2, 25, 0, 0, false, true), BaseService.PaginationFromHeaders(Headers(), 0, 2, null));
    }

    [Fact]
    public void PaginationIgnoresGarbageHeaders()
    {
        var p = BaseService.PaginationFromHeaders(Headers(("x-page", "two"), ("x-per-page", "-5"), ("x-total-count", "1e3")), 3, 1, 10);
        Assert.Equal(1, p.CurrentPage);
        Assert.Equal(10, p.PerPage);
        Assert.Equal(3, p.TotalCount);
    }

    [Fact]
    public void BodyPaginationEnvelope()
    {
        Assert.Equal(new Pagination(2, 50, 120, 3, true, true), Pagination.FromJson(System.Text.Json.Nodes.JsonNode.Parse(
            "{\"current_page\":2,\"per_page\":50,\"total_count\":120,\"total_pages\":3,\"has_next_page\":true,\"has_prev_page\":true}")));
        Assert.Equal(new Pagination(1, 0, 0, 0, false, false), Pagination.FromJson(null));
    }
}
