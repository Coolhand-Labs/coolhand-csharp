using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;
using CoolhandLabs.Http;
using CoolhandLabs.Internal;
using CoolhandLabs.Models;
using CoolhandLabs.Services;

namespace CoolhandLabs;

/// <summary>
/// The Coolhand client: logs LLM calls captured by <see cref="CoolhandHandler"/>, submits feedback and
/// manual logs, declares workloads with <see cref="WithContext{T}(DeclaredContext, Func{T})"/>, and reads
/// logs, feedback, templates, workloads and more back.
/// </summary>
/// <example>
/// <code>
/// var coolhand = new Coolhand(new CoolhandOptions { ApiKey = Environment.GetEnvironmentVariable("COOLHAND_API_KEY") });
/// using var http = coolhand.CreateHttpClient();   // LLM calls made through this client are logged
/// </code>
/// </example>
/// <remarks>
/// Error semantics follow coolhand-node: <see cref="CreateFeedbackAsync"/>, <see cref="LogRequestAsync"/>
/// and <see cref="UploadClientFileAsync"/> never throw for API or network failures and return null
/// instead; read and admin methods throw <see cref="CoolhandException"/> (network, bad JSON) or
/// <see cref="CoolhandHttpException"/> (non-2xx, with <see cref="CoolhandHttpException.StatusCode"/>);
/// invalid arguments throw <see cref="ArgumentException"/> before any request. A client is thread-safe
/// and meant to be shared; captured calls are submitted in the background, so call
/// <see cref="FlushAsync"/> (or dispose the client) before a short-lived process exits.
/// </remarks>
public sealed class Coolhand : IDisposable, IAsyncDisposable
{
    private readonly bool _silent;
    private readonly ConcurrentDictionary<Task, byte> _pending = new();

    /// <summary>Creates a client.</summary>
    /// <exception cref="ArgumentException">If the API key is missing or not a valid header value, the base URL is invalid, or the timeout is out of range.</exception>
    public Coolhand(CoolhandOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _silent = options.Silent;
        if (!options.HasApiKey)
        {
            Internal.Log.Error("❌ API key is required for logging. Pass it in CoolhandOptions.ApiKey");
            throw new ArgumentException("API key is required", nameof(options));
        }
        var log = new Log(_silent);
        PatternMatchingService = new PatternMatchingService(options.PatternsFile, _silent);
        var config = ServiceConfig.From(options);
        LoggingService = new LoggingService(config);
        FeedbackService = new FeedbackService(config);
        TemplateService = new TemplateService(config);
        StepTemplateService = new StepTemplateService(config);
        WorkloadService = new WorkloadService(config);
        ClientFileService = new ClientFileService(config);
        LlmReferenceService = new LlmReferenceService(config);
        HelpCenterDocsService = new HelpCenterDocsService(config);
        OptimizationFeedbackLinkService = new OptimizationFeedbackLinkService(config);
        McpService = new McpService(config);
        UrlFilters.WarnOnGlobLikePatterns(options.ExcludeApiPatterns);
        var exclude = options.ExcludeApiPatterns ?? CoolhandOptions.DefaultExcludeApiPatterns;
        Monitor = new RequestMonitor(PatternMatchingService, exclude, LoggingService.ApiEndpoint, _silent, options.CaptureIdleTimeout,
            SubmitCaptured);

        log.Info("🔍 Setting up Coolhand...");
        if (options.DryRun) log.Info("🚫 DRY RUN MODE: API calls will be skipped — no data will be submitted");
        if (options.Debug) log.Info("🔬 DEBUG MODE: Verbose logging enabled");
        log.Info("🎯 API Endpoint: " + LoggingService.ApiEndpoint);
        log.Info("✅ Coolhand ready - will log to API");
    }

    private void SubmitCaptured(CallData callData, MatchedPattern matched, DeclaredContext? declared) =>
        Track(LoggingService.LogRequestToApiAsync(callData, matched, CollectionMethod.GlobalMonitoring, null, null, declared));

