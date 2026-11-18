using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Aspire.IdentityServer.Server;

/// <summary>
/// Validates the access tokens of another provider that a token-exchange policy trusts. Keys come
/// only from that provider's own discovery document; an unknown key triggers at most one refresh
/// per interval, after which the token is refused.
/// </summary>
internal sealed class ForeignIssuerKeys(IHttpClientFactory clients, TimeProvider time)
{
    internal const string ClientName = "TrustedIssuerMetadata";
    internal static readonly TimeSpan MinimumRefreshInterval = TimeSpan.FromSeconds(30);
    private readonly ConcurrentDictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim gate = new(1, 1);

    /// <summary>The validated token, or null when its issuer, signature, type or lifetime is wrong.</summary>
    internal async Task<JsonWebToken?> ValidateAsync(
        string token,
        string issuer,
        CancellationToken cancellationToken
    )
    {
        var result = await ValidateWithAsync(
                token,
                issuer,
                await KeysAsync(issuer, refresh: false, cancellationToken).ConfigureAwait(false)
            )
            .ConfigureAwait(false);
        if (!result.IsValid && result.Exception is SecurityTokenSignatureKeyNotFoundException)
            result = await ValidateWithAsync(
                    token,
                    issuer,
                    await KeysAsync(issuer, refresh: true, cancellationToken).ConfigureAwait(false)
                )
                .ConfigureAwait(false);
        return result.IsValid ? result.SecurityToken as JsonWebToken : null;
    }

    private static Task<TokenValidationResult> ValidateWithAsync(
        string token,
        string issuer,
        IReadOnlyList<SecurityKey> keys
    ) =>
        new JsonWebTokenHandler().ValidateTokenAsync(
            token,
            new TokenValidationParameters
            {
                ValidIssuer = issuer,
                // The source audience belongs to the other provider's APIs: the trust is bound to
                // the declared source client and to the scopes of its token instead.
                ValidateAudience = false,
                IssuerSigningKeys = keys,
                ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                ValidTypes = ["at+jwt"],
                RequireSignedTokens = true,
                RequireExpirationTime = true,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromSeconds(5),
            }
        );

    private async Task<IReadOnlyList<SecurityKey>> KeysAsync(
        string issuer,
        bool refresh,
        CancellationToken cancellationToken
    )
    {
        if (Current(issuer, refresh) is { } cached)
            return cached;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Current(issuer, refresh) is { } fetchedMeanwhile)
                return fetchedMeanwhile;
            using var http = clients.CreateClient(ClientName);
            using var discovery = JsonDocument.Parse(
                await http.GetStringAsync(
                        issuer.TrimEnd('/') + "/.well-known/openid-configuration",
                        cancellationToken
                    )
                    .ConfigureAwait(false)
            );
            var root = discovery.RootElement;
            var jwksUri = root.GetProperty("jwks_uri").GetString();
            // The document must describe this issuer, and its keys must come from the same origin.
            if (
                !string.Equals(
                    root.GetProperty("issuer").GetString(),
                    issuer,
                    StringComparison.Ordinal
                )
                || !Uri.TryCreate(jwksUri, UriKind.Absolute, out var keysAddress)
                || !string.Equals(
                    keysAddress.GetLeftPart(UriPartial.Authority),
                    new Uri(issuer).GetLeftPart(UriPartial.Authority),
                    StringComparison.OrdinalIgnoreCase
                )
            )
                throw new SecurityTokenException(
                    "The trusted provider's metadata do not match it."
                );
            var keys = new JsonWebKeySet(
                await http.GetStringAsync(keysAddress, cancellationToken).ConfigureAwait(false)
            )
                .GetSigningKeys()
                .ToList();
            entries[issuer] = new Entry(keys, time.GetUtcNow());
            return keys;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Cached keys, unless a refresh is asked for and the last fetch is old enough.</summary>
    private IReadOnlyList<SecurityKey>? Current(string issuer, bool refresh) =>
        entries.TryGetValue(issuer, out var entry)
        && (!refresh || time.GetUtcNow() - entry.Fetched < MinimumRefreshInterval)
            ? entry.Keys
            : null;

    private sealed record Entry(IReadOnlyList<SecurityKey> Keys, DateTimeOffset Fetched);
}
