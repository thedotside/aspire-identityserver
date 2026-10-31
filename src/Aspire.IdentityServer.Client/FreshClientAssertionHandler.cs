namespace Aspire.IdentityServer.Client;

/// <summary>
/// Innermost handler of the token backchannel. When a resilience pipeline retries a token request,
/// every new attempt carries a newly signed assertion: replaying the first one would be refused.
/// </summary>
internal sealed class FreshClientAssertionHandler : DelegatingHandler
{
    private static readonly HttpRequestOptionsKey<bool> Sent = new(
        "TheDotSide.IdentityServer.ClientAssertionSent"
    );

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        if (request.Options.TryGetValue(TokenExchange.FreshContentKey, out var fresh))
        {
            if (request.Options.TryGetValue(Sent, out var sent) && sent)
            {
                var previous = request.Content;
                request.Content = fresh();
                previous?.Dispose();
            }
            request.Options.Set(Sent, true);
        }
        return base.SendAsync(request, cancellationToken);
    }
}
