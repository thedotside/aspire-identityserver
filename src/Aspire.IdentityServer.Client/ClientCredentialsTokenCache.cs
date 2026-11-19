using System.Collections.Concurrent;

namespace Aspire.IdentityServer.Client;

/// <summary>
/// Client-credentials tokens shared by every handler instance, per named client and scope. One
/// request at a time acquires a missing token; a token is renewed shortly before it expires.
/// </summary>
internal sealed class ClientCredentialsTokenCache(TimeProvider time)
{
    private readonly ConcurrentDictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> gates = new(
        StringComparer.Ordinal
    );

    internal async Task<string> GetAsync(
        string key,
        TimeSpan margin,
        Func<CancellationToken, Task<(string Token, TimeSpan? Lifetime)>> acquire,
        CancellationToken cancellationToken
    )
    {
        if (Current(key) is { } cached)
            return cached;
        var gate = gates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Current(key) is { } acquiredMeanwhile)
                return acquiredMeanwhile;
            var (token, lifetime) = await acquire(cancellationToken).ConfigureAwait(false);
            // A token without a known lifetime, or too short for the margin, is not reused.
            if (lifetime is { } value && value > margin)
                entries[key] = new Entry(token, time.GetUtcNow() + value - margin);
            return token;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Forgets a token that a resource rejected, unless a newer one replaced it.</summary>
    internal void Invalidate(string key, string token)
    {
        if (
            entries.TryGetValue(key, out var entry)
            && string.Equals(entry.Token, token, StringComparison.Ordinal)
        )
            entries.TryRemove(KeyValuePair.Create(key, entry));
    }

    private string? Current(string key) =>
        entries.TryGetValue(key, out var entry) && entry.RenewAt > time.GetUtcNow()
            ? entry.Token
            : null;

    private sealed record Entry(string Token, DateTimeOffset RenewAt);
}
