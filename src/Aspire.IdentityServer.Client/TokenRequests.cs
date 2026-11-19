using System.Net.Http.Json;
using System.Text.Json;

namespace Aspire.IdentityServer.Client;

/// <summary>Token-endpoint requests, answers and outgoing-request checks shared by the grants.</summary>
internal static class TokenRequests
{
    /// <summary>
    /// Adds fresh client authentication to a grant's form fields and creates the request. A
    /// private_key_jwt request carries the means to re-sign each retried attempt.
    /// </summary>
    internal static HttpRequestMessage Create(
        IdentityClientOptions client,
        string tokenEndpoint,
        Dictionary<string, string> fields,
        string? issuer,
        string grant
    )
    {
        if (
            !Uri.TryCreate(tokenEndpoint, UriKind.Absolute, out var endpoint)
            || !string.Equals(endpoint.Scheme, "https", StringComparison.Ordinal)
            || !string.IsNullOrEmpty(endpoint.UserInfo)
        )
            throw new ArgumentException(
                "Use a discovered HTTPS token endpoint.",
                nameof(tokenEndpoint)
            );
        fields["client_id"] = client.ClientId;
        Func<HttpContent>? fresh = null;
        if (string.Equals(client.AuthenticationMethod, "private_key_jwt", StringComparison.Ordinal))
        {
            var assertionIssuer = issuer ?? client.Authority;
            fields["client_assertion_type"] = PrivateKeyJwt.AssertionType;
            fields["client_assertion"] = PrivateKeyJwt.CreateAssertion(client, assertionIssuer);
            fresh = () =>
                new FormUrlEncodedContent(
                    new Dictionary<string, string>(fields, StringComparer.Ordinal)
                    {
                        ["client_assertion"] = PrivateKeyJwt.CreateAssertion(
                            client,
                            assertionIssuer
                        ),
                    }
                );
        }
        else if (
            string.Equals(
                client.AuthenticationMethod,
                "client_secret_post",
                StringComparison.Ordinal
            )
        )
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(client.ClientSecret, nameof(client));
            fields["client_secret"] = client.ClientSecret;
        }
        else
            throw new ArgumentException($"{grant} requires a confidential client.", nameof(client));
        var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new FormUrlEncodedContent(fields),
        };
        // A retried attempt must not replay the assertion; the token backchannel re-signs it.
        if (fresh is not null)
            request.Options.Set(TokenExchange.FreshContentKey, fresh);
        return request;
    }

    /// <summary>Reads a successful answer, or throws the OAuth error that the endpoint returned.</summary>
    internal static async Task<JsonElement> ReadAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken
    )
    {
        if (response.IsSuccessStatusCode)
            return await response
                .Content.ReadFromJsonAsync<JsonElement>(cancellationToken)
                .ConfigureAwait(false);
        string? error = null;
        string? description = null;
        try
        {
            var payload = await response
                .Content.ReadFromJsonAsync<JsonElement>(cancellationToken)
                .ConfigureAwait(false);
            if (payload.ValueKind == JsonValueKind.Object)
            {
                error = Text(payload, "error");
                description = Text(payload, "error_description");
            }
        }
        catch (JsonException)
        {
            // An answer without a JSON body still reports its status.
        }
        throw new TokenEndpointException(response.StatusCode, error, description);
    }

    /// <summary>
    /// Refuses to add a token to a request that already carries one, that is not HTTPS, or whose
    /// origin is not allowed.
    /// </summary>
    internal static void EnsureSendable(
        HttpRequestMessage request,
        ICollection<Uri> allowedOrigins,
        string handler
    )
    {
        if (request.Headers.Authorization is not null)
            throw new InvalidOperationException(
                $"The {handler} handler requires an outgoing request without an Authorization header."
            );
        if (
            request.RequestUri is not { IsAbsoluteUri: true } resource
            || !string.Equals(resource.Scheme, "https", StringComparison.Ordinal)
            || !string.IsNullOrEmpty(resource.UserInfo)
        )
            throw new InvalidOperationException(
                $"The {handler} handler requires an HTTPS resource address."
            );
        if (
            allowedOrigins.Count > 0
            && !allowedOrigins.Any(o =>
                Uri.Compare(
                    o,
                    resource,
                    UriComponents.SchemeAndServer,
                    UriFormat.Unescaped,
                    StringComparison.OrdinalIgnoreCase
                ) == 0
            )
        )
            throw new InvalidOperationException(
                $"The resource address is not one of the allowed origins of this {handler} handler."
            );
    }

    /// <summary>Refuses an authority that is not an absolute HTTPS address without extras.</summary>
    internal static void EnsureAuthority(IdentityClientOptions client)
    {
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
    }

    private static string? Text(JsonElement payload, string name) =>
        payload.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
