# Reading Templates

Private API key; errors are `CoolhandHttpException`.

`SearchTemplatesAsync(new SearchTemplatesParams { Search, WorkloadId, Status, IncludeDeprecated, IncludeSystem, Metrics, Page, Per })`
lists `/api/v2/llm_request_templates`. Search is a parameter on the list endpoint. The `Unmatched` /
`Ignored API Calls` system buckets are hidden unless `IncludeSystem = true`. Pagination is read from
the `X-*` headers.

`GetTemplateAsync(id, metrics)` returns one template with its prompt patterns (patterns come from
this method only).

## Metrics

`MetricsWindow { IncludeMetrics, DaysBack, Since, Until }` adds a `metrics` object (cost, latency,
token and volume figures). It is off by default on lists and on by default for `GetTemplateAsync`
(pass `IncludeMetrics = false` to skip it). `DaysBack` (default 28, max 365) is ignored when `Since` is
given. The `log_count` aggregate can 504 under load; retry.
