using System.Text;
using System.Text.Json.Nodes;
using CoolhandLabs.Internal;
using CoolhandLabs.Models;

namespace CoolhandLabs.Services;

/// <summary>Submits captured LLM calls (<c>POST /api/v2/llm_request_logs</c>) and reads logs back.</summary>
public sealed class LoggingService : BaseService
{
    /// <summary>Creates the service.</summary>
    public LoggingService(ServiceConfig config) : base(config, "/api/v2/llm_request_logs") { }

    /// <summary>
    /// Submits one log. The payload is
    /// <c>{"llm_request_log": {"raw_request": ..., "collector": ..., "metadata"?: ..., "declared"?: ...}}</c>.
    /// Never throws for API or network failures: resolves to null on any failure, on a dry run, or when nothing came back.
    /// </summary>
    /// <param name="callData">The call.</param>
    /// <param name="matchedPattern">The provider pattern that matched, for the progress log.</param>
    /// <param name="collectionMethod">How the call was collected.</param>
    /// <param name="collector">Overrides the SDK collector string when non-null.</param>
    /// <param name="metadata">Free-form object, omitted when null (convention: <c>project_path</c>).</param>
    /// <param name="declared">The declared context to send as <c>declared</c>, or null.</param>
    /// <param name="ct">Cancels the submission.</param>
    public async Task<LogResponse?> LogRequestToApiAsync(CallData callData, MatchedPattern? matchedPattern, CollectionMethod collectionMethod,
        string? collector = null, IReadOnlyDictionary<string, object?>? metadata = null, DeclaredContext? declared = null,
        CancellationToken ct = default)
    {
        try
        {
            var logData = new JsonObject
            {
                ["raw_request"] = callData.ToJson(),
                ["collector"] = collector ?? Collector.GetCollectorString(collectionMethod),
            };
            if (metadata is not null) logData["metadata"] = Json.From(metadata);
            if (declared is { IsEmpty: false }) logData["declared"] = declared.ToJson();
            var payload = new JsonObject { ["llm_request_log"] = logData };
            LogRequestInfo(callData, matchedPattern);
            var result = await SendRequestAsync(payload, "✅ Successfully logged to API with ID for call #" + callData.Id, ct).ConfigureAwait(false);
            var response = result is JsonObject obj ? LogResponse.FromJson(obj) : null;
            if (response is not null) LogWarnings(response.Warnings);
            LogSeparator();
            return response;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            Log.WarnUnlessSilent("❌ Failed to log request to API: " + DescribeError(e));
            return null;
        }
    }

    // Server warnings mean the log was saved, but not as declared: surface them, never throw.
    private void LogWarnings(IReadOnlyList<string> warnings)
    {
        if (Silent) return;
        foreach (var warning in warnings) Log.Warn("[coolhand-csharp] ⚠️ Coolhand warning: " + warning);
    }

    private void LogRequestInfo(CallData callData, MatchedPattern? matchedPattern)
    {
        if (Silent) return;
        var apiName = matchedPattern?.Pattern.Name ?? "API";
        var output = new StringBuilder();
        output.Append("\n🎉 LOGGING ").Append(apiName).Append(" API Call #").Append(callData.Id)
            .Append("\n🕐 Time: ").Append(callData.Timestamp)
            .Append("\n🎯 ").Append(callData.Method).Append(' ').Append(callData.Url)
            .Append("\n📊 Status: ").Append(callData.StatusCode)
            .Append("\n🔧 Protocol: ").Append(callData.Protocol);
        if (matchedPattern is not null)
        {
            output.Append("\n🔍 Matched by: ").Append(matchedPattern.MatchType).Append(" (").Append(matchedPattern.MatchValue).Append(')');
        }
        if (callData.RequestBody is JsonObject body)
        {
            if (body["model"] is { } model) output.Append("\n🤖 Model: ").Append(model is JsonValue v ? Json.ScalarText(v) : Json.Stringify(model));
            if (body["messages"] is JsonArray messages) output.Append("\n💬 Messages: ").Append(messages.Count);
            if (body.ContainsKey("temperature")) output.Append("\n🌡️  Temperature: ").Append(Json.Stringify(body["temperature"]));
        }
        output.Append(DryRun ? "\n🚫 DRY RUN: API call will be skipped" : "\n📤 Sending to: " + ApiEndpoint);
        Log.Info(output.ToString());
    }

