using System.Text.Json.Nodes;
using CoolhandLabs.Models;
using CoolhandLabs.Services;
using CoolhandLabs.Tests.Support;

namespace CoolhandLabs.Tests;

public class SavingsTests
{
    private const string Body =
        "{\"total\":4.5372,\"cost_at_previous_versions\":5.6912,\"percent\":79.7,\"workloads\":["
        + "{\"id\":\"xwjm0lgsdmy4\",\"name\":\"Suggester\",\"savings\":2.0437,\"cost_at_previous_versions\":3.0402,\"active_requests\":182}]}";

    private static ServiceConfig Config(FakeHandler api) =>
        new() { ApiKey = "ch_key", BaseUrl = "https://coolhand.test", HttpMessageHandler = api };

    private static FakeHandler Returning(int status, string json) => new() { Responder = (_, _) => FakeHandler.Json(status, json) };

    [Fact]
    public async Task ReadsTheSingletonWithTheKeyAndNoQuery()
    {
        var api = Returning(200, Body);
        var savings = await new SavingsService(Config(api)).GetRealizedSavingsAsync();

        var request = api.Requests.Single();
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/api/v2/savings", request.Uri.PathAndQuery);
        Assert.Equal("ch_key", request.Header("x-api-key"));
        Assert.Equal(4.5372, savings["total"]!.GetValue<double>());
        Assert.Equal(79.7, savings["percent"]!.GetValue<double>());
        Assert.Equal("xwjm0lgsdmy4", savings["workloads"]![0]!["id"]!.GetValue<string>());
        Assert.Equal(182, savings["workloads"]![0]!["active_requests"]!.GetValue<int>());
    }

    [Fact]
    public async Task NothingToCompareIsRealZeroSavings()
    {
        var api = Returning(200, "{\"total\":0.0,\"cost_at_previous_versions\":0.0,\"percent\":0.0,\"workloads\":[]}");
        var savings = await new SavingsService(Config(api)).GetRealizedSavingsAsync();

        Assert.Equal(0.0, savings["total"]!.GetValue<double>());
        Assert.Empty(savings["workloads"]!.AsArray());
    }

    [Fact]
    public async Task AComparisonThatCouldNotBeComputedThrowsAndIsNeverZeros()
    {
        var api = Returning(503, "{\"errors\":{\"system\":[\"Realized savings could not be computed right now. Please retry.\"]}}");
        var e = await Assert.ThrowsAsync<CoolhandHttpException>(() => new SavingsService(Config(api)).GetRealizedSavingsAsync());

        Assert.Equal(503, e.StatusCode);
        Assert.Contains("Savings request failed (503)", e.Message);
    }

    [Theory]
    [InlineData("{\"error\":\"API key is required\"}")]
    [InlineData("{\"error\":\"Invalid API key\"}")]
    public async Task AMissingInvalidOrPublicKeyThrowsWith401(string body)
    {
        var api = Returning(401, body);
        var e = await Assert.ThrowsAsync<CoolhandHttpException>(() => new SavingsService(Config(api)).GetRealizedSavingsAsync());

        Assert.Equal(401, e.StatusCode);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("\"text\"")]
    public async Task ABodyThatIsNotAJsonObjectThrowsWithoutAStatus(string body)
    {
        var api = Returning(200, body);
        var e = await Assert.ThrowsAsync<CoolhandException>(() => new SavingsService(Config(api)).GetRealizedSavingsAsync());

        Assert.IsNotType<CoolhandHttpException>(e);
    }

    [Fact]
    public async Task TheFacadeDelegatesToTheService()
    {
        var api = Returning(200, Body);
        using var coolhand = new Coolhand(new CoolhandOptions { ApiKey = "ch_key", BaseUrl = "https://coolhand.test", HttpMessageHandler = api, Silent = true });

        var savings = await coolhand.GetRealizedSavingsAsync();

        Assert.Equal("/api/v2/savings", api.Requests.Single().Uri.AbsolutePath);
        Assert.Equal(4.5372, savings["total"]!.GetValue<double>());
    }
}
