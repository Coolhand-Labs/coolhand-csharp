# Reading Logs

All need the **private** API key and throw `CoolhandHttpException` (with `StatusCode`) on a non-2xx
response.

## SearchLogsAsync

`GET /api/v2/llm_request_logs`. Named filters, sent only when set:

| Property | Query | |
|----------|-------|-|
| `TemplateId`, `WorkloadId` | `template_id`, `workload_id` | hashids |
| `SystemPromptContains`, `UserPromptContains` | `system_prompt_contains`, `user_prompt_contains` | substrings |
| `ErrorContains` | `error_contains` | case-insensitive substring of a failed request's error text |
| `Model`, `SourceApi`, `SourceApiResult`, `SourceApplication`, `ProjectPath` | same names, snake_case | |
| `UnmatchedOnly` | `unmatched_only` | only logs matched to no template |
| `DaysBack`, `Since`, `Until` | `days_back`, `since`, `until` | `Since`/`Until` replace `DaysBack`; sent as ISO-8601 UTC |
| `MinCost`, `Order` | `min_cost`, `order` | `Order = "cost_desc"`; both limited to priceable logs |
| `IncludePrompts`, `Sort`, `Page`, `Per`, `IncludeTotal` | `include_prompts`, `q[s]`, `page`, `per`, `include_total` | |

Pagination comes from the `X-Page`/`X-Per-Page`/`X-Total-Count`/`X-Total-Pages` headers; without
them it is estimated from the page returned (`IncludeTotal = true` asks for exact totals). A 422 means
a malformed or inverted window, a bad `MinCost` or an unknown `Order`.

## GetLogContentAsync

`GET /api/v2/llm_request_logs/:id` returns the full input and output. For large logs:
`MaxChars` with `Section` (`Full`, `Beginning`, `End`) slices each field, or `SearchQuery` returns
matching snippets instead (not combinable with the slice options, and must not be blank).
`IncludeThinking` and `IncludeTurnMetrics` (a per-turn breakdown for captured sessions, `null` for
other logs) are opt-in.

## FailureSummaryAsync

`GET /api/v2/llm_request_logs/failure_summary` rolls recent failed requests up by error pattern
(`error_breakdown`, the 50 most frequent) and by template (`by_template`), each pattern with a
`category` (`client_error`, `rate_limit`, `server_error`, `provider_error`, `relayed_upstream`).
`DaysBack` is sent only when given and not validated client-side (the server 422s on zero or
negative). A 504 means the summary took too long; retry with a smaller window. Pass a pattern's
`error_pattern` as `ErrorContains` to list its logs.
