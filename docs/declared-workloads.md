# Declared Workloads and Templates

`Coolhand.WithContext` tells Coolhand which workload and template the LLM calls in a block belong to,
so the server skips log-to-template matching. It is sent as `llm_request_log.declared`.

```csharp
var ctx = new DeclaredContext { Reporter = "qa-agent" }
    .WithWorkload("qa/test-plans", "QA test plans", "Writes test plans from stories")
    .WithTemplate("qa/test-plans/generate", "generate");

Message message = await Coolhand.WithContextAsync(ctx, () => client.Messages.Create(parameters));
string text = Coolhand.WithContext(ctx, () => Summarise(input));
```

- The context lives in an `AsyncLocal<T>`: it flows across `await`s and into tasks started inside the
  block, and concurrent flows never see each other's. It is snapshotted when each request starts, so a
  log submitted later in the background keeps it.
- Nesting: per field the innermost value wins; `Workload` and `Template` are replaced wholesale; a
  context that sets a workload without a template does not inherit the outer template. A blank
  `Reporter` counts as none.
- Validation happens before your code runs (`ArgumentException`): keys at most 255 characters, no
  control or invisible characters, no leading/trailing whitespace, no leading/trailing or doubled `/`;
  descriptions at most 1000 characters; a template key needs a workload in scope (or use
  `DeclaredTemplate.OfId`). `DeclaredWorkload.OfId` names an existing workload by hashid.
- `Name` and `Description` apply only when the server creates the record; ingest never renames.
  `LogResponse.Warnings` reports declarations the server ignored (e.g. the public-key cap).

## Creating and renaming (private key)

```csharp
UpsertResult? w = await coolhand.UpsertWorkloadAsync(new UpsertWorkloadParams("qa/test-plans") { Name = "QA plans" });
UpsertResult? t = await coolhand.UpsertTemplateAsync(new UpsertTemplateParams("qa/test-plans/generate") { WorkloadKey = "qa/test-plans" });
```

`PUT .../by_key/<key>` with each `/`-separated segment percent-encoded. Only the fields given are
sent; a template's workload is flattened to `workload_id` (wins) or `workload_key`. `Created` is true
for a 201. Keys are validated even in dry-run mode, which then returns `null`. A taken name is a 422.
