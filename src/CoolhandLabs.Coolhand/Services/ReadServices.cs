using System.Text.Json.Nodes;
using CoolhandLabs.Internal;
using CoolhandLabs.Models;

namespace CoolhandLabs.Services;

/// <summary>LLM request templates: list/search, get one, and upsert by key (<c>/api/v2/llm_request_templates</c>).</summary>
public sealed class TemplateService : BaseService
{
    /// <summary>Creates the service.</summary>
    public TemplateService(ServiceConfig config) : base(config, "/api/v2/llm_request_templates") { }

    internal static async Task<UpsertResult?> ToResult(Task<Upserted?> upsert)
    {
        var upserted = await upsert.ConfigureAwait(false);
        return upserted is null ? null : new UpsertResult(upserted.Record, upserted.Created);
    }

    internal static Query Metrics(Query q, MetricsWindow? m)
    {
        var w = m ?? new MetricsWindow();
        return q.Set("include_metrics", w.IncludeMetrics)
            .Set("days_back", w.DaysBack)
            .Set("since", ToTimestampParam(w.Since))
            .Set("until", ToTimestampParam(w.Until));
    }

    /// <summary>Lists templates, filtered and paginated (private API key).</summary>
    public Task<Page<JsonNode>> SearchTemplatesAsync(SearchTemplatesParams? parameters = null, CancellationToken ct = default)
    {
        var p = parameters ?? new SearchTemplatesParams();
        var q = new Query()
            .Set("search", p.Search)
            .Set("workload_id", p.WorkloadId)
            .Set("status", p.Status)
            .Set("include_deprecated", p.IncludeDeprecated)
            .Set("include_system", p.IncludeSystem);
        Metrics(q, p.Metrics).Set("page", p.Page).Set("per", p.Per);
        return ListPageAsync(q.AppendTo(ApiEndpoint), "Template", p.Page, p.Per, ct);
    }

    /// <summary>One template by hashid, with its prompt patterns; metrics included unless <c>IncludeMetrics</c> is false.</summary>
    public Task<JsonNode> GetTemplateAsync(string id, MetricsWindow? metrics = null, CancellationToken ct = default)
    {
        var url = BuildResourceUrl(id, "getTemplate: id must be a non-empty string");
        return GetJsonAsync(Metrics(new Query(), metrics).AppendTo(url), "Template", ct);
    }

    /// <summary>Creates or updates a template by key (private API key); null on a dry run.</summary>
    public Task<UpsertResult?> UpsertAsync(UpsertTemplateParams parameters, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var body = new JsonObject();
        if (parameters.Name is not null) body["name"] = parameters.Name;
        if (parameters.Reporter is not null) body["reporter"] = parameters.Reporter;
        if (parameters.WorkloadId is not null || parameters.WorkloadKey is not null)
        {
            if (!JsText.IsBlank(parameters.WorkloadId))
            {
                body["workload_id"] = parameters.WorkloadId;
            }
            else if (!JsText.IsBlank(parameters.WorkloadKey))
            {
                var problem = EntityKeys.KeyProblem(parameters.WorkloadKey!);
                if (problem is not null)
                {
                    throw new ArgumentException("Template upsert: workload key " + problem + " (got: \"" + parameters.WorkloadKey + "\")");
                }
                body["workload_key"] = parameters.WorkloadKey;
            }
            else
            {
                throw new ArgumentException("Template upsert: workload must be { id } or { key } with a non-blank string");
            }
        }
        return ToResult(PutByKeyAsync(parameters.Key, body, "Template", ct));
    }
}

/// <summary>Step templates: recurring steps detected in captured agent sessions (<c>/api/v2/step_templates</c>).</summary>
public sealed class StepTemplateService : BaseService
{
    /// <summary>Creates the service.</summary>
    public StepTemplateService(ServiceConfig config) : base(config, "/api/v2/step_templates") { }

    /// <summary>Lists step templates (private API key).</summary>
    public Task<Page<JsonNode>> ListStepTemplatesAsync(ListStepTemplatesParams? parameters = null, CancellationToken ct = default)
    {
        var p = parameters ?? new ListStepTemplatesParams();
        var q = new Query()
            .Set("search", p.Search)
            .Set("step_type", p.StepType)
            .Set("include_metrics", p.IncludeMetrics)
            .Set("page", p.Page)
            .Set("per", p.Per);
        return ListPageAsync(q.AppendTo(ApiEndpoint), "Step template", p.Page, p.Per, ct);
    }

    /// <summary>One step template by its integer id; metrics included unless <paramref name="includeMetrics"/> is false.</summary>
    /// <exception cref="ArgumentException">If <paramref name="id"/> is not a positive safe integer.</exception>
    public Task<JsonNode> GetStepTemplateAsync(long id, bool? includeMetrics = null, CancellationToken ct = default)
    {
        if (id <= 0 || id > 9_007_199_254_740_991L) throw new ArgumentException("getStepTemplate: id must be a positive integer");
        return GetJsonAsync(new Query().Set("include_metrics", includeMetrics).AppendTo(ApiEndpoint + "/" + id), "Step template", ct);
    }
}

/// <summary>Workloads: list with cost/performance metrics, and upsert by key (<c>/api/v2/workloads</c>).</summary>
public sealed class WorkloadService : BaseService
{
    /// <summary>Creates the service.</summary>
    public WorkloadService(ServiceConfig config) : base(config, "/api/v2/workloads") { }

