using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Duende.IdentityServer.Models;
using Duende.IdentityServer.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Aspire.IdentityServer.Server.Pages.Account;

/// <summary>
/// The simulated second factor: the code declared in the AppHost raises the provider session to
/// the level that the authorization request asked for with acr_values.
/// </summary>
public sealed class MfaModel(IIdentityServerInteractionService interaction) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string ReturnUrl { get; set; } = "/";

    [BindProperty]
    public string Code { get; set; } = "";

    /// <summary>The submit button used: <c>verify</c> or <c>cancel</c>.</summary>
    [BindProperty]
    public string? Button { get; set; }

    /// <summary>The client that asked for the level, as the AppHost registered it.</summary>
    public string? ClientId { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        if (await PendingAsync() is not { } pending)
            return BadRequest();
        // Without a provider session, the authorization request signs the user in first.
        if (User.Identity?.IsAuthenticated != true)
            return Redirect(ReturnUrl);
        ClientId = pending.Request.Client.ClientId;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (await PendingAsync() is not { } pending)
            return BadRequest();
        if (string.Equals(Button, "cancel", StringComparison.Ordinal))
        {
            // The client receives access_denied, as when the user cancels the sign-in.
            await interaction.DenyAuthorizationAsync(
                pending.Request,
                InteractionError.AccessDenied,
                HttpContext.RequestAborted
            );
            return Redirect(ReturnUrl);
        }
        var current = await HttpContext.AuthenticateAsync();
        if (current is not { Succeeded: true, Principal: { } principal })
            return Redirect(ReturnUrl);
        if (!Matches(Code))
        {
            // The field comes back empty, with one message whatever was typed.
            ModelState.Clear();
            Code = "";
            ModelState.AddModelError("", "Invalid code.");
            ClientId = pending.Request.Client.ClientId;
            return Page();
        }
        // The same session, raised to the requested level: its id and authentication time stay.
        var claims = principal
            .Claims.Where(c => !string.Equals(c.Type, "acr", StringComparison.Ordinal))
            .ToList();
        foreach (var method in SimulatedMfa.AuthenticationMethods)
            if (
                !claims.Exists(c =>
                    string.Equals(c.Type, "amr", StringComparison.Ordinal)
                    && string.Equals(c.Value, method, StringComparison.Ordinal)
                )
            )
                claims.Add(new Claim("amr", method));
        claims.Add(new Claim("acr", pending.Level));
        var identity = new ClaimsIdentity(
            claims,
            principal.Identity?.AuthenticationType,
            "name",
            "role"
        );
        await HttpContext.SignInAsync(new ClaimsPrincipal(identity), current.Properties);
        return Redirect(ReturnUrl);
    }

    private SimulatedMfaConfiguration? Mfa =>
        HttpContext.RequestServices.GetRequiredService<ServerConfiguration>().SimulatedMfa;

    /// <summary>The authorization request that waits for a declared level, or <see langword="null" />.</summary>
    private async Task<(AuthorizationRequest Request, string Level)?> PendingAsync()
    {
        if (Mfa is not { } mfa || !interaction.IsValidReturnUrl(ReturnUrl))
            return null;
        var request = await interaction.GetAuthorizationContextAsync(
            ReturnUrl,
            HttpContext.RequestAborted
        );
        if (request is null)
            return null;
        var level = SimulatedMfa.RequestedLevel(request.AcrValues, mfa.AcrValues);
        return level is null ? null : (request, level);
    }

    private bool Matches(string? code) =>
        Mfa is { } mfa
        && code is not null
        && CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(code),
            Encoding.UTF8.GetBytes(mfa.Code)
        );
}
