using Microsoft.Extensions.Http;

namespace Aspire.IdentityServer.Client;

/// <summary>
/// Innermost handler of the token backchannel: every attempt that reaches the network, whether a
/// retry or a hedged copy, carries a newly signed assertion, since replaying one is refused.
/// </summary>
internal sealed class FreshClientAssertionHandler : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        // The previous content is not disposed: hedged attempts may still be reading it.
        if (request.Options.TryGetValue(TokenExchange.FreshContentKey, out var fresh))
            request.Content = fresh();
        return base.SendAsync(request, cancellationToken);
    }
}

/// <summary>
/// Appends <see cref="FreshClientAssertionHandler" /> after every handler configured for the
/// backchannel, so that resilience handlers added in any order stay outside it.
/// </summary>
internal sealed class FreshClientAssertionFilter : IHttpMessageHandlerBuilderFilter
{
    public Action<HttpMessageHandlerBuilder> Configure(Action<HttpMessageHandlerBuilder> next) =>
        builder =>
        {
            next(builder);
            if (
                string.Equals(
                    builder.Name,
                    TokenExchangeHttpClientExtensions.BackchannelClientName,
                    StringComparison.Ordinal
                )
            )
                builder.AdditionalHandlers.Add(new FreshClientAssertionHandler());
        };
}
