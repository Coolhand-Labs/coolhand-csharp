using System.Text.Json.Nodes;
using CoolhandLabs.Models;
using CoolhandLabs.Testing;
using CoolhandLabs.Tests.Support;

namespace CoolhandLabs.Tests;

public class CoolhandTests
{
    [Fact]
    public void RequiresAnApiKey()
    {
        using var _ = new LogCapture();
        Assert.Throws<ArgumentException>(() => new Coolhand(new CoolhandOptions()));
        Assert.Throws<ArgumentException>(() => new Coolhand(new CoolhandOptions { ApiKey = "  " }));
        Assert.Throws<ArgumentException>(() => new Coolhand(new CoolhandOptions { ApiKey = "k\n" + "x" }));
        Assert.Throws<ArgumentException>(() => new Coolhand(new CoolhandOptions { ApiKey = "k", BaseUrl = "http://evil.example" }));
    }

    [Fact]
    public void CollectorAndUserAgentCarryTheAssemblyVersion()
    {
        Assert.Equal("0.1.0", CoolhandVersion.PackageVersion);
        Assert.Equal("coolhand-csharp-0.1.0", Collector.GetCollectorString());
        Assert.Equal("coolhand-csharp-0.1.0-manual", Collector.GetCollectorString(CollectionMethod.Manual));
        Assert.Equal("coolhand-csharp-0.1.0-global-monitoring", Collector.GetCollectorString(CollectionMethod.GlobalMonitoring));
        Assert.Equal("coolhand-csharp-0.1.0-auto-monitor", Collector.GetCollectorString(CollectionMethod.AutoMonitor));
        Assert.Equal("coolhand-csharp/0.1.0", CoolhandVersion.UserAgent);
    }

    [Fact]
    public void ReadsOptionsFromTheEnvironment()
    {
        var options = CoolhandOptions.FromEnvironment(new Dictionary<string, string?>
        {
            ["COOLHAND_API_KEY"] = "k", ["COOLHAND_SILENT"] = "false", ["COOLHAND_DEBUG"] = "true", ["COOLHAND_DRY_RUN"] = "true",
            ["COOLHAND_BASE_URL"] = "https://self.example", ["COOLHAND_PATTERNS_FILE"] = "/p.json",
            ["COOLHAND_EXCLUDE_API_PATTERNS"] = " /a , ,/b ",
        });
        Assert.Equal("k", options.ApiKey);
        Assert.False(options.Silent);
        Assert.True(options.Debug);
        Assert.True(options.DryRun);
        Assert.Equal("https://self.example", options.BaseUrl);
        Assert.Equal("/p.json", options.PatternsFile);
        Assert.Equal(new[] { "/a", "/b" }, options.ExcludeApiPatterns);

        var defaults = CoolhandOptions.FromEnvironment(new Dictionary<string, string?> { ["COOLHAND_API_KEY"] = "  ", ["COOLHAND_EXCLUDE_API_PATTERNS"] = " , " });
        Assert.False(defaults.HasApiKey);
        Assert.True(defaults.Silent);
        Assert.False(defaults.DryRun);
        Assert.Null(defaults.ExcludeApiPatterns);
    }

    [Fact]
    public async Task LogRequestSubmitsAManualCallWithTheActiveContext()
    {
        await using var coolhand = new FakeCoolhand();
        coolhand.Api.Responder = (_, _) => FakeHandler.Json(201, "{\"id\":\"log_1\"}");
        var result = await Coolhand.WithContextAsync(new DeclaredContext().WithWorkload("w"), () =>
            coolhand.Client.LogRequestAsync(new CallData { Url = "https://api.openai.com/v1/chat/completions", StreamIncomplete = true },
                metadata: new Dictionary<string, object?> { ["project_path"] = "/srv" }));
        Assert.Equal("log_1", result!.Id);
        var log = coolhand.Logs.Single();
        Assert.EndsWith("-manual", log["collector"]!.GetValue<string>());
        Assert.Equal("w", log["declared"]!["workload"]!["key"]!.GetValue<string>());
        Assert.Equal("/srv", log["metadata"]!["project_path"]!.GetValue<string>());
        Assert.True(log["raw_request"]!["stream_incomplete"]!.GetValue<bool>());
    }

    [Fact]
    public async Task ExposesEveryServiceThroughTheClient()
    {
        await using var coolhand = new FakeCoolhand();
        var lists = new HashSet<string>
        {
            "/api/v2/llm_request_logs", "/api/v2/llm_request_templates", "/api/v2/workloads", "/api/v2/step_templates",
            "/api/v2/llm_references", "/api/v2/client_files", "/api/v2/public_content",
        };
        coolhand.Api.Responder = (req, _) => FakeHandler.Json(200, lists.Contains(req.RequestUri!.AbsolutePath) ? "[]" : "{\"id\":\"x\"}");
        var c = coolhand.Client;
        await c.SearchLogsAsync();
        await c.SearchTemplatesAsync();
        await c.SearchWorkloadsAsync();
        await c.ListStepTemplatesAsync();
        await c.SearchReferencedFilesAsync();
        await c.ListClientFilesAsync();
        await c.SearchHelpArticlesAsync();
        await c.GetTemplateAsync("t");
        await c.GetFeedbackAsync("f");
        await c.GetLogContentAsync("l");
        await c.GetStepTemplateAsync(1);
        await c.GetHelpArticleAsync("s");
        await c.LinkFeedbackAsync("o", "f");
        Assert.Equal(new[]
        {
            "/api/v2/llm_request_logs", "/api/v2/llm_request_templates", "/api/v2/workloads", "/api/v2/step_templates", "/api/v2/llm_references",
            "/api/v2/client_files", "/api/v2/public_content", "/api/v2/llm_request_templates/t", "/api/v2/llm_request_log_feedbacks/f",
            "/api/v2/llm_request_logs/l", "/api/v2/step_templates/1", "/api/v2/public_content/s", "/api/v2/optimizations/o/feedback_links",
        }, coolhand.Api.Requests.Select(r => r.Uri.AbsolutePath));
        Assert.All(coolhand.Api.Requests, r => Assert.Equal("ch_test_key", r.Header("x-api-key")));
    }

