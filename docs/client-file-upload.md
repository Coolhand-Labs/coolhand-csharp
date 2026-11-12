# Client Files

Slide decks, reports and documents at `/api/v2/client_files`.

```csharp
ClientFile? file = await coolhand.UploadClientFileAsync(new ClientFileUpload("Q3 review", bytes, "q3.pptx")
{
    FileType = "slide_deck",
    Description = "Quarterly review",
    Metadata = new Dictionary<string, object?> { ["quarter"] = "Q3", ["pages"] = 12 },
});
```

Uploads are multipart, always created as `draft`, need the **private** key, and use a 10-minute
timeout. Like other submissions they never throw for API or network failures (`null` instead), but a
metadata key that is empty or contains `[`, `]` or control characters throws `ArgumentException`.
Non-string metadata values are sent as JSON.

A client admin's **per-user** key is needed for the rest, which throw `CoolhandHttpException`:

- `ListClientFilesAsync(new ListClientFilesParams { Status, FileType, Page, Per })`
- `DownloadClientFileAsync(id)`: raw bytes, `ContentType`, and `Filename` from `Content-Disposition`
  (RFC 5987 `filename*` preferred)
- `UpdateClientFileStatusAsync(id, "published")`: `draft`, `published` or `archived`; `null` in dry-run mode
