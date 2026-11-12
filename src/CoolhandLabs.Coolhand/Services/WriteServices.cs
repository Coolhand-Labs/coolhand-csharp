using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CoolhandLabs.Internal;
using CoolhandLabs.Models;

namespace CoolhandLabs.Services;

/// <summary>
/// Client files (slide decks, reports, documents) at <c>/api/v2/client_files</c>. Uploading needs the
/// private key; listing, downloading and status changes need a client admin's per-user key.
/// </summary>
public sealed class ClientFileService : BaseService
{
    private static readonly Regex ExtendedFilename =
        new(@"(?:^|[;\s])filename\*\s*=\s*[^']*'[^']*'([^;]+)", RegexOptions.IgnoreCase);
    private static readonly Regex PlainFilename =
        new(@"(?:^|[;\s])filename\s*=\s*(?:""((?:[^""\\]|\\.)*)""|([^;]+))", RegexOptions.IgnoreCase);
    private static readonly Regex BackslashEscape = new(@"\\(.)", RegexOptions.Singleline);

    /// <summary>Creates the service.</summary>
    public ClientFileService(ServiceConfig config) : base(config, "/api/v2/client_files") { }

    private static bool HasBracketOrControlChar(string key) => key.Any(c => c is '[' or ']' || c < 0x20 || c == 0x7f);

    internal static string? FilenameFromContentDisposition(string? header)
    {
        if (string.IsNullOrEmpty(header)) return null;
        var extended = ExtendedFilename.Match(header);
        if (extended.Success)
        {
            try
            {
                return JsText.Decode(JsText.Trim(extended.Groups[1].Value), false);
            }
            catch (FormatException)
            {
                // fall back to the plain parameter
            }
        }
        var plain = PlainFilename.Match(header);
        if (!plain.Success) return null;
        var value = plain.Groups[1].Success ? BackslashEscape.Replace(plain.Groups[1].Value, "$1") : JsText.Trim(plain.Groups[2].Value);
        return value.Length == 0 ? null : value;
    }

    private sealed class Multipart
    {
        public string Boundary { get; } = "----coolhand-csharp-" + Guid.NewGuid().ToString("N");
        private readonly MemoryStream _out = new();

        public void Field(string name, string value)
        {
            Write("--" + Boundary + "\r\nContent-Disposition: form-data; name=\"" + Escape(name) + "\"\r\n\r\n");
            Write(value);
            Write("\r\n");
        }

        public void File(string name, string filename, byte[] content)
        {
            Write("--" + Boundary + "\r\nContent-Disposition: form-data; name=\"" + Escape(name) + "\"; filename=\""
                  + Escape(filename) + "\"\r\nContent-Type: application/octet-stream\r\n\r\n");
            _out.Write(content, 0, content.Length);
            Write("\r\n");
        }

        public byte[] Finish()
        {
            Write("--" + Boundary + "--\r\n");
            return _out.ToArray();
        }

        private static string Escape(string s) => s.Replace("\r", "%0D").Replace("\n", "%0A").Replace("\"", "%22");

        private void Write(string s)
        {
            var bytes = Encoding.UTF8.GetBytes(s);
            _out.Write(bytes, 0, bytes.Length);
        }
    }

    /// <summary>
    /// Uploads a file (always as <c>draft</c>; private API key). Never throws for API or network
    /// failures: resolves to null instead, and on a dry run.
    /// </summary>
    /// <exception cref="ArgumentException">For a metadata key that is empty or contains brackets or control characters.</exception>
    public Task<ClientFile?> CreateClientFileAsync(ClientFileUpload payload, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var form = new Multipart();
        form.Field("client_file[name]", payload.Name);
        if (!string.IsNullOrEmpty(payload.FileType)) form.Field("client_file[file_type]", payload.FileType);
        if (!string.IsNullOrEmpty(payload.Description)) form.Field("client_file[description]", payload.Description);
        form.File("client_file[file]", payload.Filename, payload.File);
        if (payload.Metadata is not null)
        {
            foreach (var pair in payload.Metadata)
            {
                if (string.IsNullOrEmpty(pair.Key) || HasBracketOrControlChar(pair.Key))
                {
                    throw new ArgumentException("Invalid client file metadata key " + Json.Stringify(JsonValue.Create(pair.Key))
                                                + ": keys must be non-empty and must not contain \"[\", \"]\" or control characters");
                }
                form.Field("client_file[metadata][" + pair.Key + "]", pair.Value as string ?? Json.Stringify(Json.From(pair.Value)));
            }
        }
        return UploadAsync(form.Finish(), form.Boundary, payload.Name, ct);
    }

