using CoolhandLabs.Services;
using CoolhandLabs.Tests.Support;

namespace CoolhandLabs.Tests;

public class PatternMatchingTests
{
    private readonly PatternMatchingService _service = new(silent: true);

    // Ported from coolhand-node's test/provider-patterns.test.ts.
    public static TheoryData<string, string?> UrlCases() => new()
    {
        { "https://api.openai.com/v1/chat/completions", "OpenAI" },
        { "https://eu.api.openai.com/v1/chat/completions", "OpenAI" },
        { "https://api.openai.com./v1/chat/completions", "OpenAI" },
        { "https://API.OpenAI.com./v1/chat/completions", "OpenAI" },
        { "https://openai.com/blog", null },
        { "https://auth.openai.com/oauth/token", null },
        { "https://platform.openai.com/docs", null },
        { "https://api.anthropic.com/v1/messages", "Anthropic" },
        { "https://generativelanguage.googleapis.com/v1beta/anything", "Google AI" },
        { "https://models.github.ai/whatever", "GitHub Models" },
        { "https://aiplatform.googleapis.com/whatever", "Vertex AI" },
        { "https://openrouter.ai/whatever", "OpenRouter" },
        { "https://opencode.ai/zen/v1/chat/completions", "OpenCode" },
        { "https://gateway.ai.cloudflare.com/v1/acct/gw/openai/chat/completions", "Cloudflare AI Gateway" },
        { "https://api.deepseek.com/chat/completions", "DeepSeek" },
        { "https://api.deepseek.com/v1/chat/completions", "DeepSeek" },
        { "https://api.mistral.ai/v1/chat/completions", "Mistral" },
        { "https://api.mistral.ai/v1/embeddings", "Mistral" },
        { "https://api.perplexity.ai/chat/completions", "Perplexity" },
        { "https://api.x.ai/v1/chat/completions", "xAI" },
        { "https://api.cohere.com/v2/chat", "Cohere" },
        { "https://api.cohere.ai/v2/chat", "Cohere" },
        { "https://api.cohere.com/v2/chat?stream=true", "Cohere" },
        { "https://api.cohere.com/v1/embed", "Cohere" },
        { "https://api.cohere.com/v2/embed", "Cohere" },
        { "https://api.cohere.com/v1/chat", null },
        { "https://api.cohere.com/v1/rerank", null },
        { "https://api.cohere.com/v2/rerank", null },
        { "https://api.cohere.com/v1/embed-jobs", null },
        { "https://api.cohere.com/v1/tokenize", null },
        { "https://api.cohere.com/v1/classify", null },
        { "https://api.cohere.com/v2/chatbots", null },
        { "https://api.cohere.com/", null },
        { "https://evil-api.cohere.com.example.net/v2/chat", null },
        { "https://api.typesafe.ai/v1/systemone", "TypeSafe Jev" },
        { "https://api.typesafe.ai/v1/systemone/run", "TypeSafe Jev" },
        { "https://api.typesafe.ai/v1/systemone?x=1", "TypeSafe Jev" },
        { "https://api.typesafe.ai/v1/models", null },
        { "https://api.typesafe.ai/v1/systemones", null },
        { "https://api.typesafe.ai/", null },
        { "http://localhost:11434/api/chat", "Ollama" },
        { "http://localhost:11434/api/generate", "Ollama" },
        { "http://127.0.0.1:11434/api/embed", "Ollama" },
        { "http://192.168.1.20:11434/api/embeddings", "Ollama" },
        { "http://[::1]:11434/api/chat", "Ollama" },
        { "https://ollama.com/api/chat", "Ollama" },
        { "http://localhost:11434/api/tags", null },
        { "http://localhost:11434/api/pull", null },
        { "http://localhost:11434/", null },
        { "http://localhost:3000/api/chat", null },
        { "http://localhost/api/chat", null },
        { "https://example.com/api/chat", null },
        { "https://example.com/api/generate", null },
        { "https://api.example.com/api/embeddings", null },
        { "https://ollama.com/library/llama3", null },
        { "https://bedrock-runtime.us-east-1.amazonaws.com/model/anthropic.claude-3/invoke", "Bedrock" },
        { "https://bedrock-runtime.eu-west-2.amazonaws.com/model/amazon.nova-lite-v1:0/converse", "Bedrock" },
        { "https://bedrock-runtime.us-west-2.amazonaws.com/model/x/converse-stream", "Bedrock" },
        { "https://bedrock-runtime.us-west-2.amazonaws.com/openai/v1/chat/completions", "Bedrock" },
        { "https://bedrock-runtime-fips.us-gov-west-1.amazonaws.com/model/x/invoke", "Bedrock" },
        { "https://bedrock-runtime.cn-north-1.amazonaws.com.cn/model/x/invoke", "Bedrock" },
        { "https://bedrock-runtime.us-east-1.api.aws/model/x/invoke", "Bedrock" },
        { "https://bedrock-runtime.us-east-1.api.aws/openai/v1/chat/completions", "Bedrock" },
        { "https://bedrock-runtime-fips.us-gov-west-1.api.aws/model/x/converse", "Bedrock" },
        { "https://bedrock-runtime.us-east-1.api.aws/guardrail/abc/version/1/apply", null },
        { "https://bedrock-runtime.api.aws/model/x/invoke", null },
        { "https://bedrock-runtime.us-east-1.api.aws.evil.example/model/x/invoke", null },
        { "https://bedrock-runtime.us-east-1.amazonaws.com/guardrail/abc/version/1/apply", null },
        { "https://bedrock-runtime.us-east-1.amazonaws.com/", null },
        { "https://bedrock.us-east-1.amazonaws.com/model/x/invoke", null },
        { "https://bedrock-runtime.amazonaws.com/model/x/invoke", null },
        { "https://bedrock-runtime.us-east-1.amazonaws.com.evil.example/model/x/invoke", null },
        { "https://notbedrock-runtime.us-east-1.amazonaws.com/model/x/invoke", null },
        { "https://s3.us-east-1.amazonaws.com/model/x/invoke", null },
        { "https://myres.openai.azure.com/openai/deployments/gpt-4o/chat/completions?api-version=2024-10-21", "Azure OpenAI" },
        { "https://myres.openai.azure.us/openai/v1/chat/completions", "Azure OpenAI" },
        { "https://myres.cognitiveservices.azure.com/openai/deployments/gpt-4o/chat/completions", "Azure AI Services" },
        { "https://myres.services.ai.azure.com/models/chat/completions?api-version=2024-05-01-preview", "Azure AI Services" },
        { "https://myres.services.ai.azure.com/api/projects/p1/openai/v1/responses", "Azure AI Services" },
        { "https://myres.cognitiveservices.azure.com/speech/recognition/conversation", null },
        { "https://myres.cognitiveservices.azure.com/", null },
        { "https://myres.cognitiveservices.azure.com.evil.example/openai/deployments/x/chat/completions", null },
        { "https://mymodel.eastus2.models.ai.azure.com/v1/chat/completions", "Azure AI Foundry (Serverless)" },
        { "https://myendpoint.eastus.inference.ml.azure.com/score", "Azure Machine Learning" },
        { "https://api.elevenlabs.io/v1/text-to-speech/abc", "ElevenLabs" },
        { "https://api.elevenlabs.io/v1/convai/conversations", "ElevenLabs" },
        { "https://elevenlabs.io/pricing", null },
        { "https://evil.com/x", null },
        { "not a url", null },
    };

