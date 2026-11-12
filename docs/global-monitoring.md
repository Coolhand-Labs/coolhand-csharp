# Global Monitoring Guide

## How capture works

Every captured call goes through a `CoolhandHandler`, a `DelegatingHandler`. Put it in a pipeline in
one of three ways:

| Way | Covers |
|-----|--------|
| `coolhand.CreateHttpClient(inner)` / `new HttpClient(new CoolhandHandler(coolhand, inner))` | that client |
| `services.AddCoolhand()` | every client `IHttpClientFactory` creates (named, typed, default) |
| `CoolhandAnthropic.CreateClient(...)` / `options.WithCoolhand()` | an Anthropic SDK client (appended to `ClientOptions.Handlers`, innermost) |

`new CoolhandHandler()` (no client) resolves `Coolhand.Global` per request and is a pass-through
while there is none. `Coolhand.Global` is created once from the environment when `COOLHAND_API_KEY`
is set, or installed with `Coolhand.InitializeGlobalMonitoring(options)`.

For each request the handler:

1. matches the URL against the provider patterns, and skips it when it matches none, is excluded
   (`ExcludeApiPatterns`), is the Coolhand API itself, or is an Anthropic non-inference endpoint
   (telemetry, MCP directory listing, managed-agent polling);
2. snapshots the declared context and the redacted URL and headers;
3. copies the request body as the transport sends it (nothing is buffered or re-read);
4. copies the response body as your code reads it, whether it buffers (`ReadAsStringAsync`,
   `ResponseContentRead`) or streams (`ResponseHeadersRead`, `ReadAsStreamAsync`);
5. logs the call exactly once, in the background, when the body ends.

A transport failure (no response) is not logged, as in coolhand-node. Anything that goes wrong inside
capture is logged (when not silent) and the request carries on untouched.

## Abandoned streams

| What happens | Logged as |
|--------------|-----------|
| The body is read to the end | complete |
| A streamed body (`text/event-stream`, NDJSON, JSONL, AWS event stream) is disposed before the end | `stream_incomplete: true`, with what was read |
| A read fails or is cancelled | `stream_incomplete: true`, with what was read |
| Any other body is disposed early, or never read | the rest is read in the background (up to 50 MiB), then logged complete |
| Nothing is read for `CaptureIdleTimeout` (30 s) | `stream_incomplete: true`, with what was read |

Compressed responses (gzip, deflate, Brotli) are decoded for the log when the inner handler did not
already decompress them; a partial body is decoded as far as the bytes allow. Bodies are capped at
50 MiB.

## Redaction

- **Headers:** `authorization`, `api-key`, `x-api-key`, `cookie`, `set-cookie`,
  `proxy-authorization`, `openai-api-key`, `x-goog-api-key`, `cf-aig-authorization`,
  `x-amz-security-token`, `ocp-apim-subscription-key`, `subscription-key`, `xi-api-key`, and any name
  containing `auth`, `key`, `token`, `secret`, `cookie`, `passw`, `credential`, `bearer`, `jwt` or
  `signature` (rate-limit headers excepted); then a pattern's own `headers` rules.
- **URLs:** userinfo is dropped and credential query parameters (`key`, `api_key`, `token`,
  `signature`, `X-Amz-Signature`, `client_secret`, ...) become `[REDACTED]`.
- **Bodies:** JSON, NDJSON, form-encoded, multipart and truncated text. String values under
  credential names (`api_key`, `password`, `access_token`, `secret`, `GITHUB_TOKEN`, ... matched
  case- and `_`/`-`-insensitively, including `user[password]`), anything credential-like inside Azure
  "On Your Data" `data_sources`, MCP server `authorization`/`headers`, `authorization_token` and
  `client_secret`. Tool schemas and message content are left alone; `pwd` is not redacted in bodies.
  Redaction fails closed: a body it cannot process is logged as `null`.

## Provider patterns

`api-patterns.json` is coolhand-node's file, copied verbatim. A `domains` entry matches that host and
its subdomains on every path; `*` stands for one DNS label. With `requiresPathMatch` a domain (or one
of `ports`, on any host) also needs one of `paths`. `allowPathMatchAcrossDomains` lets `paths` match
hosts no pattern's domains matched. Domains made only of `*` and `.` are ignored with a warning.

## Troubleshooting

`coolhand.GetStats()` returns `TotalRequests` (seen by the handler) and `InterceptedCalls` (captured).
In your own tests, `CoolhandTestUtils.CaptureInterceptionSnapshot(coolhand)` and
`AssertInterceptionOccurred(coolhand, before)` prove a code path is monitored. When nothing is
captured, check that the `HttpClient` really has the handler (a client built with `new HttpClient()`
does not), that the URL matches a pattern, and that it is not excluded. Set `Silent = false` (or
`COOLHAND_SILENT=false`) to see each decision.
