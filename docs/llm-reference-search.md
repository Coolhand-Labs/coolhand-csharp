# Reading Referenced Files

Which files your logged requests reference, over the last 90 days. Private API key.

`SearchReferencedFilesAsync(new SearchReferencedFilesParams { FilePathContains, CreatedAtGteq, CreatedAtLteq, Page, Per })`
aggregates to one row per distinct `file_path`, ranked by `reference_count` (sent as
`q[file_path_cont]`, `q[created_at_gteq]`, `q[created_at_lteq]`).

`ListReferencedFileSessionsAsync(new ListReferencedFileSessionsParams(filePath) { Page, Per })` is the
drill-down: raw rows for one exact path, newest first, from `GET /api/v2/llm_references/sessions?file_path=...`
(file paths aren't URL-safe as a path segment). Both can 504 under load; retry.
