# Reading Workloads

`SearchWorkloadsAsync(new SearchWorkloadsParams { Search, IncludeArchived, IncludeSystem, IncludeTemplates, Metrics, Page, Per })`
lists `/api/v2/workloads` (private API key). `IncludeTemplates` embeds each workload's templates;
`Metrics` takes the same `MetricsWindow` as templates (see [template-search.md](template-search.md#metrics)),
adding `total_cost` and the dashboard's performance numbers. Pagination is read from the `X-*` headers.

Per-log cost is on every `SearchLogsAsync` row as `cost` (USD, `null` when it can't be priced); filter
and sort on it with `MinCost` and `Order = "cost_desc"`.