    private async Task<ClientFile?> UploadAsync(byte[] body, string boundary, string name, CancellationToken ct)
    {
        var result = await SendMultipartAsync(body, boundary, "✅ Successfully uploaded client file: " + name, ct).ConfigureAwait(false);
        LogSeparator();
        return result is JsonObject obj ? ClientFile.FromJson(obj) : null;
    }

    /// <summary>Lists client files (a client admin's per-user key).</summary>
    public async Task<Page<ClientFile>> ListClientFilesAsync(ListClientFilesParams? parameters = null, CancellationToken ct = default)
    {
        var p = parameters ?? new ListClientFilesParams();
        var q = new Query().Set("status", p.Status).Set("file_type", p.FileType).Set("page", p.Page).Set("per", p.Per);
        var page = await ListPageAsync(q.AppendTo(ApiEndpoint), "Client file", p.Page, p.Per, ct).ConfigureAwait(false);
        return new Page<ClientFile>(page.Items.Select(ClientFile.FromJson).ToList(), page.Pagination);
    }

    /// <summary>The file's raw bytes, never JSON-decoded (a client admin's per-user key).</summary>
    public async Task<DownloadedClientFile> DownloadClientFileAsync(string id, CancellationToken ct = default)
    {
        var url = BuildResourceUrl(id, "downloadClientFile: id must be a non-empty string") + "/content";
        var r = await FetchBytesAsync(NewRequest(HttpMethod.Get, url), "Client file request failed", ct).ConfigureAwait(false);
        return new DownloadedClientFile(r.Bytes, r.Headers.Get("content-type"), FilenameFromContentDisposition(r.Headers.Get("content-disposition")));
    }

    /// <summary>Sets <c>draft</c>, <c>published</c> or <c>archived</c> (a client admin's per-user key); null on a dry run.</summary>
    public async Task<ClientFile?> UpdateClientFileStatusAsync(string id, string status, CancellationToken ct = default)
    {
        var url = BuildResourceUrl(id, "updateClientFileStatus: id must be a non-empty string");
        if (DryRun)
        {
            Log.Info("🚫 DRY RUN: Skipping updateClientFileStatus for client file " + id);
            return null;
        }
        var request = NewRequest(HttpMethod.Patch, url);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        request.Content = JsonBody(new JsonObject { ["client_file"] = new JsonObject { ["status"] = status } });
        var text = await FetchOrThrowAsync(request, "Client file request failed", ct).ConfigureAwait(false);
        return ClientFile.FromJson(ParseJsonObject(text, "Client file"));
    }
}

/// <summary>Links feedback to optimizations as evidence (<c>/api/v2/optimizations/:id/feedback_links</c>).</summary>
public sealed class OptimizationFeedbackLinkService : BaseService
{
    internal const int BulkLinkBatchSize = 100;

    /// <summary>Creates the service.</summary>
    public OptimizationFeedbackLinkService(ServiceConfig config) : base(config, "/api/v2/optimizations") { }

    private static void AssertId(string? id, string message)
    {
        if (id is null || JsText.Trim(id).Length == 0 || id is "." or "..") throw new ArgumentException(message);
    }

    private string LinksUrl(string optimizationId, string method)
    {
        AssertId(optimizationId, method + ": optimizationId must be a non-empty string");
        return ApiEndpoint + "/" + JsText.EncodeUriComponent(optimizationId) + "/feedback_links";
    }

