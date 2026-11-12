# Feedback

```csharp
FeedbackResponse? created = await coolhand.CreateFeedbackAsync(new LlmRequestLogFeedback
{
    LlmProviderUniqueId = "req_011CABC",
    Sentiment = Sentiment.Like,
    CreatorType = CreatorType.Human,
});
```

| Property | Wire field | Notes |
|----------|------------|-------|
| `LlmRequestLogId` | `llm_request_log_id` | Exact match: the log's hashid |
| `LlmRequestLogNumericId` | `llm_request_log_id` | Legacy integer id, used when `LlmRequestLogId` is null |
| `LlmProviderUniqueId` | `llm_provider_unique_id` | Exact match: the provider's request id (Anthropic `request-id`, OpenAI `x-request-id`) |
| `OriginalOutput` | `original_output` | Fuzzy match on the output text |
| `ClientUniqueId` | `client_unique_id` | Your own identifier |
| `RevisedOutput` | `revised_output` | The corrected output: the strongest signal |
| `Explanation` | `explanation` | Why it was good or bad |
| `Sentiment` | `sentiment` | `like`, `dislike` or `neutral` |
| `Like` (obsolete) | `sentiment` | `true` becomes `like`, `false` `dislike`, unless `Sentiment` is set |
| `CreatorType` | `creator_type` | `human`, `agent` or `unknown` |
| `CreatorUniqueId` | `creator_unique_id` | Who gave the feedback |
| `WorkloadHashid` | `workload_hashid` | The workload it belongs to |

The payload is `{"llm_request_log_feedback": {..., "collector": "coolhand-csharp-<version>-manual"}}`.
`CreateFeedbackAsync` never throws for API or network failures; it returns `null`, and in dry-run
mode. `FeedbackResponse.LlmRequestLogId` is set when the feedback matched a log.

## Reading feedback (private key)

`SearchFeedbackAsync(new SearchFeedbackParams { Predicates = ..., Page, Per })` sends each predicate as
`q[<name>]` (raw Ransack: `sentiment_eq` takes `0` dislike, `1` neutral, `2` like; `explanation_cont`,
`created_at_gteq`, `s` for sort). It returns the `:summary` view of each record and the body's
pagination. `GetFeedbackAsync(id)` returns one record with `original_output`, `revised_output` and
`feedback_partials`. Both throw `CoolhandHttpException` (401 with the public key).
