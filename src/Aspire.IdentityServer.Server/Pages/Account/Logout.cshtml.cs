using Duende.IdentityServer.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Aspire.IdentityServer.Server.Pages.Account;

public sealed class LogoutModel(IIdentityServerInteractionService interaction) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string? LogoutId { get; set; }
    public bool SignedOut { get; private set; }
    public string? ReturnUrl { get; private set; }
    public string? SignOutIFrameUrl { get; private set; }

    public async Task OnPostAsync()
    {
        var logout = await interaction.GetLogoutContextAsync(LogoutId, HttpContext.RequestAborted);
        await HttpContext.SignOutAsync();
        SignedOut = true;
        ReturnUrl = logout?.PostLogoutRedirectUri;
        SignOutIFrameUrl = logout?.SignOutIFrameUrl;
    }
}
