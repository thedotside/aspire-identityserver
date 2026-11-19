using Duende.IdentityServer;
using Duende.IdentityServer.Models;
using Duende.IdentityServer.Services;
using Duende.IdentityServer.Test;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Aspire.IdentityServer.Server.Pages.Account;

public sealed class LoginModel(IIdentityServerInteractionService interaction, TestUserStore users)
    : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string ReturnUrl { get; set; } = "/";

    [BindProperty]
    public string Username { get; set; } = "";

    [BindProperty]
    public string Password { get; set; } = "";

    /// <summary>The submit button used: <c>login</c> or <c>cancel</c>.</summary>
    [BindProperty]
    public string? Button { get; set; }

    /// <summary>The client that asked for this sign-in, as the AppHost registered it.</summary>
    public string? ClientId { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        if (!ValidReturnUrl())
            return BadRequest();
        // Like production providers, login_hint pre-fills the account; the password stays required.
        var context = await interaction.GetAuthorizationContextAsync(
            ReturnUrl,
            HttpContext.RequestAborted
        );
        Username = context?.LoginHint ?? "";
        ClientId = context?.Client.ClientId;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ValidReturnUrl())
            return BadRequest();
        if (string.Equals(Button, "cancel", StringComparison.Ordinal))
            return await CancelAsync();
        if (!ModelState.IsValid || !users.ValidateCredentials(Username, Password))
        {
            ModelState.AddModelError("", "Invalid username or password.");
            var context = await interaction.GetAuthorizationContextAsync(
                ReturnUrl,
                HttpContext.RequestAborted
            );
            ClientId = context?.Client.ClientId;
            return Page();
        }
        var user = users.FindByUsername(Username);
        await HttpContext.SignInAsync(
            new IdentityServerUser(user.SubjectId)
            {
                DisplayName =
                    user.Claims.FirstOrDefault(c =>
                        string.Equals(c.Type, "name", StringComparison.Ordinal)
                    )?.Value
                    ?? user.Username,
            }
        );
        return Redirect(ReturnUrl);
    }

    /// <summary>The client receives <c>access_denied</c>, the standard answer when a user declines.</summary>
    private async Task<IActionResult> CancelAsync()
    {
        var context = await interaction.GetAuthorizationContextAsync(
            ReturnUrl,
            HttpContext.RequestAborted
        );
        if (context is null)
            return Redirect("/");
        await interaction.DenyAuthorizationAsync(
            context,
            InteractionError.AccessDenied,
            HttpContext.RequestAborted
        );
        return Redirect(ReturnUrl);
    }

    private bool ValidReturnUrl() =>
        string.Equals(ReturnUrl, "/", StringComparison.Ordinal)
        || interaction.IsValidReturnUrl(ReturnUrl);
}
