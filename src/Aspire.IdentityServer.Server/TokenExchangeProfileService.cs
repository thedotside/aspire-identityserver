using Duende.IdentityServer;
using Duende.IdentityServer.Models;
using Duende.IdentityServer.Test;
using Duende.IdentityServer.Validation;

namespace Aspire.IdentityServer.Server;

internal sealed class TokenExchangeProfileService(
    TestUserStore users,
    ServerConfiguration configuration,
    ILogger<TestUserProfileService> logger
) : TestUserProfileService(users, logger)
{
    /// <summary>
    /// A subject of a trusted provider has no local account: the exchange that just validated
    /// its token is what makes it active, and only for that grant.
    /// </summary>
    public override Task IsActiveAsync(IsActiveContext context, CancellationToken ct)
    {
        if (
            string.Equals(
                context.Caller,
                IdentityServerConstants.ProfileIsActiveCallers.ExtensionGrantValidation,
                StringComparison.Ordinal
            )
            && context.Subject.FindFirst("idp")?.Value is { } provider
            && configuration
                .Clients.SelectMany(c => c.TokenExchanges)
                .Any(p =>
                    p.SubjectProvider is not null
                    && string.Equals(p.SubjectProvider, provider, StringComparison.Ordinal)
                )
            && context
                .Subject.FindFirst("sub")
                ?.Value?.StartsWith(provider + "|", StringComparison.Ordinal) == true
        )
        {
            context.IsActive = true;
            return Task.CompletedTask;
        }
        return base.IsActiveAsync(context, ct);
    }

    public override Task GetProfileDataAsync(
        ProfileDataRequestContext context,
        CancellationToken ct
    )
    {
        if (
            context.ProtocolRequest is ValidatedTokenRequest
            {
                GrantType: TokenExchangeGrantValidator.GrantTypeValue
            }
        )
        {
            // Only this grant validator's allow-listed user claims and actor are issued.
            // Do not reload the complete user profile through the normal scope pipeline.
            context.IssuedClaims = context
                .Subject.Claims.Where(c =>
                    TokenExchangeClaims.IsUserClaim(c.Type)
                    || string.Equals(c.Type, "act", StringComparison.Ordinal)
                )
                .ToList();
            return Task.CompletedTask;
        }
        if (
            context.ProtocolRequest is ValidatedTokenRequest { GrantType: "authorization_code" }
            && string.Equals(
                context.Caller,
                IdentityServerConstants.ProfileDataCallers.ClaimsProviderAccessToken,
                StringComparison.Ordinal
            )
            && context.RequestedResources is { } granted
        )
        {
            // The BFF's granted identity scopes select its user claims for the login access
            // token too. Keep Duende's profile filtering and never copy the whole user profile.
            context.RequestedClaimTypes = context
                .RequestedClaimTypes.Concat(
                    granted.Resources.IdentityResources.SelectMany(r => r.UserClaims)
                )
                .Where(TokenExchangeClaims.IsUserClaim)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }
        return base.GetProfileDataAsync(context, ct);
    }
}
