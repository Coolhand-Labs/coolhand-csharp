using System.Text.Json.Nodes;
using CoolhandLabs.Internal;

namespace CoolhandLabs.Models;

/// <summary>One page of a list result.</summary>
/// <typeparam name="T">The item type.</typeparam>
/// <param name="Items">The items on this page.</param>
/// <param name="Pagination">Where this page sits.</param>
public sealed record Page<T>(IReadOnlyList<T> Items, Pagination Pagination);

/// <summary>Window and opt-in for the <c>metrics</c> rollup on workloads and templates.</summary>
public sealed record MetricsWindow
{
    /// <summary>Add a <c>metrics</c> object per row (off by default on list endpoints, on by default for <c>GetTemplateAsync</c>).</summary>
    public bool? IncludeMetrics { get; init; }

    /// <summary>Rolling lookback in days ending now (server default 28, max 365). Ignored when <see cref="Since"/> is given.</summary>
    public int? DaysBack { get; init; }

    /// <summary>Explicit window start (sent as ISO-8601 UTC with milliseconds).</summary>
    public DateTimeOffset? Since { get; init; }

    /// <summary>Explicit window end.</summary>
    public DateTimeOffset? Until { get; init; }
}

/// <summary>Search parameters for feedback: raw Ransack predicates (e.g. <c>sentiment_eq</c>, <c>explanation_cont</c>, <c>s</c>).</summary>
public sealed record SearchFeedbackParams
{
    /// <summary>Predicates sent as <c>q[&lt;name&gt;]</c>, e.g. <c>["sentiment_eq"] = 0</c> (0 = dislike, 1 = neutral, 2 = like).</summary>
    public IReadOnlyDictionary<string, object?> Predicates { get; init; } = new Dictionary<string, object?>();

    /// <summary>Page number (1-based).</summary>
    public int? Page { get; init; }

    /// <summary>Page size.</summary>
    public int? Per { get; init; }
}

/// <summary>Filters for <c>SearchLogsAsync</c> (named filters, not raw Ransack predicates).</summary>
public sealed record SearchLogsParams
{
    /// <summary>Template hashid.</summary>
    public string? TemplateId { get; init; }
    /// <summary>Workload hashid.</summary>
    public string? WorkloadId { get; init; }
    /// <summary>Substring of the system prompt.</summary>
    public string? SystemPromptContains { get; init; }
    /// <summary>Substring of the user prompt.</summary>
    public string? UserPromptContains { get; init; }
    /// <summary>Case-insensitive substring of a failed request's error text; pass a failure summary's <c>error_pattern</c>.</summary>
    public string? ErrorContains { get; init; }
    /// <summary>Model name.</summary>
    public string? Model { get; init; }
    /// <summary>Source API, e.g. <c>anthropic</c>.</summary>
    public string? SourceApi { get; init; }
    /// <summary>Source API result, e.g. <c>failed</c>.</summary>
    public string? SourceApiResult { get; init; }
    /// <summary>Source application.</summary>
    public string? SourceApplication { get; init; }
    /// <summary>Project path (metadata convention).</summary>
    public string? ProjectPath { get; init; }
    /// <summary>Only logs not matched to a template.</summary>
    public bool? UnmatchedOnly { get; init; }
    /// <summary>Rolling lookback in days.</summary>
    public int? DaysBack { get; init; }
    /// <summary>Window start (replaces <see cref="DaysBack"/>).</summary>
    public DateTimeOffset? Since { get; init; }
    /// <summary>Window end.</summary>
    public DateTimeOffset? Until { get; init; }
    /// <summary>Minimum cost in USD (priceable logs only).</summary>
    public decimal? MinCost { get; init; }
    /// <summary>Server-side ordering, e.g. <c>cost_desc</c>.</summary>
    public string? Order { get; init; }
    /// <summary>Include prompt text in each row.</summary>
    public bool? IncludePrompts { get; init; }
    /// <summary>Ransack sort, sent as <c>q[s]</c>, e.g. <c>created_at desc</c>.</summary>
    public string? Sort { get; init; }
    /// <summary>Page number.</summary>
    public int? Page { get; init; }
    /// <summary>Page size.</summary>
    public int? Per { get; init; }
    /// <summary>Ask for exact totals (a COUNT on the server).</summary>
    public bool? IncludeTotal { get; init; }
}

/// <summary>Which slice of each content field <c>GetLogContentAsync</c> returns (with <see cref="GetLogContentOptions.MaxChars"/>).</summary>
public enum LogContentSection
{
    /// <summary><c>full</c></summary>
    Full,
    /// <summary><c>beginning</c></summary>
    Beginning,
    /// <summary><c>end</c></summary>
    End,
}

