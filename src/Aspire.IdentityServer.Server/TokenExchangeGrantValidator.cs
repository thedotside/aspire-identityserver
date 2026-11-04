using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Duende.IdentityServer;
using Duende.IdentityServer.Models;
using Duende.IdentityServer.Test;
using Duende.IdentityServer.Validation;

namespace Aspire.IdentityServer.Server;

internal sealed class TokenExchangeGrantValidator(
    ITokenValidator validator,
    ServerConfiguration configuration,
    TestUserStore users,
    TimeProvider timeProvider
) : IExtensionGrantValidator
{
    internal const string GrantTypeValue = "urn:ietf:params:oauth:grant-type:token-exchange";
    internal const string AccessTokenType = "urn:ietf:params:oauth:token-type:access_token";
    public string GrantType => GrantTypeValue;

    public async Task ValidateAsync(ExtensionGrantValidationContext context, CancellationToken ct)
    {
        context.Result = new GrantValidationResult(TokenRequestErrors.InvalidRequest);
        var raw = context.Request.Raw;
        // This first implementation deliberately supports one access-token subject and target.
        if (
            raw.GetValues("subject_token") is not { Length: 1 } subjects
            || string.IsNullOrWhiteSpace(subjects[0])
            || subjects[0].Length > 16384
            || raw.GetValues("subject_token_type") is not { Length: 1 } subjectTypes
            || !string.Equals(subjectTypes[0], AccessTokenType, StringComparison.Ordinal)
            || raw.GetValues("audience") is not { Length: 1 } audiences
            || string.IsNullOrWhiteSpace(audiences[0])
            || raw["actor_token"] is not null
            || raw["actor_token_type"] is not null
            || raw["resource"] is not null
            || (
                raw.GetValues("requested_token_type") is { } requestedTypes
                && (
                    requestedTypes.Length != 1
                    || !string.Equals(requestedTypes[0], AccessTokenType, StringComparison.Ordinal)
                )
            )
        )
            return;

        context.Result = new GrantValidationResult(TokenRequestErrors.InvalidGrant);
        // Duende validates the actual access-token type, issuer, signature and lifetime.
        // No foreign issuer or arbitrary JWT parsing can establish trust here.
        var validated = await validator.ValidateAccessTokenAsync(subjects[0], null, ct);
        if (validated.IsError || validated.Jwt is null || validated.Claims is null)
            return;
        var claims = validated.Claims.ToList();
        var subject = SingleClaim(claims, "sub");
        var sourceClient = SingleClaim(claims, "client_id");
        var expires = SingleClaim(claims, "exp");
        var authTime = SingleClaim(claims, "auth_time");
        var identityProvider = SingleClaim(claims, "idp");
        // How the user authenticated stays a fact of the validated source token.
        var authenticationMethods = claims
            .Where(c => string.Equals(c.Type, "amr", StringComparison.Ordinal))
            .Select(c => c.Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        // A sender-constrained subject (DPoP or mTLS cnf) must never become a bearer token.
        if (
            string.IsNullOrWhiteSpace(subject)
            || string.IsNullOrWhiteSpace(sourceClient)
            || string.IsNullOrWhiteSpace(identityProvider)
            || authenticationMethods.Count == 0
            || authenticationMethods.Any(string.IsNullOrWhiteSpace)
            || claims.Any(c =>
                string.Equals(c.Type, "act", StringComparison.Ordinal)
                || string.Equals(c.Type, "cnf", StringComparison.Ordinal)
            )
            || !long.TryParse(
                expires,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var expiration
            )
            || !long.TryParse(
                authTime,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var authenticationTime
            )
        )
            return;
        var user = users.FindBySubjectId(subject);
        if (user?.IsActive != true)
            return;
        var client = configuration.Clients.Single(c =>
            string.Equals(c.ClientId, context.Request.Client.ClientId, StringComparison.Ordinal)
        );
        // RFC 8693 section 2.2.2: an audience that no policy of this client allows is a target
        // error. A known audience with an unexpected source token stays invalid_grant.
        if (
            !client.TokenExchanges.Any(p =>
                string.Equals(p.Audience, audiences[0], StringComparison.Ordinal)
            )
        )
        {
            context.Result = new GrantValidationResult(TokenRequestErrors.InvalidTarget);
            return;
        }
        var policy = client.TokenExchanges.SingleOrDefault(p =>
            string.Equals(p.SubjectClientId, sourceClient, StringComparison.Ordinal)
            && string.Equals(p.Audience, audiences[0], StringComparison.Ordinal)
        );
        if (
            !string.Equals(client.GrantType, "client_credentials", StringComparison.Ordinal)
            || policy is null
            || policy.UserClaims.Any(c => !TokenExchangeClaims.IsUserClaim(c))
            || !configuration.Clients.Any(c =>
                string.Equals(c.ClientId, sourceClient, StringComparison.Ordinal)
                && string.Equals(c.GrantType, "authorization_code", StringComparison.Ordinal)
            )
            // A narrowing exchange keeps the audience the user's token already targets.
            || (
                !policy.ScopesFromMachineClient
                && !claims.Any(c =>
                    string.Equals(c.Type, "aud", StringComparison.Ordinal)
                    && string.Equals(c.Value, policy.Audience, StringComparison.Ordinal)
                )
            )
        )
            return;

        context.Result = new GrantValidationResult(TokenRequestErrors.InvalidTarget);
        var api = configuration.Apis.Single(a =>
            string.Equals(a.Audience, policy.Audience, StringComparison.Ordinal)
        );
        if (
            context.Request.ValidatedResources.Resources.ApiResources.Any(a =>
                !string.Equals(a.Name, policy.Audience, StringComparison.Ordinal)
            )
        )
            return;
        var sourceScopes = claims
            .Where(c => string.Equals(c.Type, "scope", StringComparison.Ordinal))
            .SelectMany(c => c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .ToHashSet(StringComparer.Ordinal);
        context.Result = new GrantValidationResult(TokenRequestErrors.InvalidScope);
        var requestedScopes = context.Request.RequestedScopes?.ToArray() ?? [];
        // Duende has already limited the request to scopes allowed to this machine client, all of
        // them when scope is omitted. Choosing for the user is then explicit. Without the
        // machine-client rule, each scope must also come from the user's token.
        if (
            requestedScopes.Length == 0
            || (policy.ScopesFromMachineClient && string.IsNullOrWhiteSpace(raw["scope"]))
            || requestedScopes.Any(s =>
                (!policy.ScopesFromMachineClient && !sourceScopes.Contains(s))
                || !api.Scopes.Contains(s, StringComparer.Ordinal)
            )
        )
            return;

        // The exchange cannot extend the source token's remaining validity.
        var remaining = expiration - timeProvider.GetUtcNow().ToUnixTimeSeconds() - 1;
        context.Result = new GrantValidationResult(TokenRequestErrors.InvalidGrant);
        if (remaining <= 0 || authenticationTime > timeProvider.GetUtcNow().ToUnixTimeSeconds())
            return;
        context.Request.AccessTokenLifetime = (int)
            Math.Min(context.Request.AccessTokenLifetime, remaining);
        var allowed = policy.UserClaims.ToHashSet(StringComparer.Ordinal);
        // The local test-user profile is authoritative for optional user data. The subject
        // itself is always obtained from the cryptographically validated source token.
        var transferred = user.Claims.Where(c => allowed.Contains(c.Type)).ToList();
        transferred.Add(
            new Claim("act", JsonSerializer.Serialize(new { sub = client.ClientId }), "JSON")
        );
        var principal = new IdentityServerUser(subject)
        {
            AuthenticationTime = DateTimeOffset.FromUnixTimeSeconds(authenticationTime).UtcDateTime,
            AuthenticationMethods = authenticationMethods,
            IdentityProvider = identityProvider,
            AdditionalClaims = transferred,
        }.CreatePrincipal();
        context.Result = new GrantValidationResult(
            principal,
            new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["issued_token_type"] = AccessTokenType,
            }
        );
    }

    private static string? SingleClaim(IEnumerable<Claim> claims, string type)
    {
        var values = claims
            .Where(c => string.Equals(c.Type, type, StringComparison.Ordinal))
            .ToArray();
        return values.Length == 1 ? values[0].Value : null;
    }
}
