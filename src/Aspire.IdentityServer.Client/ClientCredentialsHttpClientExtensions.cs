using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Aspire.IdentityServer.Client;

/// <summary>Request-time settings for client-credentials tokens on one named HttpClient.</summary>
public sealed class ClientCredentialsHandlerOptions
{
    /// <summary>The requested scopes; defaults to <see cref="IdentityClientOptions.Scopes" />.</summary>
    public string? Scope { get; set; }

    /// <summary>
    /// The API origins (scheme, host and port) that may receive the token. When empty, any HTTPS
    /// address of this named client may; declare the API base address to bound it.
    /// </summary>
    public ICollection<Uri> AllowedOrigins { get; } = [];

    /// <summary>The time budget of discovery plus token request, retries included. 30 s by default.</summary>
    public TimeSpan BackchannelTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How long before its expiry a cached token is renewed. 30 s by default.</summary>
    public TimeSpan RefreshBeforeExpiration { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>Adds client-credentials tokens to named <see cref="HttpClient" /> instances.</summary>
public static class ClientCredentialsHttpClientExtensions
{
    /// <summary>
    /// Sends every request of this named client with a client-credentials access token. The
    /// token is cached until shortly before it expires, and forgotten when a resource answers
    /// 401; each token request attempt carries fresh client authentication. It shares the
    /// discovery cache and the backchannel of the token-exchange handler.
    /// </summary>
    public static IHttpClientBuilder AddClientCredentialsHandler(
        this IHttpClientBuilder builder,
        IConfigurationSection clientConfiguration,
        Action<ClientCredentialsHandlerOptions>? configure = null
    )
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(clientConfiguration);
        if (
            string.Equals(
                builder.Name,
                TokenExchangeHttpClientExtensions.BackchannelClientName,
                StringComparison.Ordinal
            )
        )
            throw new ArgumentException(
                "The token backchannel cannot use a client-credentials handler.",
                nameof(builder)
            );
        var settings = new ClientCredentialsHandlerOptions();
        configure?.Invoke(settings);
        if (settings.BackchannelTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(
                nameof(configure),
                "BackchannelTimeout must be positive."
            );
        if (settings.RefreshBeforeExpiration < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(
                nameof(configure),
                "RefreshBeforeExpiration cannot be negative."
            );
        builder.Services.AddOptions<IdentityClientOptions>(builder.Name).Bind(clientConfiguration);
        TokenBackchannel.Register(builder.Services);
        builder.Services.TryAddSingleton(services => new ClientCredentialsTokenCache(
            services.GetService<TimeProvider>() ?? TimeProvider.System
        ));
        return builder.AddHttpMessageHandler(services => new ClientCredentialsHandler(
            services.GetRequiredService<IHttpClientFactory>(),
            services.GetRequiredService<IOptionsMonitor<IdentityClientOptions>>(),
            services.GetRequiredService<TokenExchangeMetadata>(),
            services.GetRequiredService<ClientCredentialsTokenCache>(),
            builder.Name,
            settings
        ));
    }
}
