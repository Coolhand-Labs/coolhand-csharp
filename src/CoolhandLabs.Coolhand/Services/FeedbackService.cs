using System.Text;
using System.Text.Json.Nodes;
using CoolhandLabs.Internal;
using CoolhandLabs.Models;

namespace CoolhandLabs.Services;

/// <summary>Creates feedback (<c>POST /api/v2/llm_request_log_feedbacks</c>) and reads it back.</summary>
public sealed class FeedbackService : BaseService
{
    /// <summary>Creates the service.</summary>
    public FeedbackService(ServiceConfig config) : base(config, "/api/v2/llm_request_log_feedbacks") { }

    /// <summary>
    /// The wire fields of <paramref name="feedback"/>, in a fixed order, with the deprecated <c>like</c>
    /// turned into <c>sentiment</c> when no sentiment is given, and the collector string appended.
    /// </summary>
    internal static JsonObject ToWire(LlmRequestLogFeedback feedback, CollectionMethod method)
    {
#pragma warning disable CS0618 // Like is deprecated but still accepted
        var like = feedback.Like;
#pragma warning restore CS0618
        var sentiment = feedback.Sentiment;
        if (sentiment is null && like is not null)
        {
            sentiment = like.Value ? Sentiment.Like : Sentiment.Dislike;
            like = null;
        }
        var output = new JsonObject();
        void Put(string key, JsonNode? value)
        {
            if (value is not null) output[key] = value;
        }
        Put("llm_request_log_id", feedback.LlmRequestLogId is not null ? JsonValue.Create(feedback.LlmRequestLogId)
            : feedback.LlmRequestLogNumericId is not null ? JsonValue.Create(feedback.LlmRequestLogNumericId.Value) : null);
        Put("like", like is null ? null : JsonValue.Create(like.Value));
        Put("sentiment", sentiment is null ? null : JsonValue.Create(sentiment.Value.Wire()));
        Put("creator_type", feedback.CreatorType is null ? null : JsonValue.Create(feedback.CreatorType.Value.Wire()));
        Put("creator_unique_id", Str(feedback.CreatorUniqueId));
        Put("workload_hashid", Str(feedback.WorkloadHashid));
        Put("explanation", Str(feedback.Explanation));
        Put("revised_output", Str(feedback.RevisedOutput));
        Put("llm_provider_unique_id", Str(feedback.LlmProviderUniqueId));
        Put("original_output", Str(feedback.OriginalOutput));
        Put("client_unique_id", Str(feedback.ClientUniqueId));
        return WithCollector(output, method);
    }

    private static JsonNode? Str(string? value) => value is null ? null : JsonValue.Create(value);

    /// <summary>
    /// Creates feedback. The payload is <c>{"llm_request_log_feedback": {...fields, "collector": ...}}</c>.
    /// Never throws for API or network failures: resolves to null instead (and on a dry run).
    /// </summary>
    public async Task<FeedbackResponse?> CreateFeedbackAsync(LlmRequestLogFeedback feedback, CollectionMethod collectionMethod = CollectionMethod.Manual,
        CancellationToken ct = default)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(feedback);
            var fields = ToWire(feedback, collectionMethod);
            LogFeedbackInfo(fields);
            var logId = Json.Text(fields, "llm_request_log_id");
            var result = await SendRequestAsync(new JsonObject { ["llm_request_log_feedback"] = fields },
                "✅ Successfully created feedback with ID: " + (string.IsNullOrEmpty(logId) ? "N/A" : logId), ct).ConfigureAwait(false);
            LogSeparator();
            return result is JsonObject obj ? FeedbackResponse.FromJson(obj) : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            Internal.Log.Error("❌ Request error: " + DescribeError(e));
            return null;
        }
    }

    private void LogFeedbackInfo(JsonObject fields)
    {
        if (Silent) return;
        var output = new StringBuilder("\n📝 CREATING FEEDBACK for LLM Request Log ID: ")
            .Append(Json.Text(fields, "llm_request_log_id"))
            .Append("\n🎭 Sentiment: ").Append(Json.Text(fields, "sentiment") ?? "N/A");
        var explanation = Json.Text(fields, "explanation");
        if (!string.IsNullOrEmpty(explanation))
        {
            output.Append("\n💭 Explanation: ").Append(explanation.Length > 100 ? explanation.Substring(0, 100) + "..." : explanation);
        }
        output.Append(DryRun ? "\n🚫 DRY RUN: API call will be skipped" : "\n📤 Sending to: " + ApiEndpoint);
        Log.Info(output.ToString());
    }

    /// <summary>
    /// Searches feedback with raw Ransack predicates (private API key). Returns the <c>:summary</c> view
    /// of each record plus the body's pagination envelope.
    /// </summary>
    public async Task<Page<JsonNode>> SearchFeedbackAsync(SearchFeedbackParams? parameters = null, CancellationToken ct = default)
    {
        var p = parameters ?? new SearchFeedbackParams();
        var q = new Query();
        foreach (var pair in p.Predicates) q.Set("q[" + pair.Key + "]", pair.Value);
        q.Set("page", p.Page);
        q.Set("per", p.Per);
        var body = await GetJsonAsync(q.AppendTo(ApiEndpoint), "Feedback", ct).ConfigureAwait(false);
        var feedback = body is JsonObject obj ? obj["feedback"] : null;
        var items = feedback is null ? Array.Empty<JsonNode>() : Items(feedback, "Feedback");
        return new Page<JsonNode>(items, Pagination.FromJson(body is JsonObject o ? o["pagination"] : null));
    }

    /// <summary>One feedback record with <c>original_output</c>, <c>revised_output</c> and <c>feedback_partials</c> (private API key).</summary>
    public Task<JsonNode> GetFeedbackAsync(string id, CancellationToken ct = default) =>
        GetJsonAsync(BuildResourceUrl(id, "getFeedback: id must be a non-empty string"), "Feedback", ct);
}