    private bool SkipForDryRun(string method, string optimizationId)
    {
        if (!DryRun) return false;
        Log.Info("🚫 DRY RUN: Skipping " + method + " for optimization " + optimizationId);
        return true;
    }

    private async Task<JsonNode> PostLinksAsync(string url, JsonObject payload, CancellationToken ct)
    {
        var request = NewRequest(HttpMethod.Post, url);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        request.Content = JsonBody(payload);
        var text = await FetchOrThrowAsync(request, "Feedback link request failed", ct).ConfigureAwait(false);
        return ParseJsonObject(text, "Feedback link");
    }

    /// <summary>Links one feedback to an optimization (private API key), with no note. Null on a dry run.</summary>
    public Task<OptimizationFeedbackLink?> LinkFeedbackAsync(string optimizationId, string feedbackId, CancellationToken ct = default) =>
        LinkAsync(optimizationId, feedbackId, false, null, ct);

    /// <summary>Links one feedback with a note (a null note is sent as JSON null). Null on a dry run.</summary>
    public Task<OptimizationFeedbackLink?> LinkFeedbackAsync(string optimizationId, string feedbackId, string? note, CancellationToken ct = default) =>
        LinkAsync(optimizationId, feedbackId, true, note, ct);

    private Task<OptimizationFeedbackLink?> LinkAsync(string optimizationId, string feedbackId, bool hasNote, string? note, CancellationToken ct)
    {
        AssertId(feedbackId, "linkFeedback: feedbackId must be a non-empty string");
        var url = LinksUrl(optimizationId, "linkFeedback");
        if (SkipForDryRun("linkFeedback", optimizationId)) return Task.FromResult<OptimizationFeedbackLink?>(null);
        var payload = new JsonObject { ["feedback_id"] = feedbackId };
        if (hasNote) payload["note"] = note;
        return PostLinkAsync(url, payload, ct);
    }

    private async Task<OptimizationFeedbackLink?> PostLinkAsync(string url, JsonObject payload, CancellationToken ct) =>
        OptimizationFeedbackLink.FromJson(await PostLinksAsync(url, payload, ct).ConfigureAwait(false));

    /// <summary>Links many feedbacks in batches of 100, merging the counts. Null on a dry run.</summary>
    public Task<BulkLinkFeedbackResult?> BulkLinkFeedbackAsync(string optimizationId, IReadOnlyList<string> feedbackIds, CancellationToken ct = default) =>
        BulkAsync(optimizationId, feedbackIds, false, null, ct);

    /// <summary>Links many feedbacks with a note, in batches of 100. Null on a dry run.</summary>
    public Task<BulkLinkFeedbackResult?> BulkLinkFeedbackAsync(string optimizationId, IReadOnlyList<string> feedbackIds, string? note,
        CancellationToken ct = default) =>
        BulkAsync(optimizationId, feedbackIds, true, note, ct);

    private Task<BulkLinkFeedbackResult?> BulkAsync(string optimizationId, IReadOnlyList<string> feedbackIds, bool hasNote, string? note,
        CancellationToken ct)
    {
        if (feedbackIds is null || feedbackIds.Count == 0) throw new ArgumentException("bulkLinkFeedback: feedbackIds must be a non-empty array");
        foreach (var id in feedbackIds) AssertId(id, "bulkLinkFeedback: every feedback id must be a non-empty string");
        var url = LinksUrl(optimizationId, "bulkLinkFeedback");
        if (SkipForDryRun("bulkLinkFeedback", optimizationId)) return Task.FromResult<BulkLinkFeedbackResult?>(null);
        return RunBatchesAsync(url, feedbackIds, hasNote, note, ct);
    }