/// <summary>Options for <c>GetLogContentAsync</c> (large-log handling).</summary>
public sealed record GetLogContentOptions
{
    /// <summary>Which slice to return; only meaningful with <see cref="MaxChars"/>.</summary>
    public LogContentSection? Section { get; init; }
    /// <summary>Max characters per content field.</summary>
    public int? MaxChars { get; init; }
    /// <summary>Return matching snippets instead of content (must not be blank; not combinable with <see cref="Section"/>/<see cref="MaxChars"/>).</summary>
    public string? SearchQuery { get; init; }
    /// <summary>Include thinking blocks.</summary>
    public bool? IncludeThinking { get; init; }
    /// <summary>Include the per-turn breakdown for captured sessions as <c>turn_metrics</c>.</summary>
    public bool? IncludeTurnMetrics { get; init; }
}

/// <summary>Parameters for <c>FailureSummaryAsync</c>.</summary>
public sealed record FailureSummaryParams
{
    /// <summary>Lookback in days (server default 7, max 36500). Zero or negative is a 422 from the server.</summary>
    public int? DaysBack { get; init; }
}

/// <summary>Filters for <c>SearchTemplatesAsync</c>.</summary>
public sealed record SearchTemplatesParams
{
    /// <summary>Name search (a parameter on the list endpoint).</summary>
    public string? Search { get; init; }
    /// <summary>Workload hashid.</summary>
    public string? WorkloadId { get; init; }
    /// <summary>Status, e.g. <c>published</c>.</summary>
    public string? Status { get; init; }
    /// <summary>Include deprecated templates.</summary>
    public bool? IncludeDeprecated { get; init; }
    /// <summary>Include the <c>Unmatched</c> / <c>Ignored API Calls</c> system buckets.</summary>
    public bool? IncludeSystem { get; init; }
    /// <summary>Metrics opt-in and window.</summary>
    public MetricsWindow? Metrics { get; init; }
    /// <summary>Page number.</summary>
    public int? Page { get; init; }
    /// <summary>Page size.</summary>
    public int? Per { get; init; }
}

/// <summary>Filters for <c>ListStepTemplatesAsync</c>.</summary>
public sealed record ListStepTemplatesParams
{
    /// <summary>Name search.</summary>
    public string? Search { get; init; }
    /// <summary>Step type, e.g. <c>prompt_step</c> or <c>tool_step</c>.</summary>
    public string? StepType { get; init; }
    /// <summary>Add run, turn, tool and efficiency metrics (the server caps <c>per</c> at 25 then).</summary>
    public bool? IncludeMetrics { get; init; }
    /// <summary>Page number.</summary>
    public int? Page { get; init; }
    /// <summary>Page size.</summary>
    public int? Per { get; init; }
}

/// <summary>Filters for <c>SearchWorkloadsAsync</c>.</summary>
public sealed record SearchWorkloadsParams
{
    /// <summary>Name search.</summary>
    public string? Search { get; init; }
    /// <summary>Include archived workloads.</summary>
    public bool? IncludeArchived { get; init; }
    /// <summary>Include system workloads.</summary>
    public bool? IncludeSystem { get; init; }
    /// <summary>Embed each workload's templates.</summary>
    public bool? IncludeTemplates { get; init; }
    /// <summary>Metrics opt-in and window.</summary>
    public MetricsWindow? Metrics { get; init; }
    /// <summary>Page number.</summary>
    public int? Page { get; init; }
    /// <summary>Page size.</summary>
    public int? Per { get; init; }
}

/// <summary>Filters for <c>SearchReferencedFilesAsync</c> (last 90 days).</summary>
public sealed record SearchReferencedFilesParams
{
    /// <summary>Substring of the file path (<c>q[file_path_cont]</c>).</summary>
    public string? FilePathContains { get; init; }
    /// <summary>Lower bound on creation time (<c>q[created_at_gteq]</c>).</summary>
    public string? CreatedAtGteq { get; init; }
    /// <summary>Upper bound on creation time (<c>q[created_at_lteq]</c>).</summary>
    public string? CreatedAtLteq { get; init; }
    /// <summary>Page number.</summary>
    public int? Page { get; init; }
    /// <summary>Page size.</summary>
    public int? Per { get; init; }
}

/// <summary>Parameters for <c>ListReferencedFileSessionsAsync</c>.</summary>
/// <param name="FilePath">The exact file path (required).</param>
public sealed record ListReferencedFileSessionsParams(string FilePath)
{
    /// <summary>Page number.</summary>
    public int? Page { get; init; }
    /// <summary>Page size.</summary>
    public int? Per { get; init; }
}

/// <summary>Parameters for <c>SearchHelpArticlesAsync</c>.</summary>
public sealed record SearchHelpArticlesParams
{
    /// <summary>Full-text query.</summary>
    public string? Query { get; init; }
    /// <summary>A category slug from <c>ListHelpCategoriesAsync</c> (sent as <c>q[help_category_slug_eq]</c>).</summary>
    public string? Category { get; init; }
    /// <summary>Page number.</summary>
    public int? Page { get; init; }
    /// <summary>Page size (<c>per_page</c>).</summary>
    public int? PerPage { get; init; }
}

