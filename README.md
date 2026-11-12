# Coolhand .NET SDK

Monitor and log LLM API calls from .NET applications to the [Coolhand](https://coolhandlabs.com)
analytics platform: OpenAI, Anthropic, Google AI (Gemini), Vertex AI, Azure OpenAI, Azure AI
Services and AI Foundry, Amazon Bedrock, GitHub Models, OpenRouter, OpenCode, Cloudflare AI
Gateway, DeepSeek, Mistral, Perplexity, xAI, Cohere, Ollama, ElevenLabs, TypeSafe Jev and more.
Capture works with any `HttpClient`, with `IHttpClientFactory`, and with the official Anthropic C#
SDK, and the same client submits feedback, declares workloads and reads logs, feedback, templates
and workloads back.

`coolhand-csharp` is the .NET member of the Coolhand SDK family. Its behaviour, wire format,
provider patterns and redaction follow [coolhand-node](https://github.com/Coolhand-Labs/coolhand-node)
(the reference implementation, 0.18.0), with a .NET API: `async`/`await` with `CancellationToken`s,
records with `init` properties, nullable reference types and a `DelegatingHandler`.

## Related Packages

| Package | Environment | Purpose |
|---------|-------------|---------|
| `CoolhandLabs.Coolhand` | .NET 8+ | Server-side monitoring and logging of LLM API calls (no dependencies) |
| `CoolhandLabs.Coolhand.Anthropic` | .NET 8+ | Monitored clients for the official `Anthropic` NuGet package |
| `CoolhandLabs.Coolhand.Extensions.Http` | .NET 8+ | `services.AddCoolhand()` for `IHttpClientFactory` |
| [`coolhand-node`](https://github.com/Coolhand-Labs/coolhand-node) | Node.js | The same, for Node.js |
| [`coolhand-java`](https://github.com/Coolhand-Labs/coolhand-java) | JVM | The same, for Java |
| [`coolhand`](https://github.com/Coolhand-Labs/coolhand-python) (Python) | Python | The same, for Python |
| [`coolhand`](https://github.com/Coolhand-Labs/coolhand-js) (browser) | Browser | Feedback widget for collecting user sentiment on AI outputs |

## Installation

.NET 8 or later. Until the packages are on NuGet, build them from this repository:

```bash
git clone https://github.com/Coolhand-Labs/coolhand-csharp.git
dotnet pack coolhand-csharp/Coolhand.sln -c Release -o ./packages
dotnet add package CoolhandLabs.Coolhand --source ./packages
dotnet add package CoolhandLabs.Coolhand.Anthropic --source ./packages   # if you use the Anthropic SDK
```

The core package has no dependencies beyond the .NET base library.

## Getting Started

1. **Get an API key** at [coolhandlabs.com](https://coolhandlabs.com/).
2. **Add the package** above.
3. **Set `COOLHAND_API_KEY`** in your environment.
4. **Send your LLM traffic through a monitored client** (see below).
5. **Deploy**: your LLM calls now show up in Coolhand.

## Quick Start

.NET has no safe way to patch every `HttpClient` in a process, so monitoring is explicit: LLM calls
are captured by a `CoolhandHandler` in the client's handler pipeline. With `COOLHAND_API_KEY` set the
integrations use a process-wide client created from the environment (the equivalent of
coolhand-node's `auto-monitor`); without it they hand back plain, unmonitored clients, so the same
code runs with and without Coolhand.

### Anthropic C# SDK

```csharp
using CoolhandLabs.Anthropic;

AnthropicClient client = CoolhandAnthropic.CreateClient(o => o with { MaxRetries = 3 });
// or, when you build ClientOptions yourself: new AnthropicClient(options.WithCoolhand())
```

### IHttpClientFactory (every factory client in the app)

```csharp
builder.Services.AddCoolhand();                 // from COOLHAND_API_KEY; or AddCoolhand(new CoolhandOptions { ... })
builder.Services.AddHttpClient("openai");       // monitored
```

`services.AddHttpClient("x").AddCoolhandHandler()` monitors a single client instead.

### Any HttpClient

```csharp
var coolhand = new Coolhand(new CoolhandOptions { ApiKey = Environment.GetEnvironmentVariable("COOLHAND_API_KEY") });

using HttpClient http = coolhand.CreateHttpClient();   // or new HttpClient(new CoolhandHandler(coolhand, inner))
```

Make an explicit client the process-wide one (coolhand-node's `initializeGlobalMonitoring`) with
`Coolhand.InitializeGlobalMonitoring(options)`. `Coolhand.IsConfigured` tells you whether Coolhand
can log (a global client exists or `COOLHAND_API_KEY` is set).

A `Coolhand` client is thread-safe and meant to be shared. Captured calls are submitted in the
background; `await coolhand.FlushAsync(TimeSpan.FromSeconds(5))` (or dispose the client) before a
short-lived process exits.

See [docs/global-monitoring.md](docs/global-monitoring.md) for what is captured, streaming behaviour,
redaction and troubleshooting.

## Feedback API

```csharp
FeedbackResponse? created = await coolhand.CreateFeedbackAsync(new LlmRequestLogFeedback
{
    LlmProviderUniqueId = requestId,         // e.g. Anthropic's request-id header, OpenAI's x-request-id
    Sentiment = Sentiment.Dislike,
    Explanation = "Missed the boundary case",
    CreatorType = CreatorType.Human,
});

string? matchedLog = created?.LlmRequestLogId;   // set when the feedback matched a log
```

`CreateFeedbackAsync` never throws for API or network failures: it returns `null` (and logs the
error), as coolhand-node resolves to `null`. Match feedback to a log by `LlmRequestLogId`,
`LlmProviderUniqueId`, `OriginalOutput` (fuzzy) or `ClientUniqueId`.
[docs/feedback.md](docs/feedback.md) has every field and the sentiment conversion.

### Reading Feedback

```csharp
Page<JsonNode> page = await coolhand.SearchFeedbackAsync(new SearchFeedbackParams
{
    Predicates = new Dictionary<string, object?> { ["sentiment_eq"] = 0, ["explanation_cont"] = "too long" },
    Per = 50,
});
JsonNode one = await coolhand.GetFeedbackAsync(page.Items[0]["id"]!.GetValue<string>());
```

Read methods need the **private** API key.

## Reading Logs

```csharp
Page<JsonNode> logs = await coolhand.SearchLogsAsync(new SearchLogsParams { Model = "gpt-4", SourceApiResult = "failed", DaysBack = 7 });
JsonNode content = await coolhand.GetLogContentAsync(logs.Items[0]["id"]!.GetValue<string>());

// Roll failed requests up by error pattern, then list the logs behind one
JsonNode summary = await coolhand.FailureSummaryAsync(new FailureSummaryParams { DaysBack = 7 });
string? top = summary["error_breakdown"]?[0]?["error_pattern"]?.GetValue<string>();
Page<JsonNode> failed = await coolhand.SearchLogsAsync(new SearchLogsParams { ErrorContains = top });
```

See [docs/log-search.md](docs/log-search.md).

## Reading Templates, Step Templates and Workloads

```csharp
var templates = await coolhand.SearchTemplatesAsync(new SearchTemplatesParams { Search = "summar", Status = "published" });
var template = await coolhand.GetTemplateAsync("tpl_abc");                       // prompt patterns included
var steps = await coolhand.ListStepTemplatesAsync(new ListStepTemplatesParams { StepType = "prompt_step", IncludeMetrics = true });
var workloads = await coolhand.SearchWorkloadsAsync(new SearchWorkloadsParams
{
    Metrics = new MetricsWindow { IncludeMetrics = true, DaysBack = 30 },
});
```

See [docs/template-search.md](docs/template-search.md), [docs/step-templates.md](docs/step-templates.md)
and [docs/workload-search.md](docs/workload-search.md).

## Reading the Help Center

```csharp
var helpCenter = new HelpCenterDocsService();                    // no API key required
var articles = await helpCenter.SearchHelpArticlesAsync(new SearchHelpArticlesParams { Query = "match rate", PerPage = 5 });
var article = await helpCenter.GetHelpArticleAsync(articles.Items[0]["slug"]!.GetValue<string>());
```

See [docs/help-center.md](docs/help-center.md).

## Reading Referenced Files

```csharp
var files = await coolhand.SearchReferencedFilesAsync(new SearchReferencedFilesParams { FilePathContains = "routes" });
var sessions = await coolhand.ListReferencedFileSessionsAsync(new ListReferencedFileSessionsParams("src/routes.ts"));
```

See [docs/llm-reference-search.md](docs/llm-reference-search.md).

### Link Feedback to an Optimization

`LinkFeedbackAsync`, `BulkLinkFeedbackAsync` (batches of 100) and `UnlinkFeedbackAsync` attach
feedback to an optimization as evidence. Private API key required. See [docs/feedback-links.md](docs/feedback-links.md).

### Declare Workloads and Templates

Wrap LLM calls in `Coolhand.WithContext(ctx, () => ...)` (or `WithContextAsync`) to tell Coolhand
which workload and template they belong to, skipping log-to-template matching. The context flows
across `await`s. `UpsertWorkloadAsync` and `UpsertTemplateAsync` create or rename them by key
(private API key). See [docs/declared-workloads.md](docs/declared-workloads.md).

```csharp
var ctx = new DeclaredContext { Reporter = "qa-agent" }
    .WithWorkload("qa/test-plans", "QA test plans", "Writes test plans from stories")
    .WithTemplate("qa/test-plans/generate", "generate");
var message = await Coolhand.WithContextAsync(ctx, () => client.Messages.Create(parameters));
```

## Configuration Options

| Option | Type | Default | Description |
|--------|------|---------|-------------|
| `ApiKey` | `string` | *required* | Your Coolhand API key |
| `Silent` | `bool` | `true` | Suppress progress output |
| `Debug` | `bool` | `false` | Verbose logging (endpoint and payload size before each call) |
| `DryRun` | `bool` | `false` | Skip all API submissions; nothing is sent to Coolhand |
| `PatternsFile` | `string?` | `null` | Path to a custom API patterns file |
| `ExcludeApiPatterns` | `IReadOnlyList<string>?` | `DefaultExcludeApiPatterns` | URL substrings (not globs) never captured. See [Excluding endpoints](docs/exclude-patterns.md). |
| `BaseUrl` | `string?` | `https://coolhandlabs.com` | API host for self-hosted deployments. Must be `https://` (or `http://localhost`). |
| `RequestTimeout` | `TimeSpan?` | 30 s | Timeout for Coolhand API calls. Uploads use 10 minutes. |
| `CaptureIdleTimeout` | `TimeSpan` | 30 s | How long a response may sit unread before it is logged as incomplete (`Zero` disables). |
| `HttpMessageHandler` | `HttpMessageHandler?` | shared, no redirects | The handler used to reach Coolhand (proxies, tests). |

### Environment Variables

| Variable | Default | Description |
|----------|---------|-------------|
| `COOLHAND_API_KEY` | *required* | Your Coolhand API key |
| `COOLHAND_SILENT` | `true` | Anything but `false` is silent |
| `COOLHAND_DEBUG` | `false` | `true` enables verbose logging |
| `COOLHAND_DRY_RUN` | `false` | `true` skips all API submissions |
| `COOLHAND_PATTERNS_FILE` | | Path to a custom API patterns file |
| `COOLHAND_BASE_URL` | | Override the API host |
| `COOLHAND_EXCLUDE_API_PATTERNS` | | Comma-separated URL substrings to exclude |

SDK messages go to the console by default; send them to your logger with
`CoolhandLog.Sink = (level, message) => logger.Log(...)`.

## Supported Libraries

- The official Anthropic C# SDK (`Anthropic` on NuGet), streaming included
- The OpenAI .NET SDK, Azure OpenAI, Semantic Kernel and any library that accepts an `HttpClient` or uses `IHttpClientFactory`
- Direct `HttpClient` calls to any provider listed above

## Custom AI Providers

```json
{
  "patterns": [
    { "name": "My Custom AI", "domains": ["api.mycustomai.com"], "headers": { "api-key": "[REDACTED]" } }
  ]
}
```

Pass it as `PatternsFile`. The rules (`domains`, `paths`, `requiresPathMatch`, `ports`,
`allowPathMatchAcrossDomains`, `*` wildcards) are coolhand-node's; see
[docs/global-monitoring.md](docs/global-monitoring.md).

## Error Handling

Write methods (`LogRequestAsync`, `CreateFeedbackAsync`, `UploadClientFileAsync`) never throw for
API or network failures; they return `null` and log the error. Read and admin methods throw
`CoolhandHttpException` (with `StatusCode`) on a non-2xx response and `CoolhandException` on network
failures, redirects and non-JSON bodies. Invalid arguments throw `ArgumentException` before any
request. Cancelling a `CancellationToken` you passed always throws `OperationCanceledException`.

## Differences from coolhand-node

- **No process-wide patching.** Node patches `http`, `https` and `fetch`; .NET cannot safely
  intercept every `HttpClient` (a `DiagnosticListener` sees requests but cannot read bodies without
  consuming them). Capture is a `CoolhandHandler` you add: `CreateHttpClient()`, the Anthropic
  integration, or `AddCoolhand()`, which covers every `IHttpClientFactory` client and is the closest
  equivalent to `auto-monitor`. `Coolhand.Global` (created from `COOLHAND_API_KEY`) plays
  `auto-monitor`'s part for the integrations.
- **Async API.** Every network method is `...Async`, takes a `CancellationToken`, and is
  thread-safe. `FlushAsync` waits for background submissions.
- **Declared context uses `AsyncLocal<T>`**, so it flows across `await` and into tasks started inside
  the block, like `AsyncLocalStorage`.
- **Read results are `System.Text.Json.Nodes.JsonNode`** (as coolhand-java uses Jackson's
  `JsonNode`), plus typed `FeedbackResponse`, `LogResponse`, `ClientFile` and friends where node has
  named interfaces.
- **Collector string and User-Agent** are `coolhand-csharp-<version>-<method>` and
  `coolhand-csharp/<version>`.
- **`LlmRequestLogId`** is a string; a legacy integer id goes in `LlmRequestLogNumericId`.
- **Logging** goes through `CoolhandLog.Sink` instead of `console`.

## Security

- API keys and other credentials in request headers, URL query strings and request and response
  bodies (Azure OpenAI "On Your Data" credentials, MCP tokens, realtime client secrets, OAuth tokens,
  form and multipart fields) are redacted before anything is sent; see
  [docs/global-monitoring.md](docs/global-monitoring.md)
- Your Coolhand key is masked in every error message and log line
- `DryRun = true` sends nothing to Coolhand

## Documentation

- [Global Monitoring Guide](docs/global-monitoring.md): capture, streaming, redaction, troubleshooting
- [Excluding Endpoints](docs/exclude-patterns.md)
- [Manual Submission](docs/manual-submission.md)
- [Feedback](docs/feedback.md)
- [Reading Logs](docs/log-search.md)
- [Reading Templates](docs/template-search.md)
- [Reading Step Templates](docs/step-templates.md)
- [Reading Workloads](docs/workload-search.md)
- [Reading Referenced Files](docs/llm-reference-search.md)
- [Reading the Help Center](docs/help-center.md)
- [Linking Feedback to an Optimization](docs/feedback-links.md)
- [Declared Workloads and Templates](docs/declared-workloads.md)
- [Client Files](docs/client-file-upload.md)

## About Coolhand Labs

[Coolhand Labs](https://coolhandlabs.com) builds observability and feedback tooling for AI-powered
applications: monitor LLM usage, collect structured human feedback, and improve output quality over
time, across every provider and framework.