    private async Task<BulkLinkFeedbackResult?> RunBatchesAsync(string url, IReadOnlyList<string> feedbackIds, bool hasNote, string? note,
        CancellationToken ct)
    {
        long linked = 0, already = 0, errored = 0;
        var notFound = new List<string>();
        for (var i = 0; i < feedbackIds.Count; i += BulkLinkBatchSize)
        {
            var batch = new JsonArray();
            foreach (var id in feedbackIds.Skip(i).Take(BulkLinkBatchSize)) batch.Add(id);
            var payload = new JsonObject { ["feedback_ids"] = batch };
            if (hasNote) payload["note"] = note;
            var result = await PostLinksAsync(url, payload, ct).ConfigureAwait(false);
            if (result is not JsonObject obj || !Json.IsNumber(obj["linked"]) || !Json.IsNumber(obj["already_linked"])
                || !Json.IsNumber(obj["errored"]) || obj["not_found"] is not JsonArray missing)
            {
                throw new CoolhandException("Feedback link response was not a valid bulk result: " + Excerpt(Json.Stringify(result)));
            }
            linked += obj["linked"]!.GetValue<long>();
            already += obj["already_linked"]!.GetValue<long>();
            errored += obj["errored"]!.GetValue<long>();
            foreach (var n in missing) notFound.Add(n is JsonValue v ? Json.ScalarText(v) ?? "" : Json.Stringify(n));
        }
        return new BulkLinkFeedbackResult(linked, already, errored, notFound);
    }

    /// <summary>Removes a link by the link's hashid (not the feedback's). No-op on a dry run.</summary>
    public Task UnlinkFeedbackAsync(string optimizationId, string linkId, CancellationToken ct = default)
    {
        AssertId(linkId, "unlinkFeedback: linkId must be a non-empty string");
        var url = LinksUrl(optimizationId, "unlinkFeedback") + "/" + JsText.EncodeUriComponent(linkId);
        if (SkipForDryRun("unlinkFeedback", optimizationId)) return Task.CompletedTask;
        var request = NewRequest(HttpMethod.Delete, url);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        return FetchOrThrowAsync(request, "Feedback link request failed", ct);
    }
}

/// <summary>Calls a tool on Coolhand's MCP endpoint (<c>POST /mcp</c>, JSON-RPC 2.0 <c>tools/call</c>).</summary>
public sealed class McpService : BaseService
{
    /// <summary>Creates the service.</summary>
    public McpService(ServiceConfig config) : base(config, "/mcp") { }

    /// <summary>Calls <paramref name="toolName"/> and returns the JSON-RPC <c>result</c>; null on a dry run.</summary>
    /// <exception cref="CoolhandException">On a JSON-RPC error, a tool-level <c>result.error</c>, a network failure or a non-JSON body.</exception>
    /// <exception cref="CoolhandHttpException">On a non-2xx response.</exception>
    public async Task<JsonNode?> McpCallAsync(string toolName, IReadOnlyDictionary<string, object?>? arguments = null, CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = toolName,
                ["arguments"] = Json.From(arguments ?? new Dictionary<string, object?>()),
            },
        };
        if (DryRun)
        {
            if (!Silent)
            {
                Log.Info("🚫 DRY RUN: Skipping MCP call to " + ApiEndpoint);
                Log.Info("🚫 DRY RUN: Would send: " + Json.Pretty(body));
            }
            return null;
        }
        var request = NewRequest(HttpMethod.Post, ApiEndpoint);
        request.Content = JsonBody(body);
        var text = await FetchOrThrowAsync(request, "MCP request failed", ct).ConfigureAwait(false);
        if (!Json.TryParse(text, out var json)) throw new CoolhandException("MCP response was not valid JSON: " + Excerpt(text));
        if (json is not JsonObject obj) throw new CoolhandException("MCP response was not a JSON object: " + Excerpt(text));
        var error = obj["error"];
        if (error is not null && !(error is JsonValue ev && ev.TryGetValue<bool>(out var b) && !b))
        {
            var message = error is JsonObject eo ? eo["message"] : null;
            throw new CoolhandException("MCP error: " + Mask(message is JsonValue mv ? Json.ScalarText(mv) ?? "null" : Json.Stringify(error)));
        }
        var result = obj["result"];
        if (result is JsonObject r && Json.AsString(r["error"]) is { Length: > 0 } toolError) throw new CoolhandException(Mask(toolError));
        return result?.DeepClone();
    }
}