    private Task<T> Track<T>(Task<T> task)
    {
        if (task.IsCompleted) return task;
        _pending.TryAdd(task, 0);
        task.ContinueWith(t => _pending.TryRemove(t, out _), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return task;
    }

    // ------------------------------------------------------------------ declared context

    /// <summary>
    /// Runs <paramref name="call"/> with <paramref name="context"/> declared for every LLM call captured
    /// while it runs (sent as <c>llm_request_log.declared</c>), and returns its result. The context flows
    /// across awaits (an <see cref="AsyncLocal{T}"/>), so a <c>Func&lt;Task&lt;T&gt;&gt;</c> works here too.
    /// Nests: per field the innermost value wins, workload and template are replaced wholesale, and a
    /// context that sets a workload without a template does not inherit the outer template. Exceptions
    /// from <paramref name="call"/> propagate unchanged.
    /// </summary>
    /// <exception cref="ArgumentException">Before <paramref name="call"/> runs, for a malformed declaration (blank or invalid key,
    /// over-long description, a template key with no workload in scope).</exception>
    public static T WithContext<T>(DeclaredContext context, Func<T> call)
    {
        if (call is null) throw new ArgumentException("withContext: second argument must be a function", nameof(call));
        var merged = DeclaredContexts.Merge(DeclaredContexts.Installed, context);
        var previous = DeclaredContexts.Install(merged);
        try
        {
            return call();
        }
        finally
        {
            DeclaredContexts.Install(previous);
        }
    }

    /// <summary><see cref="WithContext{T}(DeclaredContext, Func{T})"/> for a block with no result.</summary>
    public static void WithContext(DeclaredContext context, Action call)
    {
        if (call is null) throw new ArgumentException("withContext: second argument must be a function", nameof(call));
        WithContext<object?>(context, () =>
        {
            call();
            return null;
        });
    }

    /// <summary>Awaits <paramref name="call"/> with <paramref name="context"/> declared; see <see cref="WithContext{T}(DeclaredContext, Func{T})"/>.</summary>
    public static async Task<T> WithContextAsync<T>(DeclaredContext context, Func<Task<T>> call)
    {
        if (call is null) throw new ArgumentException("withContext: second argument must be a function", nameof(call));
        var merged = DeclaredContexts.Merge(DeclaredContexts.Installed, context);
        var previous = DeclaredContexts.Install(merged);
        try
        {
            return await call().ConfigureAwait(false);
        }
        finally
        {
            DeclaredContexts.Install(previous);
        }
    }

    /// <summary>Awaits <paramref name="call"/> with <paramref name="context"/> declared.</summary>
    public static Task WithContextAsync(DeclaredContext context, Func<Task> call)
    {
        if (call is null) throw new ArgumentException("withContext: second argument must be a function", nameof(call));
        return WithContextAsync<object?>(context, async () =>
        {
            await call().ConfigureAwait(false);
            return null;
        });
    }

    /// <summary>The declaration active here, or null (an empty declaration counts as none).</summary>
    public static DeclaredContext? CurrentContext => DeclaredContexts.Current;

    // ------------------------------------------------------------------ global monitoring

    private static readonly object GlobalLock = new();
    private static volatile Coolhand? _global;
    private static volatile bool _envInitAttempted;

    /// <summary>
    /// Installs the process-wide client that <see cref="CoolhandHandler"/> and the integrations use when
    /// none is passed to them (coolhand-node's <c>initializeGlobalMonitoring</c>). A second call while one
    /// is active returns the active one.
    /// </summary>
    public static Coolhand InitializeGlobalMonitoring(CoolhandOptions options)
    {
        lock (GlobalLock)
        {
            if (_global is not null) return _global;
            var created = new Coolhand(options);
            _global = created;
            if (!created._silent) new Log(false).Info("🌐 Global Coolhand monitoring initialized");
            return created;
        }
    }

    /// <summary>
    /// The process-wide client: the one installed by <see cref="InitializeGlobalMonitoring"/>, otherwise
    /// one created once from the environment (<see cref="CoolhandOptions.FromEnvironment()"/>) when
    /// <c>COOLHAND_API_KEY</c> is set, as coolhand-node's <c>auto-monitor</c> does. Null when there is
    /// neither, or when the environment configuration is invalid (logged).
    /// </summary>
    public static Coolhand? Global
    {
        get
        {
            var current = _global;
            if (current is not null || _envInitAttempted) return current;
            lock (GlobalLock)
            {
                if (_global is null && !_envInitAttempted)
                {
                    _envInitAttempted = true;
                    var options = CoolhandOptions.FromEnvironment();
                    if (options.HasApiKey)
                    {
                        try
                        {
                            _global = new Coolhand(options);
                        }
                        catch (Exception e)
                        {
                            Internal.Log.Error("❌ Failed to initialize global monitoring: " + e.Message);
                        }
                    }
                }
                return _global;
            }
        }
    }

    /// <summary>True when Coolhand can log: a global client is installed, or <c>COOLHAND_API_KEY</c> is set (non-blank).</summary>
    public static bool IsConfigured => _global is not null || !JsText.IsBlank(Environment.GetEnvironmentVariable("COOLHAND_API_KEY"));

    /// <summary>True once a global client exists.</summary>
    public static bool IsGlobalMonitoringActive => _global is not null;

    /// <summary>Statistics of the global client, or zeros with <c>ApiEndpoint</c> <c>"Not initialized"</c>.</summary>
    public static CoolhandStats GetGlobalStats() => _global?.GetStats() ?? new CoolhandStats(0, 0, "Not initialized");

    /// <summary>Drops the global client (and lets the environment be read again), flushing it first. For tests.</summary>
    public static void ResetGlobalMonitoring()
    {
        Coolhand? current;
        lock (GlobalLock)
        {
            current = _global;
            _global = null;
            _envInitAttempted = false;
        }
        current?.Dispose();
    }

    // ------------------------------------------------------------------ HTTP integration

    /// <summary>A fresh, unattached <see cref="CoolhandHandler"/> bound to this client (for <c>ClientOptions.Handlers</c> or a handler pipeline).</summary>
    public DelegatingHandler CreateHandler() => new CoolhandHandler(this);

    /// <summary>
    /// An <see cref="HttpClient"/> whose LLM calls are logged through this client. It sends through
    /// <paramref name="inner"/> (disposed with the client), or a new <see cref="HttpClientHandler"/> with
    /// automatic decompression.
    /// </summary>
    public HttpClient CreateHttpClient(HttpMessageHandler? inner = null) =>
        new(new CoolhandHandler(this, inner ?? new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.All }));

