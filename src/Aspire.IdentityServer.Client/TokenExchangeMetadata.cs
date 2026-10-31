using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;

namespace Aspire.IdentityServer.Client;

/// <summary>Discovery results shared by every exchange handler, per authority.</summary>
internal sealed class TokenExchangeMetadata(TimeProvider time)
{
    internal static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);
    private readonly ConcurrentDictionary<string, Entry> entries = new(StringComparer.Ordinal);

    internal async Task<(string Issuer, string TokenEndpoint)> GetAsync(
        HttpClient backchannel,
        string authority,
        CancellationToken cancellationToken
    )
    {
        if (entries.TryGetValue(authority, out var cached) && cached.Expires > time.GetUtcNow())
            return (cached.Issuer, cached.TokenEndpoint);
        var metadata = await backchannel
            .GetFromJsonAsync<JsonElement>(
                authority.TrimEnd('/') + "/.well-known/openid-configuration",
                cancellationToken
            )
            .ConfigureAwait(false);
        if (
            !metadata.TryGetProperty("issuer", out var issuerElement)
            || issuerElement.GetString() is not { } issuer
            || (
                !string.Equals(issuer, authority, StringComparison.Ordinal)
                && !string.Equals(issuer, authority.TrimEnd('/'), StringComparison.Ordinal)
            )
            || !metadata.TryGetProperty("token_endpoint", out var endpointElement)
            || string.IsNullOrWhiteSpace(endpointElement.GetString())
        )
            throw new HttpRequestException(
                "The discovery response does not match the configured identity provider."
            );
        var entry = new Entry(issuer, endpointElement.GetString()!, time.GetUtcNow() + Lifetime);
        entries[authority] = entry;
        return (entry.Issuer, entry.TokenEndpoint);
    }

    private sealed record Entry(string Issuer, string TokenEndpoint, DateTimeOffset Expires);
}