    /// <summary>
    /// Full input/output content of one log (<c>GET /api/v2/llm_request_logs/:id</c>). Requires the private API key.
    /// </summary>
    /// <exception cref="ArgumentException">If <paramref name="logId"/> is blank or a dot-segment, or the search query is blank or combined with a slice.</exception>
    public Task<JsonNode> GetLogContentAsync(string logId, GetLogContentOptions? options = null, CancellationToken ct = default)
    {
        var url = BuildResourceUrl(logId, "getLogContent: logId must be a non-empty string");
        var o = options ?? new GetLogContentOptions();
        var q = new Query();
        q.Set("section", o.Section?.ToString().ToLowerInvariant());
        q.Set("max_chars", o.MaxChars);
        if (o.SearchQuery is not null)
        {
            if (JsText.IsBlank(o.SearchQuery)) throw new ArgumentException("getLogContent: searchQuery must be a non-empty string");
            if (o.Section is not null || o.MaxChars is not null)
            {
                throw new ArgumentException("getLogContent: searchQuery cannot be combined with section/maxChars");
            }
            q.Set("search_query", o.SearchQuery);
        }
        q.Set("include_thinking", o.IncludeThinking);
        q.Set("include_turn_metrics", o.IncludeTurnMetrics);
        return GetJsonAsync(q.AppendTo(url), "Log", ct);
    }

    /// <summary>
    /// Searches logs by named filters (private API key). Pagination is read from response headers when
    /// present and estimated from the page otherwise (pass <c>IncludeTotal</c> for exact totals).
    /// </summary>
    public Task<Page<JsonNode>> SearchLogsAsync(SearchLogsParams? parameters = null, CancellationToken ct = default)
    {
        var p = parameters ?? new SearchLogsParams();
        var q = new Query()
            .Set("template_id", p.TemplateId)
            .Set("workload_id", p.WorkloadId)
            .Set("system_prompt_contains", p.SystemPromptContains)
            .Set("user_prompt_contains", p.UserPromptContains)
            .Set("error_contains", p.ErrorContains)
            .Set("model", p.Model)
            .Set("source_api", p.SourceApi)
            .Set("source_api_result", p.SourceApiResult)
            .Set("source_application", p.SourceApplication)
            .Set("project_path", p.ProjectPath)
            .Set("unmatched_only", p.UnmatchedOnly)
            .Set("days_back", p.DaysBack)
            .Set("since", ToTimestampParam(p.Since))
            .Set("until", ToTimestampParam(p.Until))
            .Set("min_cost", p.MinCost)
            .Set("order", p.Order)
            .Set("include_prompts", p.IncludePrompts)
            .Set("q[s]", p.Sort)
            .Set("page", p.Page)
            .Set("per", p.Per)
            .Set("include_total", p.IncludeTotal);
        return ListPageAsync(q.AppendTo(ApiEndpoint), "Log", p.Page, p.Per, ct);
    }

    /// <summary>
    /// Rolls this client's recent failed requests up by error pattern and by template
    /// (<c>GET /api/v2/llm_request_logs/failure_summary</c>, private API key). Pass a pattern's
    /// <c>error_pattern</c> as <see cref="SearchLogsParams.ErrorContains"/> to list its logs.
    /// </summary>
    /// <returns>The summary object: <c>error_breakdown</c> (at most 50 patterns), <c>by_template</c>, totals and <c>actionable_summary</c>.</returns>
    public Task<JsonNode> FailureSummaryAsync(FailureSummaryParams? parameters = null, CancellationToken ct = default)
    {
        var q = new Query().Set("days_back", parameters?.DaysBack);
        return GetJsonAsync(q.AppendTo(ApiEndpoint + "/failure_summary"), "Log", ct);
    }
}
