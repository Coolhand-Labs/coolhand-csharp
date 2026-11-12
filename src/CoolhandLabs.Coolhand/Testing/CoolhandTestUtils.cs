namespace CoolhandLabs.Testing;

/// <summary>The interception counter at one moment, for <see cref="CoolhandTestUtils.AssertInterceptionOccurred(InterceptionSnapshot, string)"/>.</summary>
/// <param name="InterceptedCalls">The client's <c>InterceptedCalls</c> when the snapshot was taken.</param>
public sealed record InterceptionSnapshot(long InterceptedCalls);

/// <summary>Thrown when an expected interception did not happen.</summary>
public sealed class InterceptionAssertionException : Exception
{
    /// <summary>Creates the exception.</summary>
    public InterceptionAssertionException(string message) : base(message) { }
}

/// <summary>
/// Helpers for your own tests: prove that a code path's LLM calls are really monitored (coolhand-node's
/// <c>coolhand-node/test-utils</c>).
/// </summary>
/// <example>
/// <code>
/// var before = CoolhandTestUtils.CaptureInterceptionSnapshot(coolhand);
/// await myService.SummariseAsync("...");
/// CoolhandTestUtils.AssertInterceptionOccurred(coolhand, before);
/// </code>
/// </example>
/// <remarks>
/// The counter is per client and not scoped to a request: a delta proves some matched request was
/// intercepted in the window, not that a particular concurrent one was. A failure means the call did not
/// go through a monitored client (an <see cref="HttpClient"/> without <see cref="Http.CoolhandHandler"/>),
/// did not match a provider pattern, was excluded, or was a non-inference call.
/// </remarks>
public static class CoolhandTestUtils
{
    /// <summary>Snapshot of the global client's counter (zero when there is no global client).</summary>
    public static InterceptionSnapshot CaptureInterceptionSnapshot() => new(Coolhand.GetGlobalStats().InterceptedCalls);

    /// <summary>Snapshot of <paramref name="coolhand"/>'s counter.</summary>
    public static InterceptionSnapshot CaptureInterceptionSnapshot(Coolhand coolhand) => new(coolhand.GetStats().InterceptedCalls);

    /// <summary>Asserts the global client intercepted at least one request since <paramref name="before"/>.</summary>
    /// <exception cref="InterceptionAssertionException">If it did not.</exception>
    public static void AssertInterceptionOccurred(InterceptionSnapshot before, string? message = null) =>
        Check(before, CaptureInterceptionSnapshot(), message);

    /// <summary>Asserts <paramref name="coolhand"/> intercepted at least one request since <paramref name="before"/>.</summary>
    /// <exception cref="InterceptionAssertionException">If it did not.</exception>
    public static void AssertInterceptionOccurred(Coolhand coolhand, InterceptionSnapshot before, string? message = null) =>
        Check(before, CaptureInterceptionSnapshot(coolhand), message);

    private static void Check(InterceptionSnapshot before, InterceptionSnapshot after, string? message)
    {
        if (after.InterceptedCalls > before.InterceptedCalls) return;
        throw new InterceptionAssertionException(message
            ?? "Expected coolhand-csharp to intercept at least one new request, but InterceptedCalls did not increase (before: "
            + before.InterceptedCalls + ", after: " + after.InterceptedCalls + "). This can mean the request was sent through an "
            + "HttpClient without CoolhandHandler, or that it didn't match a configured API pattern (or matched an excluded/self "
            + "pattern, or was a non-inference call) — see https://github.com/Coolhand-Labs/coolhand-csharp/blob/main/docs/"
            + "global-monitoring.md troubleshooting.");
    }
}
