using Duende.IdentityServer.Validation;

namespace Aspire.IdentityServer.Server;

/// <summary>
/// Refuses <c>prompt=create</c> without the openid scope, before the sign-up page can create an
/// account: OpenID Connect Prompt Create 1.0 counts a sign-up as done only with an ID token.
/// </summary>
internal sealed class CreatePromptValidator : ICustomAuthorizeRequestValidator
{
    public Task ValidateAsync(CustomAuthorizeRequestValidationContext context, CancellationToken ct)
    {
        if (
            context.Result is { ValidatedRequest: { } request } result
            && request.OriginalPromptModes.Contains("create", StringComparer.Ordinal)
            && !request.IsOpenIdRequest
        )
        {
            result.IsError = true;
            result.Error = "invalid_request";
            result.ErrorDescription = "prompt=create requires the openid scope";
        }
        return Task.CompletedTask;
    }
}