    /// <summary>Lists workloads (private API key).</summary>
    public Task<Page<JsonNode>> SearchWorkloadsAsync(SearchWorkloadsParams? parameters = null, CancellationToken ct = default)
    {
        var p = parameters ?? new SearchWorkloadsParams();
        var q = new Query()
            .Set("search", p.Search)
            .Set("include_archived", p.IncludeArchived)
            .Set("include_system", p.IncludeSystem)
            .Set("include_templates", p.IncludeTemplates);
        TemplateService.Metrics(q, p.Metrics).Set("page", p.Page).Set("per", p.Per);
        return ListPageAsync(q.AppendTo(ApiEndpoint), "Workload", p.Page, p.Per, ct);
    }

    /// <summary>Creates or updates a workload by key (private API key); null on a dry run.</summary>
    public Task<UpsertResult?> UpsertAsync(UpsertWorkloadParams parameters, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var body = new JsonObject();
        if (parameters.Name is not null) body["name"] = parameters.Name;
        if (parameters.Description is not null)
        {
            var problem = EntityKeys.DescriptionProblem(parameters.Description);
            if (problem is not null) throw new ArgumentException("Workload upsert: description " + problem);
            body["description"] = parameters.Description;
        }
        if (parameters.Reporter is not null) body["reporter"] = parameters.Reporter;
        return TemplateService.ToResult(PutByKeyAsync(parameters.Key, body, "Workload", ct));
    }
}

/// <summary>Files referenced in captured sessions (<c>/api/v2/llm_references</c>), last 90 days.</summary>
public sealed class LlmReferenceService : BaseService
{
    /// <summary>Creates the service.</summary>
    public LlmReferenceService(ServiceConfig config) : base(config, "/api/v2/llm_references") { }

    /// <summary>One row per distinct file path, ranked by <c>reference_count</c> (private API key).</summary>
    public Task<Page<JsonNode>> SearchReferencedFilesAsync(SearchReferencedFilesParams? parameters = null, CancellationToken ct = default)
    {
        var p = parameters ?? new SearchReferencedFilesParams();
        var q = new Query()
            .Set("q[file_path_cont]", p.FilePathContains)
            .Set("q[created_at_gteq]", p.CreatedAtGteq)
            .Set("q[created_at_lteq]", p.CreatedAtLteq)
            .Set("page", p.Page)
            .Set("per", p.Per);
        return ListPageAsync(q.AppendTo(ApiEndpoint), "Referenced file", p.Page, p.Per, ct);
    }

    /// <summary>Raw rows for one exact file path, newest first (private API key).</summary>
    public Task<Page<JsonNode>> ListReferencedFileSessionsAsync(ListReferencedFileSessionsParams parameters, CancellationToken ct = default)
    {
        if (parameters?.FilePath is null) throw new ArgumentException("listReferencedFileSessions: filePath must be a string");
        var q = new Query().Set("file_path", parameters.FilePath).Set("page", parameters.Page).Set("per", parameters.Per);
        return ListPageAsync(q.AppendTo(ApiEndpoint + "/sessions"), "Referenced file session", parameters.Page, parameters.Per, ct);
    }
}

/// <summary>
/// The Coolhand help center (<c>/api/v2/public_content</c>): readable with no API key. A key (the
/// config's, else <c>COOLHAND_API_KEY</c>) lifts the anonymous limit of 30 requests per minute per IP;
/// pass an empty key to stay anonymous.
/// </summary>
public sealed class HelpCenterDocsService : BaseService
{
    /// <summary>Anonymous unless <c>COOLHAND_API_KEY</c> is set.</summary>
    public HelpCenterDocsService() : this(new ServiceConfig()) { }

    /// <summary>Creates the service.</summary>
    public HelpCenterDocsService(ServiceConfig config) : base(WithEnvKey(config), "/api/v2/public_content", allowAnonymous: true) { }

    private static ServiceConfig WithEnvKey(ServiceConfig config) =>
        config with { ApiKey = config.ApiKey ?? Environment.GetEnvironmentVariable("COOLHAND_API_KEY") ?? "" };

    /// <summary>Searches help articles; with a <c>Category</c>, within that section.</summary>
    public Task<Page<JsonNode>> SearchHelpArticlesAsync(SearchHelpArticlesParams? parameters = null, CancellationToken ct = default)
    {
        var p = parameters ?? new SearchHelpArticlesParams();
        var query = p.Query is null || JsText.Trim(p.Query).Length == 0 ? null : JsText.Trim(p.Query);
        var category = p.Category is null ? null : JsText.Trim(p.Category);
        var q = new Query();
        if (!string.IsNullOrEmpty(category))
        {
            q.Set("q[help_category_slug_eq]", category).Set("q[full_text]", query);
        }
        else
        {
            q.Set("q", query);
        }
        q.Set("page", p.Page).Set("per_page", p.PerPage);
        return ListPageAsync(q.AppendTo(ApiEndpoint), "Help center", p.Page, p.PerPage, ct);
    }

    /// <summary>One article by slug, with its markdown <c>body</c> and <c>toc</c>.</summary>
    public Task<JsonNode> GetHelpArticleAsync(string slug, CancellationToken ct = default) =>
        GetJsonAsync(BuildResourceUrl(slug, "getHelpArticle: slug must be a non-empty string"), "Help center", ct);

    /// <summary>The help center sections; a section's <c>slug</c> is the <c>Category</c> filter.</summary>
    public async Task<IReadOnlyList<JsonNode>> ListHelpCategoriesAsync(CancellationToken ct = default) =>
        Items(await GetJsonAsync(ApiEndpoint + "/categories", "Help center", ct).ConfigureAwait(false), "Help center");
}
