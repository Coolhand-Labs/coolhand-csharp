# Linking Feedback to an Optimization

Attach feedback to an optimization as evidence (`/api/v2/optimizations/:id/feedback_links`).
Private API key; failures throw `CoolhandHttpException`.

```csharp
OptimizationFeedbackLink? link = await coolhand.LinkFeedbackAsync("opt_1", "fb_1", note: "shows the tone issue");
BulkLinkFeedbackResult? bulk = await coolhand.BulkLinkFeedbackAsync("opt_1", feedbackIds);
await coolhand.UnlinkFeedbackAsync("opt_1", link!.Id!);   // the link's id, not the feedback's
```

The overloads without a note send no `note`; passing `null` sends JSON `null`. `BulkLinkFeedbackAsync`
posts batches of 100 one after another and merges `Linked`, `AlreadyLinked`, `Errored` and `NotFound`.
All three return `null` (or do nothing) in dry-run mode, and validate ids (non-blank, not `.` or `..`)
before any request.
