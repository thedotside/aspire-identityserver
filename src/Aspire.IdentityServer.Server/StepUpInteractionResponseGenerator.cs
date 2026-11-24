using Duende.IdentityServer.Configuration;
using Duende.IdentityServer.ResponseHandling;
using Duende.IdentityServer.Services;
using Duende.IdentityServer.Validation;

namespace Aspire.IdentityServer.Server;

/// <summary>
/// Duende's interaction decisions, plus the simulated second factor: once the user is signed in,
/// a declared level requested with acr_values that the session lacks sends the user to the code
/// page. With prompt=none, Duende turns that interaction into interaction_required.
/// </summary>
internal sealed class StepUpInteractionResponseGenerator(
    IdentityServerOptions options,
    TimeProvider timeProvider,
    ILogger<AuthorizeInteractionResponseGenerator> logger,
    IConsentService consent,
    IProfileService profile,
    ServerConfiguration config
) : AuthorizeInteractionResponseGenerator(options, timeProvider, logger, consent, profile)
{
    protected override async Task<InteractionResponse> ProcessLoginAsync(
        ValidatedAuthorizeRequest request,
        CancellationToken ct
    )
    {
        var result = await base.ProcessLoginAsync(request, ct);
        if (result.IsLogin || result.IsError || config.SimulatedMfa is not { } mfa)
            return result;
        // Duende reserves the idp: and tenant: prefixes; the other values are levels.
        var requested = (request.AuthenticationContextReferenceClasses ?? [])
            .Where(value =>
                !value.StartsWith("idp:", StringComparison.Ordinal)
                && !value.StartsWith("tenant:", StringComparison.Ordinal)
            )
            .ToList();
        var undeclared = requested
            .Where(value => !mfa.AcrValues.Contains(value, StringComparer.Ordinal))
            .ToList();
        // Like a provider that does not know them: ignored, but visible in the dashboard logs.
        if (undeclared.Count > 0)
            Logger.LogWarning(
                "Ignoring acr values {AcrValues} that identity provider '{Name}' does not declare with WithSimulatedMfa.",
                string.Join(' ', undeclared),
                config.Name
            );
        var level = SimulatedMfa.RequestedLevel(requested, mfa.AcrValues);
        if (
            level is not null
            && !string.Equals(
                request.Subject?.FindFirst("acr")?.Value,
                level,
                StringComparison.Ordinal
            )
        )
            result.RedirectUrl = SimulatedMfa.PagePath;
        return result;
    }
}
