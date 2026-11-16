namespace CoolhandLabs.Tests.Support;

/// <summary>
/// A <see cref="FactAttribute"/> that skips unless <c>COOLHAND_LIVE_BASE_URL</c> and <c>COOLHAND_LIVE_API_KEY</c> are
/// set, so the default build and CI (no server, no key) report it as skipped instead of failing.
/// </summary>
public sealed class LiveFactAttribute : FactAttribute
{
    internal const string BaseUrlVariable = "COOLHAND_LIVE_BASE_URL";
    internal const string ApiKeyVariable = "COOLHAND_LIVE_API_KEY";

    /// <summary>Skips when the live server is not configured.</summary>
    public LiveFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(BaseUrlVariable))
            || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ApiKeyVariable)))
        {
            Skip = $"Set {BaseUrlVariable} and {ApiKeyVariable} to run against a live Coolhand server.";
        }
    }
}
