using Anthropic;
using Anthropic.Core;
using CoolhandLabs.Http;

namespace CoolhandLabs.Anthropic;

/// <summary>
/// Builds Anthropic SDK clients whose Claude API calls are logged to Coolhand. Requires the official
/// <c>Anthropic</c> NuGet package (13.8.0 or later).
/// </summary>
/// <example>
/// <code>
/// AnthropicClient client = CoolhandAnthropic.CreateClient(o => o with { MaxRetries = 3 });
/// </code>
/// </example>
/// <remarks>
/// Capture is a <see cref="CoolhandHandler"/> appended to <see cref="ClientOptions.Handlers"/> (innermost,
/// next to the transport), so every attempt the SDK makes is logged, streamed messages included, with
/// credential headers redacted. The Anthropic <c>request-id</c> response header is logged with each call,
/// so feedback created with <c>LlmProviderUniqueId = response request id</c> matches it. When there is no
/// Coolhand client (no <c>COOLHAND_API_KEY</c> and no <see cref="Coolhand.InitializeGlobalMonitoring"/>),
/// the SDK client is built without the handler, so the same code runs with and without Coolhand.
/// </remarks>
public static class CoolhandAnthropic
{
    /// <summary>
    /// An <see cref="AnthropicClient"/> built from <c>new ClientOptions()</c> (which reads
    /// <c>ANTHROPIC_API_KEY</c>), then <paramref name="configure"/>, monitored by <see cref="Coolhand.Global"/>
    /// when it exists.
    /// </summary>
    /// <param name="configure">Adjusts the options, e.g. <c>o =&gt; o with { MaxRetries = 3 }</c>.</param>
    public static AnthropicClient CreateClient(Func<ClientOptions, ClientOptions>? configure = null) =>
        CreateClient(Coolhand.Global, configure);

    /// <summary>As <see cref="CreateClient(Func{ClientOptions, ClientOptions})"/>, monitored by <paramref name="coolhand"/> (null: unmonitored).</summary>
    public static AnthropicClient CreateClient(Coolhand? coolhand, Func<ClientOptions, ClientOptions>? configure = null)
    {
        var options = new ClientOptions();
        if (configure is not null) options = configure(options);
        return new AnthropicClient(WithCoolhand(options, coolhand));
    }

    /// <summary>
    /// <paramref name="options"/> with a <see cref="CoolhandHandler"/> for <paramref name="coolhand"/>
    /// (null: <see cref="Coolhand.Global"/>) appended to its handlers; unchanged when there is no client.
    /// </summary>
    public static ClientOptions WithCoolhand(this ClientOptions options, Coolhand? coolhand = null)
    {
        var client = coolhand ?? Coolhand.Global;
        if (client is null) return options;
        var handlers = new List<DelegatingHandler>(options.Handlers) { new CoolhandHandler(client) };
        options.Handlers = handlers;
        return options;
    }
}