    [Fact]
    public async Task SubmissionsAreTrackedUntilFlushed()
    {
        await using var coolhand = new FakeCoolhand();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        coolhand.Api.Gate = release.Task;
        coolhand.Api.Responder = (_, _) => FakeHandler.Json(201, "{\"id\":\"fb\"}");
        var pending = coolhand.Client.CreateFeedbackAsync(new LlmRequestLogFeedback { Explanation = "x" });
        Assert.False(await coolhand.Client.FlushAsync(TimeSpan.FromMilliseconds(50)));
        release.SetResult();
        Assert.True(await coolhand.Client.FlushAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("fb", (await pending)!.Id);
    }

    [Fact]
    public void SanitizeHeadersIsExposed() =>
        Assert.Equal("[REDACTED]", new FakeCoolhand().Client.SanitizeHeaders(new Dictionary<string, string> { ["Authorization"] = "Bearer x" })["authorization"]);

    [Fact]
    public async Task SetupMessagesHonourSilent()
    {
        using var logs = new LogCapture();
        await using (new FakeCoolhand()) { }
        Assert.DoesNotContain("Coolhand ready", logs.All);
        await using (new FakeCoolhand(o => o with { Silent = false, DryRun = true, Debug = true })) { }
        Assert.Contains("✅ Coolhand ready - will log to API", logs.All);
        Assert.Contains("DRY RUN MODE", logs.All);
        Assert.Contains("DEBUG MODE", logs.All);
    }

    [Fact]
    public void TestUtilsReportMissingInterceptions()
    {
        var coolhand = new FakeCoolhand().Client;
        var before = CoolhandTestUtils.CaptureInterceptionSnapshot(coolhand);
        var e = Assert.Throws<InterceptionAssertionException>(() => CoolhandTestUtils.AssertInterceptionOccurred(coolhand, before));
        Assert.Contains("did not increase (before: 0, after: 0)", e.Message);
        Assert.Equal("custom", Assert.Throws<InterceptionAssertionException>(() => CoolhandTestUtils.AssertInterceptionOccurred(coolhand, before, "custom")).Message);
    }

    [Fact]
    public void TheLogSinkIsReplaceableAndNeverThrows()
    {
        var previous = CoolhandLog.Sink;
        try
        {
            CoolhandLog.Sink = (_, _) => throw new InvalidOperationException("sink broke");
            Internal.Log.Warn("still fine");
            CoolhandLog.Sink = null;
            Internal.Log.Error("dropped");
            CoolhandLog.UseConsole();
            Assert.NotNull(CoolhandLog.Sink);
        }
        finally
        {
            CoolhandLog.Sink = previous;
        }
    }
}

[Collection("global")]
public class GlobalMonitoringTests : IDisposable
{
    private readonly string? _savedKey = Environment.GetEnvironmentVariable("COOLHAND_API_KEY");

    public GlobalMonitoringTests() => Coolhand.ResetGlobalMonitoring();

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("COOLHAND_API_KEY", _savedKey);
        Coolhand.ResetGlobalMonitoring();
    }

    [Fact]
    public void WithoutAKeyThereIsNoGlobalClient()
    {
        Environment.SetEnvironmentVariable("COOLHAND_API_KEY", null);
        Assert.False(Coolhand.IsConfigured);
        Assert.Null(Coolhand.Global);
        Assert.False(Coolhand.IsGlobalMonitoringActive);
        Assert.Equal(new CoolhandStats(0, 0, "Not initialized"), Coolhand.GetGlobalStats());
        Assert.Equal(0, CoolhandTestUtils.CaptureInterceptionSnapshot().InterceptedCalls);
    }

    [Fact]
    public void TheEnvironmentKeyCreatesTheGlobalClientOnce()
    {
        Environment.SetEnvironmentVariable("COOLHAND_API_KEY", "ch_env_key");
        Assert.True(Coolhand.IsConfigured);
        var first = Coolhand.Global;
        Assert.NotNull(first);
        Assert.Same(first, Coolhand.Global);
        Assert.True(Coolhand.IsGlobalMonitoringActive);
        Assert.Equal("https://coolhandlabs.com/api/v2/llm_request_logs", Coolhand.GetGlobalStats().ApiEndpoint);
    }

    [Fact]
    public void InitializeGlobalMonitoringWinsAndIsIdempotent()
    {
        Environment.SetEnvironmentVariable("COOLHAND_API_KEY", null);
        var installed = Coolhand.InitializeGlobalMonitoring(new CoolhandOptions { ApiKey = "a" });
        Assert.Same(installed, Coolhand.InitializeGlobalMonitoring(new CoolhandOptions { ApiKey = "b" }));
        Assert.Same(installed, Coolhand.Global);
        Assert.True(Coolhand.IsConfigured);
    }

    [Fact]
    public void AnInvalidEnvironmentConfigurationIsLoggedNotThrown()
    {
        using var logs = new LogCapture();
        Environment.SetEnvironmentVariable("COOLHAND_API_KEY", "bad\u0001key");
        Assert.Null(Coolhand.Global);
        Assert.Contains("Failed to initialize global monitoring", logs.All);
    }
}