    [Theory]
    [MemberData(nameof(UrlCases))]
    public void MatchesProviderUrls(string url, string? expected) =>
        Assert.Equal(expected, _service.MatchesApiPatternFromUrl(url)?.Pattern.Name);

    [Fact]
    public void ReportsHowAUrlMatched()
    {
        var match = _service.MatchesApiPatternFromUrl("https://eu.api.openai.com/v1/chat/completions")!;
        Assert.Equal("domain", match.MatchType);
        Assert.Equal("api.openai.com", match.MatchValue);
        var byPort = _service.MatchesApiPatternFromUrl("http://192.168.1.20:11434/api/chat")!;
        Assert.Equal("path", byPort.MatchType);
    }

    [Fact]
    public void BundlesTheNodePatternFileVerbatim()
    {
        var names = PatternMatchingService.BundledPatterns().Select(p => p.Name).ToList();
        foreach (var name in new[] { "OpenAI", "Anthropic", "Google AI", "Bedrock", "Ollama", "Azure OpenAI", "ElevenLabs", "TypeSafe Jev" })
        {
            Assert.Contains(name, names);
        }
        Assert.Equal(names.Count, _service.PatternsCount);
    }

    [Fact]
    public void NormalizesMatchPaths()
    {
        Assert.Equal("/v1/systemone", PatternMatchingService.NormalizeMatchPath("//v1//x/../systemone?y=1#f"));
        Assert.Equal("/", PatternMatchingService.NormalizeMatchPath(""));
    }

