using System.Text.Json.Nodes;

namespace CoolhandLabs;

/// <summary>
/// A workload named by an existing hashid (<see cref="Id"/>) or a stable <see cref="Key"/> that is
/// find-or-created. <see cref="Name"/> and <see cref="Description"/> apply only when the server creates
/// the record; ingest never renames. Sent inside <c>llm_request_log.declared.workload</c>.
/// </summary>
/// <param name="Key">Stable key: at most 255 characters, no control or invisible characters, no leading or
/// trailing whitespace, no leading/trailing or doubled <c>/</c>.</param>
/// <param name="Name">Display name used on creation.</param>
/// <param name="Description">At most 1000 characters, used on creation.</param>
public sealed record DeclaredWorkload(string? Key, string? Name = null, string? Description = null)
{
    /// <summary>Existing workload hashid (wins over <see cref="Key"/> server-side).</summary>
    public string? Id { get; init; }

    /// <summary>A workload by key.</summary>
    public static DeclaredWorkload OfKey(string key, string? name = null, string? description = null) => new(key, name, description);

    /// <summary>A workload by existing hashid.</summary>
    public static DeclaredWorkload OfId(string id) => new((string?)null) { Id = id };

    internal JsonObject ToJson()
    {
        var obj = new JsonObject();
        if (Id is not null) obj["id"] = Id;
        if (Key is not null) obj["key"] = Key;
        if (Name is not null) obj["name"] = Name;
        if (Description is not null) obj["description"] = Description;
        return obj;
    }
}

/// <summary>
/// A template named by <see cref="Id"/> or <see cref="Key"/> (<see cref="Id"/> wins). A key needs a
/// workload in the same or an enclosing <see cref="Coolhand.WithContext{T}(DeclaredContext, Func{T})"/>.
/// Sent inside <c>llm_request_log.declared.template</c>.
/// </summary>
/// <param name="Key">Stable key, same rules as <see cref="DeclaredWorkload.Key"/>.</param>
/// <param name="Name">Display name used on creation.</param>
public sealed record DeclaredTemplate(string? Key, string? Name = null)
{
    /// <summary>Existing template hashid.</summary>
    public string? Id { get; init; }

    /// <summary>A template by key.</summary>
    public static DeclaredTemplate OfKey(string key, string? name = null) => new(key, name);

    /// <summary>A template by existing hashid.</summary>
    public static DeclaredTemplate OfId(string id) => new((string?)null) { Id = id };

    internal JsonObject ToJson()
    {
        var obj = new JsonObject();
        if (Id is not null) obj["id"] = Id;
        if (Key is not null) obj["key"] = Key;
        if (Name is not null) obj["name"] = Name;
        return obj;
    }
}

/// <summary>
/// What <see cref="Coolhand.WithContext{T}(DeclaredContext, Func{T})"/> declares, sent as
/// <c>llm_request_log.declared</c>: which workload and template the LLM calls made inside the block
/// belong to, so the server skips log-to-template matching. Every field is optional.
/// </summary>
/// <example>
/// <code>
/// var ctx = new DeclaredContext { Reporter = "qa-agent" }
///     .WithWorkload("qa/test-plans", "QA test plans", "Writes test plans from stories")
///     .WithTemplate("qa/test-plans/generate", "generate");
/// </code>
/// </example>
public sealed record DeclaredContext
{
    /// <summary>Who is declaring (applies only when a record is first created); blank means none.</summary>
    public string? Reporter { get; init; }

    /// <summary>The workload, or null to inherit the enclosing one.</summary>
    public DeclaredWorkload? Workload { get; init; }

    /// <summary>The template, or null to inherit the enclosing one (unless this context sets a workload, which resets the template).</summary>
    public DeclaredTemplate? Template { get; init; }

    /// <summary>True when nothing is declared (such a context is never sent).</summary>
    public bool IsEmpty => Reporter is null && Workload is null && Template is null;

    /// <summary>A copy with the reporter set.</summary>
    public DeclaredContext WithReporter(string? reporter) => this with { Reporter = reporter };

    /// <summary>A copy with a workload by key.</summary>
    public DeclaredContext WithWorkload(string key, string? name = null, string? description = null) =>
        this with { Workload = new DeclaredWorkload(key, name, description) };

    /// <summary>A copy with a template by key.</summary>
    public DeclaredContext WithTemplate(string key, string? name = null) => this with { Template = new DeclaredTemplate(key, name) };

    /// <summary>The wire form.</summary>
    public JsonObject ToJson()
    {
        var obj = new JsonObject();
        if (Reporter is not null) obj["reporter"] = Reporter;
        if (Workload is not null) obj["workload"] = Workload.ToJson();
        if (Template is not null) obj["template"] = Template.ToJson();
        return obj;
    }
}