/// <summary>Filters for <c>ListClientFilesAsync</c>.</summary>
public sealed record ListClientFilesParams
{
    /// <summary><c>draft</c>, <c>published</c> or <c>archived</c>.</summary>
    public string? Status { get; init; }
    /// <summary>File type.</summary>
    public string? FileType { get; init; }
    /// <summary>Page number.</summary>
    public int? Page { get; init; }
    /// <summary>Page size.</summary>
    public int? Per { get; init; }
}

/// <summary>A file to upload with <c>UploadClientFileAsync</c> (always created as a draft).</summary>
/// <param name="Name">Display name.</param>
/// <param name="File">The file's bytes.</param>
/// <param name="Filename">The file name sent in the multipart part.</param>
public sealed record ClientFileUpload(string Name, byte[] File, string Filename)
{
    /// <summary>File type, e.g. <c>slide_deck</c>.</summary>
    public string? FileType { get; init; }
    /// <summary>Description.</summary>
    public string? Description { get; init; }
    /// <summary>Metadata; non-string values are sent as JSON. Keys must be non-empty and contain no brackets or control characters.</summary>
    public IReadOnlyDictionary<string, object?>? Metadata { get; init; }
}

/// <summary>A client file record.</summary>
/// <param name="Id">Hashid.</param>
/// <param name="Name">Display name.</param>
/// <param name="FileType">File type.</param>
/// <param name="Status">Status.</param>
/// <param name="Description">Description.</param>
/// <param name="Metadata">Metadata object.</param>
/// <param name="CreatedAt">Creation time.</param>
/// <param name="Raw">The full JSON.</param>
public sealed record ClientFile(string? Id, string? Name, string? FileType, string? Status, string? Description,
    JsonNode? Metadata, string? CreatedAt, JsonNode Raw)
{
    /// <summary>Reads a client file from its JSON.</summary>
    public static ClientFile FromJson(JsonNode node) => new(
        Json.Text(node, "id"), Json.Text(node, "name"), Json.Text(node, "file_type"), Json.Text(node, "status"),
        Json.Text(node, "description"), node is JsonObject o ? o["metadata"]?.DeepClone() : null, Json.Text(node, "created_at"), node);
}

/// <summary>A downloaded client file.</summary>
/// <param name="Content">The raw bytes, never JSON-decoded.</param>
/// <param name="ContentType">The <c>Content-Type</c>, if any.</param>
/// <param name="Filename">The filename from <c>Content-Disposition</c>, if any.</param>
public sealed record DownloadedClientFile(byte[] Content, string? ContentType, string? Filename);

/// <summary>A link between feedback and an optimization.</summary>
/// <param name="Id">The link's hashid (used by <c>UnlinkFeedbackAsync</c>).</param>
/// <param name="OptimizationId">The optimization.</param>
/// <param name="FeedbackId">The feedback.</param>
/// <param name="Note">The note, if any.</param>
/// <param name="CreatedAt">Creation time.</param>
public sealed record OptimizationFeedbackLink(string? Id, string? OptimizationId, string? FeedbackId, string? Note, string? CreatedAt)
{
    /// <summary>Reads a link from its JSON.</summary>
    public static OptimizationFeedbackLink FromJson(JsonNode node) => new(
        Json.Text(node, "id"), Json.Text(node, "optimization_id"), Json.Text(node, "feedback_id"), Json.Text(node, "note"),
        Json.Text(node, "created_at"));
}

/// <summary>The merged result of <c>BulkLinkFeedbackAsync</c> across batches.</summary>
/// <param name="Linked">Newly linked.</param>
/// <param name="AlreadyLinked">Already linked before.</param>
/// <param name="Errored">Failed to link.</param>
/// <param name="NotFound">Feedback ids the server did not find.</param>
public sealed record BulkLinkFeedbackResult(long Linked, long AlreadyLinked, long Errored, IReadOnlyList<string> NotFound);

/// <summary>Parameters for <c>UpsertWorkloadAsync</c>: only the fields given are sent.</summary>
/// <param name="Key">The workload key.</param>
public sealed record UpsertWorkloadParams(string Key)
{
    /// <summary>Display name.</summary>
    public string? Name { get; init; }
    /// <summary>Description (at most 1000 characters).</summary>
    public string? Description { get; init; }
    /// <summary>Who is declaring.</summary>
    public string? Reporter { get; init; }
}

/// <summary>Parameters for <c>UpsertTemplateAsync</c>: only the fields given are sent.</summary>
/// <param name="Key">The template key.</param>
public sealed record UpsertTemplateParams(string Key)
{
    /// <summary>The owning workload's hashid (wins over <see cref="WorkloadKey"/>).</summary>
    public string? WorkloadId { get; init; }
    /// <summary>The owning workload's key.</summary>
    public string? WorkloadKey { get; init; }
    /// <summary>Display name.</summary>
    public string? Name { get; init; }
    /// <summary>Who is declaring.</summary>
    public string? Reporter { get; init; }
}

/// <summary>A record upserted by key.</summary>
/// <param name="Record">The workload or template.</param>
/// <param name="Created">True when the server created it (201) rather than updated it (200).</param>
public sealed record UpsertResult(JsonNode Record, bool Created);
