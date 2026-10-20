using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Aspire.IdentityServer.Client;

internal sealed class TokenExchangeHandler(
    IHttpClientFactory clients,
    IOptionsMonitor<IdentityClientOptions> clientOptions,
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
        if (request.Headers.Authorization is not null)
            throw new InvalidOperationException(
                "The exchange handler requires an outgoing request without an Authorization header."
            );
        if (
            request.RequestUri is not { IsAbsoluteUri: true } resource
            || !string.Equals(resource.Scheme, "https", StringComparison.Ordinal)
            || !string.IsNullOrEmpty(resource.UserInfo)
        )
            throw new InvalidOperationException(
                "Token exchange requires an HTTPS resource address."
            );
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
        if (
            !Uri.TryCreate(client.Authority, UriKind.Absolute, out var authority)
            || !string.Equals(authority.Scheme, "https", StringComparison.Ordinal)
            || !string.IsNullOrEmpty(authority.UserInfo)
            || !string.IsNullOrEmpty(authority.Query)
            || !string.IsNullOrEmpty(authority.Fragment)
        )
            throw new InvalidOperationException(
                "Configure an absolute HTTPS identity-provider authority."
            );
        using var backchannel = clients.CreateClient(
            TokenExchangeHttpClientExtensions.BackchannelClientName
        );
        var metadata = await backchannel
            .GetFromJsonAsync<JsonElement>(
                client.Authority.TrimEnd('/') + "/.well-known/openid-configuration",
                cancellationToken
            )
            .ConfigureAwait(false);
        if (
            !metadata.TryGetProperty("issuer", out var issuer)
            || (
                !string.Equals(issuer.GetString(), client.Authority, StringComparison.Ordinal)
                && !string.Equals(
                    issuer.GetString(),
                    client.Authority.TrimEnd('/'),
                    StringComparison.Ordinal
                )
            )
            || !metadata.TryGetProperty("token_endpoint", out var endpoint)
            || string.IsNullOrWhiteSpace(endpoint.GetString())
        )
            throw new HttpRequestException(
                "The discovery response does not match the configured identity provider."
            );
        using var exchangeRequest = TokenExchange.CreateRequest(
            client,
            endpoint.GetString()!,
            subject,
            settings.Audience,
            settings.Scope
        );
        using var exchangeResponse = await backchannel
            .SendAsync(exchangeRequest, cancellationToken)
            .ConfigureAwait(false);
        exchangeResponse.EnsureSuccessStatusCode();
        var payload = await exchangeResponse
            .Content.ReadFromJsonAsync<JsonElement>(cancellationToken)
            .ConfigureAwait(false);
        if (
            !payload.TryGetProperty("access_token", out var accessToken)
            || string.IsNullOrWhiteSpace(accessToken.GetString())
            || !payload.TryGetProperty("token_type", out var tokenType)
            || !string.Equals(tokenType.GetString(), "Bearer", StringComparison.OrdinalIgnoreCase)
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
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            accessToken.GetString()
        );
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
}
