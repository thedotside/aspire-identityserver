using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Aspire.IdentityServer.Client;

internal sealed class TokenExchangeHandler(
    IHttpClientFactory clients,
    IOptionsMonitor<IdentityClientOptions> clientOptions,
    TokenExchangeMetadata metadata,
    string clientName,
    TokenExchangeHandlerOptions settings,
    IServiceProvider services
) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        TokenRequests.EnsureSendable(request, settings.AllowedOrigins, "token-exchange");
        string? subject;
        if (settings.SubjectTokenProvider is { } provider)
            subject = await provider(services, request, cancellationToken).ConfigureAwait(false);
        else
            request.Options.TryGetValue(TokenExchange.SubjectTokenKey, out subject);
        if (string.IsNullOrWhiteSpace(subject))
            throw new InvalidOperationException(
                "A user access token is required. Set SubjectTokenProvider or the request's TokenExchange.SubjectTokenKey."
            );

        var client = clientOptions.Get(clientName);
        TokenRequests.EnsureAuthority(client);
        var accessToken = await ExchangeAsync(client, subject, cancellationToken)
            .ConfigureAwait(false);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        try
        {
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Keep user-specific state off pooled handlers, and permit an outer retry to
            // perform a new exchange with fresh client authentication.
            request.Headers.Authorization = null;
        }
    }

    private async Task<string> ExchangeAsync(
        IdentityClientOptions client,
        string subject,
        CancellationToken cancellationToken
    )
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(settings.BackchannelTimeout);
        try
        {
            using var backchannel = clients.CreateClient(
                TokenExchangeHttpClientExtensions.BackchannelClientName
            );
            var (issuer, endpoint) = await metadata
                .GetAsync(backchannel, client.Authority, timeout.Token)
                .ConfigureAwait(false);
            using var exchangeRequest = TokenExchange.CreateRequest(
                client,
                endpoint,
                subject,
                settings.Audience,
                settings.Scope,
                issuer
            );
            using var exchangeResponse = await backchannel
                .SendAsync(exchangeRequest, timeout.Token)
                .ConfigureAwait(false);
            var payload = await TokenRequests
                .ReadAsync(exchangeResponse, timeout.Token)
                .ConfigureAwait(false);
            if (
                !payload.TryGetProperty("access_token", out var accessToken)
                || string.IsNullOrWhiteSpace(accessToken.GetString())
                || !payload.TryGetProperty("token_type", out var tokenType)
                || !string.Equals(
                    tokenType.GetString(),
                    "Bearer",
                    StringComparison.OrdinalIgnoreCase
                )
                || !payload.TryGetProperty("issued_token_type", out var issuedType)
                || !string.Equals(
                    issuedType.GetString(),
                    TokenExchange.AccessTokenType,
                    StringComparison.Ordinal
                )
            )
                throw new HttpRequestException(
                    "The provider returned an invalid token-exchange response."
                );
            return accessToken.GetString()!;
        }
        catch (OperationCanceledException error)
            when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"The token-exchange backchannel did not answer within {settings.BackchannelTimeout.TotalSeconds:0.#} s.",
                error
            );
        }
    }
}
