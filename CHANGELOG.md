# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.1.0] - 2026-11-12

First release, modelled on coolhand-node 0.18.0.

### ✨ Features
- **Capture** of LLM API calls through `CoolhandHandler`, a `DelegatingHandler` for any `HttpClient`: request and response bodies (streamed responses included), redacted headers and URLs, decompression (gzip, deflate, Brotli), a 50 MiB cap, and `stream_incomplete: true` with the partial body when a response is abandoned, fails mid-stream or sits unread past `CaptureIdleTimeout`. Each call is logged exactly once, in the background.
- **Integrations:** `CoolhandAnthropic` for the official Anthropic C# SDK (`CoolhandLabs.Coolhand.Anthropic`), and `services.AddCoolhand()` / `AddCoolhandHandler()` for `IHttpClientFactory` (`CoolhandLabs.Coolhand.Extensions.Http`).
- **Provider patterns** identical to coolhand-node 0.18.0 (Bedrock dual-stack hosts included), custom patterns files, `ExcludeApiPatterns`, the self-endpoint guard and the non-inference filter.
- **Redaction** of credentials in headers, query strings, JSON, NDJSON, form-encoded, multipart and truncated bodies, matching coolhand-node.
- **Declared workloads and templates** with `Coolhand.WithContext` / `WithContextAsync` (`AsyncLocal`), and `UpsertWorkloadAsync` / `UpsertTemplateAsync`.
- **Feedback:** `CreateFeedbackAsync`, `SearchFeedbackAsync`, `GetFeedbackAsync`.
- **Logs:** `LogRequestAsync`, `SearchLogsAsync` (with `ErrorContains`), `GetLogContentAsync` (with `IncludeTurnMetrics`), `FailureSummaryAsync`.
- **Reads:** templates, step templates, workloads with cost metrics, referenced files, the help center (no key needed), client files, optimization feedback links and the MCP endpoint.
- **Dry-run, debug and silent modes**, environment configuration (`COOLHAND_*`), `Coolhand.Global` and `InitializeGlobalMonitoring`, `CoolhandTestUtils` for interception assertions, and `CoolhandLog.Sink`.