    /// <summary>The capture engine <see cref="CoolhandHandler"/> feeds; see <see cref="RequestMonitor"/> to write your own integration.</summary>
    public RequestMonitor Monitor { get; }

    // ------------------------------------------------------------------ logs

    /// <summary>
    /// Submits one call that did not flow through an integration (e.g. replayed sessions). The declared
    /// context active here is sent with it. Never throws for API or network failures.
    /// </summary>
    /// <param name="rawRequest">The call.</param>
    /// <param name="collector">Overrides the SDK collector string.</param>
    /// <param name="metadata">Free-form metadata (convention: <c>project_path</c>).</param>
    /// <param name="ct">Cancels the submission.</param>
    /// <returns>The created log, or null on failure or a dry run.</returns>
    public Task<LogResponse?> LogRequestAsync(CallData rawRequest, string? collector = null, IReadOnlyDictionary<string, object?>? metadata = null,
        CancellationToken ct = default) =>
        Track(LoggingService.LogRequestToApiAsync(rawRequest, null, CollectionMethod.Manual, collector, metadata, DeclaredContexts.Current, ct));

    /// <summary>Full input/output content of one log by id (private API key).</summary>
    public Task<JsonNode> GetLogContentAsync(string logId, GetLogContentOptions? options = null, CancellationToken ct = default) =>
        LoggingService.GetLogContentAsync(logId, options, ct);

    /// <summary>Searches logs by named filters (private API key).</summary>
    public Task<Page<JsonNode>> SearchLogsAsync(SearchLogsParams? parameters = null, CancellationToken ct = default) =>
        LoggingService.SearchLogsAsync(parameters, ct);

    /// <summary>Recent failures rolled up by error pattern and template (private API key).</summary>
    public Task<JsonNode> FailureSummaryAsync(FailureSummaryParams? parameters = null, CancellationToken ct = default) =>
        LoggingService.FailureSummaryAsync(parameters, ct);

    // ------------------------------------------------------------------ feedback

    /// <summary>Creates feedback for an LLM output. Never throws for API or network failures.</summary>
    /// <returns>The created record (with <c>LlmRequestLogId</c> when it matched a log), or null on any failure or a dry run.</returns>
    public Task<FeedbackResponse?> CreateFeedbackAsync(LlmRequestLogFeedback feedback, CancellationToken ct = default) =>
        Track(FeedbackService.CreateFeedbackAsync(feedback, CollectionMethod.Manual, ct));

    /// <summary>Searches feedback with raw Ransack predicates (private API key).</summary>
    public Task<Page<JsonNode>> SearchFeedbackAsync(SearchFeedbackParams? parameters = null, CancellationToken ct = default) =>
        FeedbackService.SearchFeedbackAsync(parameters, ct);

    /// <summary>One feedback record with partials (private API key).</summary>
    public Task<JsonNode> GetFeedbackAsync(string id, CancellationToken ct = default) => FeedbackService.GetFeedbackAsync(id, ct);

    // ------------------------------------------------------------------ templates, workloads

