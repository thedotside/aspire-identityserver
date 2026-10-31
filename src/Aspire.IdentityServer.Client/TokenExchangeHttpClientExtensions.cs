using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;

namespace Aspire.IdentityServer.Client;

/// <summary>Request-time settings for token exchange on one named HttpClient.</summary>
public sealed class TokenExchangeHandlerOptions
{
    /// <summary>
    /// Reads the user token for each outgoing request. The service provider belongs to the
    /// handler scope, not the incoming request scope. Use a singleton context accessor, or
    /// that context's RequestServices for request-scoped services. Never capture a user token.
    /// When unset, the handler reads TokenExchange.SubjectTokenKey from the request options.
    /// </summary>
    public Func<
        IServiceProvider,
        HttpRequestMessage,
        CancellationToken,
        ValueTask<string?>
    >? SubjectTokenProvider { get; set; }

    /// <summary>The requested audience; defaults to <see cref="IdentityClientOptions.TokenExchangeAudience" />.</summary>
    public string? Audience { get; set; }

    /// <summary>The requested scopes; defaults to <see cref="IdentityClientOptions.Scopes" />.</summary>
    public string? Scope { get; set; }

    /// <summary>
    /// The API origins (scheme, host and port) that may receive the exchanged token. When empty,
    /// any HTTPS address of this named client may; declare the API base address to bound it.
    /// </summary>
    public ICollection<Uri> AllowedOrigins { get; } = [];

    /// <summary>The time budget of discovery plus exchange, retries included. 30 s by default.</summary>
    public TimeSpan BackchannelTimeout { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>Adds token exchange to named <see cref="HttpClient" /> instances.</summary>
public static class TokenExchangeHttpClientExtensions
{
    /// <summary>The independent client used for discovery and token requests, without this handler.</summary>
    public const string BackchannelClientName =
        "TheDotSide.IdentityServer.TokenExchange.Backchannel";

    /// <summary>Enables token exchange only for this named client. No user tokens are cached.</summary>
    public static IHttpClientBuilder AddTokenExchangeHandler(
        this IHttpClientBuilder builder,
        IConfigurationSection clientConfiguration,
        Action<TokenExchangeHandlerOptions>? configure = null
    )
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(clientConfiguration);
        if (string.Equals(builder.Name, BackchannelClientName, StringComparison.Ordinal))
            throw new ArgumentException(
                "The token backchannel cannot use its own exchange handler.",
                nameof(builder)
            );
        var settings = new TokenExchangeHandlerOptions();
        configure?.Invoke(settings);
        if (settings.BackchannelTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(
                nameof(configure),
                "BackchannelTimeout must be positive."
            );
        builder.Services.AddOptions<IdentityClientOptions>(builder.Name).Bind(clientConfiguration);
        // The shared backchannel is configured once, whatever the number of exchange handlers.
        if (!builder.Services.Any(d => d.ServiceType == typeof(TokenExchangeMetadata)))
        {
            builder.Services.AddSingleton(services => new TokenExchangeMetadata(
                services.GetService<TimeProvider>() ?? TimeProvider.System
            ));
            builder.Services.AddHttpClient(BackchannelClientName);
            builder.Services.TryAddEnumerable(
                ServiceDescriptor.Singleton<
                    IHttpMessageHandlerBuilderFilter,
                    FreshClientAssertionFilter
                >()
            );
        }
        return builder.AddHttpMessageHandler(services => new TokenExchangeHandler(
            services.GetRequiredService<IHttpClientFactory>(),
            services.GetRequiredService<IOptionsMonitor<IdentityClientOptions>>(),
            services.GetRequiredService<TokenExchangeMetadata>(),
            builder.Name,
            settings,
            services
        ));
    }
}
