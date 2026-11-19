using Duende.IdentityServer.Models;
using Duende.IdentityServer.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Aspire.IdentityServer.Server.Pages;

public sealed class ErrorModel(IIdentityServerInteractionService interaction) : PageModel
{
    /// <summary>The identifier IdentityServer adds when it shows this page.</summary>
    [BindProperty(SupportsGet = true)]
    public string? ErrorId { get; set; }

    /// <summary>The protocol error, while IdentityServer still knows the identifier.</summary>
    public ErrorMessage? Message { get; private set; }

    public async Task OnGetAsync()
    {
        // A development provider shows the error itself: its description names the cause.
        if (!string.IsNullOrEmpty(ErrorId))
            Message = await interaction.GetErrorContextAsync(ErrorId, HttpContext.RequestAborted);
    }
}
