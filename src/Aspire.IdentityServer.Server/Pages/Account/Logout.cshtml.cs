using Duende.IdentityServer.Models;
using Duende.IdentityServer.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Aspire.IdentityServer.Server.Pages.Account;

public sealed class LogoutModel(IIdentityServerInteractionService interaction) : PageModel
{
    /// <summary>
    /// Served from this origin, as the sign-in pages allow no inline script: returns to the
    /// application once the window, sign-out notification frames included, has loaded.
    /// </summary>
    internal const string RedirectScript = """
        window.addEventListener("load", function () {
          var link = document.getElementById("return");
          if (link) { window.location.href = link.href; }
        });
        """;

    [BindProperty(SupportsGet = true)]
    public string? LogoutId { get; set; }
    public bool SignedOut { get; private set; }
    public string? ReturnUrl { get; private set; }
    public string? SignOutIFrameUrl { get; private set; }

    /// <summary>Whether the signed-out page returns to the application by itself.</summary>
    public bool AutomaticRedirect { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        var logout = await interaction.GetLogoutContextAsync(LogoutId, HttpContext.RequestAborted);
        // An application that proves the session with id_token_hint signs out directly, as with
        // production providers. Any other request, which any page could send, is confirmed.
        return logout.ShowSignoutPrompt ? Page() : await SignOutAsync(logout);
    }

    public async Task<IActionResult> OnPostAsync() =>
        await SignOutAsync(
            await interaction.GetLogoutContextAsync(LogoutId, HttpContext.RequestAborted)
        );

    private async Task<IActionResult> SignOutAsync(LogoutRequest logout)
    {
        await HttpContext.SignOutAsync();
        SignedOut = true;
        ReturnUrl = logout.PostLogoutRedirectUri;
        SignOutIFrameUrl = logout.SignOutIFrameUrl;
        // Duende has validated the address against the client registration.
        if (ReturnUrl is not null && SignOutIFrameUrl is null)
            return Redirect(ReturnUrl);
        // The notification frame must load before leaving: the page script then returns.
        AutomaticRedirect = ReturnUrl is not null;
        return Page();
    }
}
