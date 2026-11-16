using CoolhandLabs.Models;
using CoolhandLabs.Services;
using CoolhandLabs.Tests.Support;

namespace CoolhandLabs.Tests;

/// <summary>Real HTTP against a real Coolhand server, no mocks. Skipped unless the live variables are set.</summary>
public class SavingsLiveTests
{
    private static SavingsService Service(string? apiKey = null) => new(new ServiceConfig
    {
        ApiKey = apiKey ?? Environment.GetEnvironmentVariable(LiveFactAttribute.ApiKeyVariable),
        BaseUrl = Environment.GetEnvironmentVariable(LiveFactAttribute.BaseUrlVariable),
    });

    [LiveFact]
    public async Task ReturnsTheDocumentedShape()
    {
        var savings = await Service().GetRealizedSavingsAsync();

        var total = savings["total"]!.GetValue<double>();
        var baseline = savings["cost_at_previous_versions"]!.GetValue<double>();
        Assert.True(total <= baseline);
        Assert.InRange(savings["percent"]!.GetValue<double>(), 0.0, 100.0);
        foreach (var row in savings["workloads"]!.AsArray())
        {
            Assert.NotNull(row!["id"]);
            Assert.NotNull(row["name"]);
            Assert.NotNull(row["savings"]);
            Assert.NotNull(row["cost_at_previous_versions"]);
            Assert.NotNull(row["active_requests"]);
        }
    }

    [LiveFact]
    public async Task ABogusKeyIsRejectedWith401()
    {
        var e = await Assert.ThrowsAsync<CoolhandHttpException>(() => Service("ch_priv_not_a_real_key").GetRealizedSavingsAsync());

        Assert.Equal(401, e.StatusCode);
    }
}
