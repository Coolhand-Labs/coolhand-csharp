# Reading Realized Savings

`GetRealizedSavingsAsync()` returns what switching templates or models has saved your client:
what the requests served by the current templates cost, versus what the same requests would have
cost on the deprecated versions they replaced. It is the figure the dashboard shows, and it is
read-only.

```csharp
JsonNode savings = await coolhand.GetRealizedSavingsAsync();       // or new SavingsService(config)
double total = savings["total"]!.GetValue<double>();               // 4.5372 (USD)
double percent = savings["percent"]!.GetValue<double>();           // 79.7
```

**Auth:** the **private** API key. The public key is rejected exactly like an invalid key.

**Scoping and window:** the client is always derived from the key, so there is deliberately no
`clientId` and no parameter of any kind. The comparison covers deprecations from the last three
months, fixed server-side, and is cached for an hour. There is no `DaysBack`.

## Return value

A `JsonNode` object:

```json
{
  "total": 4.5372,
  "cost_at_previous_versions": 5.6912,
  "percent": 79.7,
  "workloads": [
    { "id": "xwjm0lgsdmy4", "name": "Help Center Article Suggester", "savings": 2.0437,
      "cost_at_previous_versions": 3.0402, "active_requests": 182 }
  ]
}
```

Money values are USD numbers rounded to 4 decimals. `percent` is `total` as a share of
`cost_at_previous_versions`, one decimal, `0` when the baseline is 0. `workloads` lists only
workloads with a template deprecated in the last three months, and every row carries all five
keys. A client with nothing to compare gets zeros and an empty `workloads` array, which is real
zero savings.

## Errors

A non-2xx response throws `CoolhandHttpException` with the status on `StatusCode`:

| Status | When |
|---|---|
| `401` | No API key, an invalid key, or the public key |
| `503` | The comparison could not be computed right now. Retryable |

**A `503` is not zero savings.** Do not map it to zeros; show it as an error and retry. A network
failure or a body that is not a JSON object throws a plain `CoolhandException` without a status.

## Combining it with workload metrics

`savings` is **not windowed**, but a workload's `metrics` from `SearchWorkloadsAsync` are, so a
workload's `savings` can exceed its windowed `metrics.total_cost`; do not read the two as a ratio
over the same period. Join them on `id`:

```csharp
// Archived and system workloads can still carry realized savings, so include them or their rows go unmatched.
var workloads = await coolhand.SearchWorkloadsAsync(new SearchWorkloadsParams
{
    Metrics = new MetricsWindow { IncludeMetrics = true, DaysBack = 30 },
    IncludeArchived = true, IncludeSystem = true, Per = 100,
});
var savings = await coolhand.GetRealizedSavingsAsync();

var savingsById = savings["workloads"]!.AsArray().ToDictionary(row => row!["id"]!.GetValue<string>());
foreach (var workload in workloads.Items)
{
    var id = workload["id"]!.GetValue<string>();
    var spend = workload["metrics"]?["total_cost"];                            // windowed
    var saved = savingsById.TryGetValue(id, out var row) ? row!["savings"] : null; // not windowed
}
```

This reads one page of up to 100 workloads; `SearchWorkloadsAsync` is paged (`Page`, `Per`), so
repeat the call while `workloads.Pagination` says there are more. The `get_client_roi_summary` MCP
tool performs the same join over the two endpoints.
