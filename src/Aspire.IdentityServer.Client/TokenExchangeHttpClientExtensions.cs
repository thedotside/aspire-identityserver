using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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

    public string? Audience { get; set; }
    public string? Scope { get; set; }
}

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
        builder.Services.AddOptions<IdentityClientOptions>(builder.Name).Bind(clientConfiguration);
        builder.Services.AddHttpClient(BackchannelClientName);
        return builder.AddHttpMessageHandler(services => new TokenExchangeHandler(
            services.GetRequiredService<IHttpClientFactory>(),
            services.GetRequiredService<IOptionsMonitor<IdentityClientOptions>>(),
            builder.Name,
            settings,
            services
        ));
    }
}
