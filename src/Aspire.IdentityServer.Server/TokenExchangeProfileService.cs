using Duende.IdentityServer.Models;
using Duende.IdentityServer.Test;
using Duende.IdentityServer.Validation;

namespace Aspire.IdentityServer.Server;

internal sealed class TokenExchangeProfileService(
    TestUserStore users,
    ILogger<TestUserProfileService> logger
) : TestUserProfileService(users, logger)
{
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
        return base.GetProfileDataAsync(context, ct);
    }
}
