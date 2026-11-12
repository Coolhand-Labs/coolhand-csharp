using CoolhandLabs;
using CoolhandLabs.Http;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// <c>IHttpClientFactory</c> integration: the closest .NET has to coolhand-node's global monitoring.
/// </summary>
public static class CoolhandServiceCollectionExtensions
{
    /// <summary>
    /// Monitors the LLM API calls of every <see cref="HttpClient"/> the factory creates (named, typed and
    /// default clients alike), by adding a <see cref="CoolhandHandler"/> to the factory's defaults.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="options">The client's options; null uses <see cref="Coolhand.Global"/> (from <c>COOLHAND_API_KEY</c>),
    /// and when there is none the handler is a pass-through that starts logging if a global client appears later.</param>
    /// <returns><paramref name="services"/>, for chaining. The <see cref="Coolhand"/> client, when there is one, is registered as a singleton.</returns>
    public static IServiceCollection AddCoolhand(this IServiceCollection services, CoolhandOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var client = options is null ? Coolhand.Global : new Coolhand(options);
        if (client is not null) services.TryAddSingleton(client);
        services.ConfigureHttpClientDefaults(builder => builder.AddHttpMessageHandler(() =>
            client is null ? new CoolhandHandler() : new CoolhandHandler(client)));
        return services;
    }

    /// <summary>Monitors one named or typed client only.</summary>
    /// <param name="builder">The client's builder.</param>
    /// <param name="coolhand">The client to log through; null uses <see cref="Coolhand.Global"/> per request.</param>
    public static IHttpClientBuilder AddCoolhandHandler(this IHttpClientBuilder builder, Coolhand? coolhand = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddHttpMessageHandler(() => coolhand is null ? new CoolhandHandler() : new CoolhandHandler(coolhand));
    }
}