    private static string WritePatterns(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), "coolhand-patterns-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void LoadsACustomPatternsFileWithWildcardsAndPathRules()
    {
        var path = WritePatterns("{\"patterns\":[{\"name\":\"Mine\",\"domains\":[\"svc.*.example.com\"],\"headers\":{\"x-mine\":\"[HIDDEN]\"}},"
                                 + "{\"name\":\"Paths\",\"domains\":[\"a.example.com\"],\"paths\":[\"/v1/chat\"],\"requiresPathMatch\":true},"
                                 + "{\"name\":\"Anywhere\",\"domains\":[\"none.invalid\"],\"paths\":[\"/model/\"],\"allowPathMatchAcrossDomains\":true}]}");
        var service = new PatternMatchingService(path);
        Assert.Equal(3, service.PatternsCount);
        Assert.Equal("Mine", service.MatchesApiPatternFromUrl("https://svc.eu1.example.com/x")?.Pattern.Name);
        Assert.Equal("Mine", service.MatchesApiPatternFromUrl("https://extra.svc.eu1.example.com/x")?.Pattern.Name);
        Assert.Null(service.MatchesApiPatternFromUrl("https://svc.example.com/x"));
        Assert.Null(service.MatchesApiPatternFromUrl("https://svc.a.b.example.com/x"));
        Assert.Null(service.MatchesApiPatternFromUrl("https://svcXeu1Xexample.com/x"));
        Assert.Null(service.MatchesApiPatternFromUrl("https://svc.eu1.example.com.evil.net/x"));
        Assert.Equal("Paths", service.MatchesApiPatternFromUrl("https://a.example.com/v1/chat")?.Pattern.Name);
        Assert.Null(service.MatchesApiPatternFromUrl("https://a.example.com/v2/chat"));
        Assert.Null(service.MatchesApiPatternFromUrl("https://a.example.com/other"));
        Assert.Equal("Anywhere", service.MatchesApiPatternFromUrl("https://a.example.com//model/x")?.Pattern.Name);
        Assert.Equal("[HIDDEN]", service.SanitizeHeaders(new Dictionary<string, string> { ["X-Mine"] = "v" },
            service.MatchesApiPatternFromUrl("https://svc.eu1.example.com/x")!.Pattern)["x-mine"]);
    }

    [Fact]
    public void IgnoresWildcardOnlyDomainsWithAWarning()
    {
        using var logs = new LogCapture();
        var path = WritePatterns("{\"patterns\":[{\"name\":\"Greedy\",\"domains\":[\"*\",\"*.*\",\"ok.example\"]}]}");
        var service = new PatternMatchingService(path, silent: false);
        Assert.Equal(new[] { "ok.example" }, service.LoadedPatterns[0].Domains);
        Assert.Null(service.MatchesApiPatternFromUrl("https://anything.example/"));
        Assert.Contains("would match every host", logs.All);
    }

    [Theory]
    [InlineData("{\"patterns\":{}}")]
    [InlineData("{\"patterns\":[{\"domains\":\"x\"}]}")]
    [InlineData("{\"patterns\":[{\"domains\":[\"x\"],\"ports\":[1.5]}]}")]
    [InlineData("{\"patterns\":[{\"domains\":[\"x\"],\"paths\":[1]}]}")]
    [InlineData("not json")]
    public void FallsBackToTheBundledPatternsForAMalformedFile(string json)
    {
        using var logs = new LogCapture();
        var service = new PatternMatchingService(WritePatterns(json), silent: false);
        Assert.Equal(PatternMatchingService.BundledPatterns().Count, service.PatternsCount);
        Assert.Contains("Error loading API patterns", logs.All);
    }

    [Fact]
    public void FallsBackForAMissingFile()
    {
        using var logs = new LogCapture();
        var service = new PatternMatchingService("/nonexistent/patterns.json", silent: false);
        Assert.Equal(PatternMatchingService.BundledPatterns().Count, service.PatternsCount);
        Assert.Contains("API patterns file not found", logs.All);
    }
}