    /// <summary>Lists templates (private API key).</summary>
    public Task<Page<JsonNode>> SearchTemplatesAsync(SearchTemplatesParams? parameters = null, CancellationToken ct = default) =>
        TemplateService.SearchTemplatesAsync(parameters, ct);

    /// <summary>One template with its prompt patterns (private API key).</summary>
    public Task<JsonNode> GetTemplateAsync(string id, MetricsWindow? metrics = null, CancellationToken ct = default) =>
        TemplateService.GetTemplateAsync(id, metrics, ct);

    /// <summary>Lists step templates (private API key).</summary>
    public Task<Page<JsonNode>> ListStepTemplatesAsync(ListStepTemplatesParams? parameters = null, CancellationToken ct = default) =>
        StepTemplateService.ListStepTemplatesAsync(parameters, ct);

    /// <summary>One step template by integer id (private API key).</summary>
    public Task<JsonNode> GetStepTemplateAsync(long id, bool? includeMetrics = null, CancellationToken ct = default) =>
        StepTemplateService.GetStepTemplateAsync(id, includeMetrics, ct);

    /// <summary>Lists workloads with optional metrics (private API key).</summary>
    public Task<Page<JsonNode>> SearchWorkloadsAsync(SearchWorkloadsParams? parameters = null, CancellationToken ct = default) =>
        WorkloadService.SearchWorkloadsAsync(parameters, ct);

    /// <summary>Creates or updates a workload by key (private API key); null on a dry run.</summary>
    public Task<UpsertResult?> UpsertWorkloadAsync(UpsertWorkloadParams parameters, CancellationToken ct = default) =>
        WorkloadService.UpsertAsync(parameters, ct);

    /// <summary>Creates or updates a template by key (private API key); null on a dry run.</summary>
    public Task<UpsertResult?> UpsertTemplateAsync(UpsertTemplateParams parameters, CancellationToken ct = default) =>
        TemplateService.UpsertAsync(parameters, ct);

    // ------------------------------------------------------------------ referenced files

    /// <summary>Files your logged requests reference, one row per path (private API key).</summary>
    public Task<Page<JsonNode>> SearchReferencedFilesAsync(SearchReferencedFilesParams? parameters = null, CancellationToken ct = default) =>
        LlmReferenceService.SearchReferencedFilesAsync(parameters, ct);

    /// <summary>The sessions behind one exact file path (private API key).</summary>
    public Task<Page<JsonNode>> ListReferencedFileSessionsAsync(ListReferencedFileSessionsParams parameters, CancellationToken ct = default) =>
        LlmReferenceService.ListReferencedFileSessionsAsync(parameters, ct);

    // ------------------------------------------------------------------ client files

    /// <summary>Uploads a file as a draft (private API key). Never throws for API or network failures.</summary>
    public Task<ClientFile?> UploadClientFileAsync(ClientFileUpload payload, CancellationToken ct = default) =>
        Track(ClientFileService.CreateClientFileAsync(payload, ct));

    /// <summary>Lists client files (a client admin's per-user key).</summary>
    public Task<Page<ClientFile>> ListClientFilesAsync(ListClientFilesParams? parameters = null, CancellationToken ct = default) =>
        ClientFileService.ListClientFilesAsync(parameters, ct);

    /// <summary>Downloads a client file's bytes (a client admin's per-user key).</summary>
    public Task<DownloadedClientFile> DownloadClientFileAsync(string id, CancellationToken ct = default) =>
        ClientFileService.DownloadClientFileAsync(id, ct);

    /// <summary>Sets a client file's status (a client admin's per-user key); null on a dry run.</summary>
    public Task<ClientFile?> UpdateClientFileStatusAsync(string id, string status, CancellationToken ct = default) =>
        ClientFileService.UpdateClientFileStatusAsync(id, status, ct);

    // ------------------------------------------------------------------ optimization links

    /// <summary>Links feedback to an optimization (private API key); null on a dry run.</summary>
    public Task<OptimizationFeedbackLink?> LinkFeedbackAsync(string optimizationId, string feedbackId, CancellationToken ct = default) =>
        OptimizationFeedbackLinkService.LinkFeedbackAsync(optimizationId, feedbackId, ct);

    /// <summary>Links feedback to an optimization with a note (private API key); null on a dry run.</summary>
    public Task<OptimizationFeedbackLink?> LinkFeedbackAsync(string optimizationId, string feedbackId, string? note, CancellationToken ct = default) =>
        OptimizationFeedbackLinkService.LinkFeedbackAsync(optimizationId, feedbackId, note, ct);

