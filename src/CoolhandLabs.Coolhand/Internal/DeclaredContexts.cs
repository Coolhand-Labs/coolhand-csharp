namespace CoolhandLabs.Internal;

/// <summary>
/// The ambient declared context (coolhand-node's <c>declared-context.ts</c>, with an
/// <see cref="AsyncLocal{T}"/> in place of <c>AsyncLocalStorage</c>, so it flows across awaits and into
/// tasks started inside the block): validation, nesting and the current value.
/// </summary>
internal static class DeclaredContexts
{
    private static readonly AsyncLocal<DeclaredContext?> CurrentValue = new();

    /// <summary>The raw (possibly empty) context installed here, or null.</summary>
    public static DeclaredContext? Installed => CurrentValue.Value;

    /// <summary>The active declaration; null when none, or when the active one declares nothing.</summary>
    public static DeclaredContext? Current => CurrentValue.Value is { IsEmpty: false } ctx ? ctx : null;

    /// <summary>Installs <paramref name="ctx"/> (already merged) and returns what it replaced.</summary>
    public static DeclaredContext? Install(DeclaredContext? ctx)
    {
        var previous = CurrentValue.Value;
        CurrentValue.Value = ctx;
        return previous;
    }

    private static string? NonBlank(string? value) => value is not null && !JsText.IsBlank(value) ? value : null;

    private static void ValidateKey(string field, string key)
    {
        var problem = EntityKeys.KeyProblem(key);
        if (problem is not null) throw new ArgumentException("withContext: " + field + ".key " + problem);
    }

    private static DeclaredWorkload NormalizeWorkload(DeclaredWorkload w)
    {
        var id = NonBlank(w.Id);
        if (w.Key is not null) ValidateKey("workload", w.Key);
        if (id is null && w.Key is null) throw new ArgumentException("withContext: workload needs a non-blank \"id\" or a \"key\"");
        if (w.Description is not null)
        {
            var problem = EntityKeys.DescriptionProblem(w.Description);
            if (problem is not null) throw new ArgumentException("withContext: workload.description " + problem);
        }
        return w with { Id = id };
    }

    private static DeclaredTemplate NormalizeTemplate(DeclaredTemplate t)
    {
        var id = NonBlank(t.Id);
        if (t.Key is not null) ValidateKey("template", t.Key);
        if (id is null && t.Key is null) throw new ArgumentException("withContext: template needs a non-blank \"id\" or a \"key\"");
        return t with { Id = id };
    }

    /// <summary>
    /// Validates <paramref name="ctx"/> (throwing before anything runs) and merges it over
    /// <paramref name="parent"/>: per field the innermost value wins, workload/template are replaced
    /// wholesale, and a context that sets a workload without a template does not inherit the outer template.
    /// </summary>
    /// <exception cref="ArgumentException">For a malformed declaration.</exception>
    public static DeclaredContext Merge(DeclaredContext? parent, DeclaredContext? ctx)
    {
        if (ctx is null)
        {
            throw new ArgumentException("withContext: first argument must be a DeclaredContext ({ reporter, workload, template })");
        }
        var reporter = NonBlank(ctx.Reporter);
        var workload = ctx.Workload is null ? null : NormalizeWorkload(ctx.Workload);
        var template = ctx.Template is null ? null : NormalizeTemplate(ctx.Template);

        var mergedReporter = reporter ?? parent?.Reporter;
        var mergedWorkload = workload ?? parent?.Workload;
        var mergedTemplate = template ?? (workload is not null || parent is null ? null : parent.Template);

        // A template key is find-or-created under a workload; with none, the server 422s and drops the log.
        if (mergedTemplate is { Key: not null, Id: null } && mergedWorkload is null)
        {
            throw new ArgumentException(
                "withContext: template.key needs a workload in this or an enclosing withContext (or use template.id)");
        }
        return new DeclaredContext { Reporter = mergedReporter, Workload = mergedWorkload, Template = mergedTemplate };
    }
}
