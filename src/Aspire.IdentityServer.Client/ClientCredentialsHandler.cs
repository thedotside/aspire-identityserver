using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Aspire.IdentityServer.Client;

internal sealed class ClientCredentialsHandler(
    IHttpClientFactory clients,
    IOptionsMonitor<IdentityClientOptions> clientOptions,
    TokenExchangeMetadata metadata,
    ClientCredentialsTokenCache cache,
    string clientName,
    ClientCredentialsHandlerOptions settings
) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        TokenRequests.EnsureSendable(request, settings.AllowedOrigins, "client-credentials");
        var client = clientOptions.Get(clientName);
        TokenRequests.EnsureAuthority(client);
        var scope = settings.Scope ?? client.Scopes;
        var key = string.Concat(clientName, "\n", scope);
        var token = await cache
            .GetAsync(
                key,
                settings.RefreshBeforeExpiration,
                ct => AcquireAsync(client, scope, ct),
                cancellationToken
            )
            .ConfigureAwait(false);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        try
        {
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            // A provider restarted with new signing keys rejects the cached token: the next
            // request acquires a new one.
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                cache.Invalidate(key, token);
            return response;
        }
        finally
        {
            // An outer retry passes through this handler again, with the current token.
            request.Headers.Authorization = null;
        }
    }

    private async Task<(string Token, TimeSpan? Lifetime)> AcquireAsync(
        IdentityClientOptions client,
        string scope,
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
            using var tokenRequest = ClientCredentials.CreateRequest(
                client,
                endpoint,
                scope,
                issuer
            );
            using var tokenResponse = await backchannel
                .SendAsync(tokenRequest, timeout.Token)
                .ConfigureAwait(false);
            var payload = await TokenRequests
                .ReadAsync(tokenResponse, timeout.Token)
                .ConfigureAwait(false);
            if (
                payload.ValueKind != JsonValueKind.Object
                || !payload.TryGetProperty("access_token", out var accessToken)
                || accessToken.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(accessToken.GetString())
                || !payload.TryGetProperty("token_type", out var tokenType)
                || tokenType.ValueKind != JsonValueKind.String
                || !string.Equals(
                    tokenType.GetString(),
                    "Bearer",
                    StringComparison.OrdinalIgnoreCase
                )
            )
                throw new HttpRequestException(
                    "The provider returned an invalid client-credentials response."
                );
            TimeSpan? lifetime =
                payload.TryGetProperty("expires_in", out var expiresIn)
                && expiresIn.ValueKind == JsonValueKind.Number
                && expiresIn.TryGetInt32(out var seconds)
                && seconds > 0
                    ? TimeSpan.FromSeconds(seconds)
                    : null;
            return (accessToken.GetString()!, lifetime);
        }
        catch (OperationCanceledException error)
            when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The token backchannel did not answer within {settings.BackchannelTimeout.TotalSeconds:0.#} s."
                ),
                error
            );
        }
    }
}
