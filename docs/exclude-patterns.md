# Excluding Endpoints

`ExcludeApiPatterns` (or `COOLHAND_EXCLUDE_API_PATTERNS`, comma-separated) lists URL substrings that
are never captured. Entries are **substrings, not globs or regular expressions**: a request is skipped
when its full URL, or the URL's canonical form (host lowercased, one trailing host dot dropped, empty
path as `/`), contains any entry.

```csharp
new CoolhandOptions { ApiKey = key, ExcludeApiPatterns = new[] { "internal.corp", "/v1/embeddings" } };
coolhand.ExcludeApiPatterns = new[] { "/v1/moderations" };   // replace at runtime
```

- `null` keeps `CoolhandOptions.DefaultExcludeApiPatterns` (Vertex batch jobs and Azure OpenAI
  files, batches, fine-tuning and model listing); an empty list excludes nothing.
- Empty entries are ignored, since they would match every URL.
- An entry containing `*` only matches URLs containing `*` literally, and the SDK warns about it.
- Write host entries in lowercase. Prefer specific entries (`api.example.com/v1/x`) over short ones
  (`/v1`) that would exclude more than you meant.