    /// <summary>Links many feedbacks in batches of 100 (private API key); null on a dry run.</summary>
    public Task<BulkLinkFeedbackResult?> BulkLinkFeedbackAsync(string optimizationId, IReadOnlyList<string> feedbackIds, CancellationToken ct = default) =>
        OptimizationFeedbackLinkService.BulkLinkFeedbackAsync(optimizationId, feedbackIds, ct);

    /// <summary>Links many feedbacks with a note (private API key); null on a dry run.</summary>
    public Task<BulkLinkFeedbackResult?> BulkLinkFeedbackAsync(string optimizationId, IReadOnlyList<string> feedbackIds, string? note,
        CancellationToken ct = default) =>
        OptimizationFeedbackLinkService.BulkLinkFeedbackAsync(optimizationId, feedbackIds, note, ct);

    /// <summary>Removes a link by its hashid (private API key).</summary>
    public Task UnlinkFeedbackAsync(string optimizationId, string linkId, CancellationToken ct = default) =>
        OptimizationFeedbackLinkService.UnlinkFeedbackAsync(optimizationId, linkId, ct);

    // ------------------------------------------------------------------ help center

    /// <summary>Searches the help center (sends this client's key).</summary>
    public Task<Page<JsonNode>> SearchHelpArticlesAsync(SearchHelpArticlesParams? parameters = null, CancellationToken ct = default) =>
        HelpCenterDocsService.SearchHelpArticlesAsync(parameters, ct);

    /// <summary>One help article by slug.</summary>
    public Task<JsonNode> GetHelpArticleAsync(string slug, CancellationToken ct = default) => HelpCenterDocsService.GetHelpArticleAsync(slug, ct);

    /// <summary>The help center sections.</summary>
    public Task<IReadOnlyList<JsonNode>> ListHelpCategoriesAsync(CancellationToken ct = default) => HelpCenterDocsService.ListHelpCategoriesAsync(ct);

    // ------------------------------------------------------------------ monitoring

    /// <summary>Headers as they would be logged (credentials redacted).</summary>
    public IReadOnlyDictionary<string, string> SanitizeHeaders(IEnumerable<KeyValuePair<string, string>> headers, ApiPattern? pattern = null) =>
        PatternMatchingService.SanitizeHeaders(headers, pattern);

    /// <summary>This client's counters.</summary>
    public CoolhandStats GetStats() => new(Monitor.TotalRequests, Monitor.InterceptedCalls, LoggingService.ApiEndpoint);

    /// <summary>The URL substrings never captured. Setting it warns about glob-like entries.</summary>
    public IReadOnlyList<string> ExcludeApiPatterns
    {
        get => Monitor.ExcludeApiPatterns;
        set => Monitor.ExcludeApiPatterns = value;
    }

    /// <summary>
    /// Waits until every captured call whose body has ended, and every submission started so far, has
    /// been handed to the Coolhand API (or failed), up to <paramref name="timeout"/>.
    /// </summary>
    /// <returns>True if nothing was left pending.</returns>
    public async Task<bool> FlushAsync(TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            if (Monitor.PendingFinalizations == 0 && _pending.IsEmpty) return true;
            if (stopwatch.Elapsed >= timeout) return false;
            await Task.Delay(5).ConfigureAwait(false);
        }
    }

    /// <summary>Flushes for up to five seconds. The client stays usable.</summary>
    public void Dispose() => FlushAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false).GetAwaiter().GetResult();

    /// <summary>Flushes for up to five seconds. The client stays usable.</summary>
    public async ValueTask DisposeAsync() => await FlushAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

    // ------------------------------------------------------------------ services

    /// <summary>Provider patterns and redaction.</summary>
    public PatternMatchingService PatternMatchingService { get; }
    /// <summary>Log submission and log reads.</summary>
    public LoggingService LoggingService { get; }
    /// <summary>Feedback.</summary>
    public FeedbackService FeedbackService { get; }
    /// <summary>Templates.</summary>
    public TemplateService TemplateService { get; }
    /// <summary>Step templates.</summary>
    public StepTemplateService StepTemplateService { get; }
    /// <summary>Workloads.</summary>
    public WorkloadService WorkloadService { get; }
    /// <summary>Client files.</summary>
    public ClientFileService ClientFileService { get; }
    /// <summary>Referenced files.</summary>
    public LlmReferenceService LlmReferenceService { get; }
    /// <summary>Help center.</summary>
    public HelpCenterDocsService HelpCenterDocsService { get; }
    /// <summary>Optimization feedback links.</summary>
    public OptimizationFeedbackLinkService OptimizationFeedbackLinkService { get; }
    /// <summary>Coolhand's MCP endpoint.</summary>
    public McpService McpService { get; }
}
