using Duende.IdentityServer;
using Duende.IdentityServer.Services;
using Duende.IdentityServer.Test;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Aspire.IdentityServer.Server.Pages.Account;

public sealed class LoginModel(IIdentityServerInteractionService interaction, TestUserStore users) : PageModel
{
    [BindProperty(SupportsGet = true)] public string ReturnUrl { get; set; } = "/";
    [BindProperty] public string Username { get; set; } = "";
    [BindProperty] public string Password { get; set; } = "";

    public IActionResult OnGet() => ValidReturnUrl() ? Page() : BadRequest();

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ValidReturnUrl()) return BadRequest();
        if (!ModelState.IsValid || !users.ValidateCredentials(Username, Password))
        {
            ModelState.AddModelError("", "Invalid username or password.");
            return Page();
        }
        var user = users.FindByUsername(Username);
        await HttpContext.SignInAsync(new IdentityServerUser(user.SubjectId)
        {
            DisplayName = user.Claims.FirstOrDefault(c => c.Type == "name")?.Value ?? user.Username
        });
        return Redirect(ReturnUrl);
    }

    private bool ValidReturnUrl() => ReturnUrl == "/" || interaction.IsValidReturnUrl(ReturnUrl);
}
