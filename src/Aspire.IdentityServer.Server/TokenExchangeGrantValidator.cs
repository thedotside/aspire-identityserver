using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Duende.IdentityServer;
using Duende.IdentityServer.Models;
using Duende.IdentityServer.Test;
using Duende.IdentityServer.Validation;
using Microsoft.IdentityModel.JsonWebTokens;

namespace Aspire.IdentityServer.Server;

internal sealed class TokenExchangeGrantValidator(
    ITokenValidator validator,
    ServerConfiguration configuration,
    TestUserStore users,
    ForeignIssuerKeys foreignKeys,
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
        // This implementation deliberately supports one access-token subject and target.
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
        var client = configuration.Clients.Single(c =>
            string.Equals(c.ClientId, context.Request.Client.ClientId, StringComparison.Ordinal)
        );
        // The unverified issuer only chooses which keys validate the token: a token of this
        // provider goes to Duende's validation, any other issuer must be trusted by a policy.
        var issuer = UnverifiedIssuer(subjects[0]);
        var foreign = !string.Equals(issuer, configuration.Issuer, StringComparison.Ordinal);
        var source = foreign
            ? await ForeignAsync(subjects[0], issuer, client, ct)
            : await LocalAsync(subjects[0], ct);
        if (source is null)
            return;
        // A local subject must be an active test user; a foreign one has no local account.
        var user = foreign ? null : users.FindBySubjectId(source.Subject);
        if (!foreign && user?.IsActive != true)
            return;

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
        // A policy trusts one source client of one provider: a client of the same name on
        // another provider never matches.
        var policy = client.TokenExchanges.SingleOrDefault(p =>
            string.Equals(p.SubjectClientId, source.ClientId, StringComparison.Ordinal)
            && string.Equals(p.Audience, audiences[0], StringComparison.Ordinal)
            && string.Equals(p.SubjectIssuer, foreign ? issuer : null, StringComparison.Ordinal)
        );
        if (
            !string.Equals(client.GrantType, "client_credentials", StringComparison.Ordinal)
            || policy is null
            || policy.UserClaims.Any(c => !TokenExchangeClaims.IsUserClaim(c))
            // A local source is an authorization-code client of this provider.
            || (
                !foreign
                && !configuration.Clients.Any(c =>
                    string.Equals(c.ClientId, source.ClientId, StringComparison.Ordinal)
                    && string.Equals(c.GrantType, "authorization_code", StringComparison.Ordinal)
                )
            )
            // A local narrowing exchange keeps the audience the user's token already targets.
            || (
                !foreign
                && !policy.ScopesFromMachineClient
                && !source.Audiences.Contains(policy.Audience, StringComparer.Ordinal)
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
        context.Result = new GrantValidationResult(TokenRequestErrors.InvalidScope);
        var requestedScopes = context.Request.RequestedScopes?.ToArray() ?? [];
        // Duende has already limited the request to scopes allowed to this machine client, all of
        // them when scope is omitted. Choosing for the user is then explicit. Without the
        // machine-client rule, each scope must also come from the user's token: across
        // providers, a scope name keeps its meaning and nothing is granted beyond it.
        if (
            requestedScopes.Length == 0
            || (policy.ScopesFromMachineClient && string.IsNullOrWhiteSpace(raw["scope"]))
            || requestedScopes.Any(s =>
                (!policy.ScopesFromMachineClient && !source.Scopes.Contains(s))
                || !api.Scopes.Contains(s, StringComparer.Ordinal)
            )
        )
            return;

        // The exchange cannot extend the source token's remaining validity.
        var remaining = source.Expiration - timeProvider.GetUtcNow().ToUnixTimeSeconds() - 1;
        context.Result = new GrantValidationResult(TokenRequestErrors.InvalidGrant);
        if (
            remaining <= 0
            || source.AuthenticationTime > timeProvider.GetUtcNow().ToUnixTimeSeconds()
        )
            return;
        context.Request.AccessTokenLifetime = (int)
            Math.Min(context.Request.AccessTokenLifetime, remaining);
        var allowed = policy.UserClaims.ToHashSet(StringComparer.Ordinal);
        List<Claim> transferred;
        string subject;
        string identityProvider;
        if (foreign)
        {
            // No local account: the selected claims come from the validated source token, and
            // the local subject is qualified by the source provider, never matched by name.
            transferred = source.Claims.Where(c => allowed.Contains(c.Type)).ToList();
            subject = $"{policy.SubjectProvider}|{source.Subject}";
            identityProvider = policy.SubjectProvider!;
        }
        else
        {
            // The local test-user profile is authoritative for optional user data. The subject
            // itself is always obtained from the cryptographically validated source token.
            transferred = user!.Claims.Where(c => allowed.Contains(c.Type)).ToList();
            subject = source.Subject;
            identityProvider = source.IdentityProvider;
        }
        transferred.Add(
            new Claim("act", JsonSerializer.Serialize(new { sub = client.ClientId }), "JSON")
        );
        // Like amr, the level reached at sign-in follows the user: Duende issues the acr of the
        // subject. It is a protocol claim, outside the user claims that the policy selects.
        if (source.AuthenticationContextClass is { } level)
            transferred.Add(new Claim("acr", level));
        var principal = new IdentityServerUser(subject)
        {
            AuthenticationTime = DateTimeOffset
                .FromUnixTimeSeconds(source.AuthenticationTime)
                .UtcDateTime,
            AuthenticationMethods = source.AuthenticationMethods,
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

    /// <summary>A token of this provider: Duende validates its type, issuer, signature and lifetime.</summary>
    private async Task<Source?> LocalAsync(string token, CancellationToken ct)
    {
        var validated = await validator.ValidateAccessTokenAsync(token, null, ct);
        if (validated.IsError || validated.Jwt is null || validated.Claims is null)
            return null;
        return Source.From(validated.Claims.ToList());
    }

    /// <summary>
    /// A token of another provider, accepted only when a policy of this machine client trusts
    /// its exact issuer; that provider's own keys validate it.
    /// </summary>
    private async Task<Source?> ForeignAsync(
        string token,
        string? issuer,
        ClientConfiguration client,
        CancellationToken ct
    )
    {
        if (
            issuer is null
            || !client.TokenExchanges.Any(p =>
                string.Equals(p.SubjectIssuer, issuer, StringComparison.Ordinal)
            )
        )
            return null;
        var validated = await foreignKeys.ValidateAsync(token, issuer, ct);
        return validated is null ? null : Source.From(validated.Claims.ToList());
    }

    private static string? UnverifiedIssuer(string token)
    {
        try
        {
            return new JsonWebToken(token).Issuer;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>The facts of a validated source token that an exchange relies on.</summary>
    private sealed record Source(
        string Subject,
        string ClientId,
        long Expiration,
        long AuthenticationTime,
        string IdentityProvider,
        List<string> AuthenticationMethods,
        HashSet<string> Scopes,
        List<string> Audiences,
        List<Claim> Claims,
        string? AuthenticationContextClass
    )
    {
        internal static Source? From(List<Claim> claims)
        {
            var subject = SingleClaim(claims, "sub");
            var clientId = SingleClaim(claims, "client_id");
            var identityProvider = SingleClaim(claims, "idp");
            // How the user authenticated stays a fact of the validated source token.
            var methods = claims
                .Where(c => string.Equals(c.Type, "amr", StringComparison.Ordinal))
                .Select(c => c.Value)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            // The level reached, such as a simulated MFA, is optional but never ambiguous.
            var levels = claims
                .Where(c => string.Equals(c.Type, "acr", StringComparison.Ordinal))
                .Select(c => c.Value)
                .ToList();
            // A sender-constrained subject (DPoP or mTLS cnf) must never become a bearer token,
            // and an exchanged token is not exchanged again.
            if (
                string.IsNullOrWhiteSpace(subject)
                || string.IsNullOrWhiteSpace(clientId)
                || string.IsNullOrWhiteSpace(identityProvider)
                || methods.Count == 0
                || methods.Any(string.IsNullOrWhiteSpace)
                || levels.Count > 1
                || levels.Any(string.IsNullOrWhiteSpace)
                || claims.Any(c =>
                    string.Equals(c.Type, "act", StringComparison.Ordinal)
                    || string.Equals(c.Type, "cnf", StringComparison.Ordinal)
                )
                || !long.TryParse(
                    SingleClaim(claims, "exp"),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var expiration
                )
                || !long.TryParse(
                    SingleClaim(claims, "auth_time"),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var authenticationTime
                )
            )
                return null;
            return new Source(
                subject,
                clientId,
                expiration,
                authenticationTime,
                identityProvider,
                methods,
                claims
                    .Where(c => string.Equals(c.Type, "scope", StringComparison.Ordinal))
                    .SelectMany(c => c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    .ToHashSet(StringComparer.Ordinal),
                claims
                    .Where(c => string.Equals(c.Type, "aud", StringComparison.Ordinal))
                    .Select(c => c.Value)
                    .ToList(),
                claims,
                levels.SingleOrDefault()
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
}
