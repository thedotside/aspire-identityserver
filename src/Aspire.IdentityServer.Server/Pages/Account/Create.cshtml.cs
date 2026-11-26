using System.Security.Claims;
using Duende.IdentityServer;
using Duende.IdentityServer.Models;
using Duende.IdentityServer.Services;
using Duende.IdentityServer.Test;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Aspire.IdentityServer.Server.Pages.Account;

/// <summary>
/// The sign-up page that prompt=create opens on a provider declared with <c>WithSignUp</c>: it
/// creates an account in the provider's memory, signs it in, then resumes the authorization
/// request, which can still ask for the simulated MFA.
/// </summary>
public sealed class CreateModel(
    IIdentityServerInteractionService interaction,
    ILogger<CreateModel> logger
) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string ReturnUrl { get; set; } = "/";

    [BindProperty]
    public string Username { get; set; } = "";

    [BindProperty]
    public string Password { get; set; } = "";

    /// <summary>The values of the claim fields, in the order of <see cref="ClaimFields" />.</summary>
    [BindProperty]
    public IList<string> ClaimValues { get; set; } = [];

    /// <summary>The submit button used: <c>create</c> or <c>cancel</c>.</summary>
    [BindProperty]
    public string? Button { get; set; }

    /// <summary>The client that asked for the sign-up, as the AppHost registered it.</summary>
    public string? ClientId { get; private set; }

    /// <summary>The claim types that the form asks for, in declaration order.</summary>
    public IReadOnlyList<string> ClaimFields { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync()
    {
        if (Form is not { } form)
            return NotFound();
        if (await PendingAsync() is not { } request)
            return BadRequest();
        return Show(form, request);
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (Form is not { } form)
            return NotFound();
        if (await PendingAsync() is not { } request)
            return BadRequest();
        if (string.Equals(Button, "cancel", StringComparison.Ordinal))
        {
            // The client receives access_denied, as when the user cancels the sign-in.
            await interaction.DenyAuthorizationAsync(
                request,
                InteractionError.AccessDenied,
                HttpContext.RequestAborted
            );
            return Redirect(ReturnUrl);
        }
        // Model binding turns blank values into null; spaces pass the browser's required check.
        if (
            string.IsNullOrWhiteSpace(Username)
            || string.IsNullOrWhiteSpace(Password)
            || ClaimValues.Count != form.ClaimTypes.Count
            || ClaimValues.Any(string.IsNullOrWhiteSpace)
        )
            return Refuse(form, request, "Fill in every field.");
        // The sign-in page compares usernames as typed: a stray space would hide the account.
        if (!string.Equals(Username, Username.Trim(), StringComparison.Ordinal))
            return Refuse(form, request, "The username cannot start or end with a space.");
        var provider = HttpContext.RequestServices.GetRequiredService<ServerConfiguration>();
        var claims = form
            .ClaimTypes.Zip(ClaimValues, (type, value) => new Claim(type, value))
            .ToList();
        // A name field gives the display name, as the AppHost's display name does; else the username.
        var name = claims.Find(c => string.Equals(c.Type, "name", StringComparison.Ordinal))?.Value
            ?? Username;
        claims.RemoveAll(c => string.Equals(c.Type, "name", StringComparison.Ordinal));
        var user = new TestUser
        {
            Username = Username,
            Password = Password,
            SubjectId = $"{provider.Name}:{Username}",
            Claims = [new Claim("name", name), .. claims],
        };
        if (!HttpContext.RequestServices.GetRequiredService<LocalAccounts>().TryAdd(user))
            return Refuse(form, request, "This username is already taken.");
        logger.LogInformation(
            "Created test account '{Username}' with subject '{SubjectId}' on identity provider '{Name}'.",
            user.Username,
            user.SubjectId,
            provider.Name
        );
        await HttpContext.SignInAsync(
            new IdentityServerUser(user.SubjectId) { DisplayName = name }
        );
        return Redirect(ReturnUrl);
    }

    /// <summary>The sign-up form declared in the AppHost, or <see langword="null" />.</summary>
    private SignUpConfiguration? Form =>
        HttpContext.RequestServices.GetRequiredService<ServerConfiguration>().SignUp
            is { Enabled: true } form
            ? form
            : null;

    /// <summary>The authorization request that opened this page, or <see langword="null" />.</summary>
    private async Task<AuthorizationRequest?> PendingAsync() =>
        interaction.IsValidReturnUrl(ReturnUrl)
            ? await interaction.GetAuthorizationContextAsync(ReturnUrl, HttpContext.RequestAborted)
            : null;

    private PageResult Show(SignUpConfiguration form, AuthorizationRequest request)
    {
        ClientId = request.Client.ClientId;
        ClaimFields = form.ClaimTypes;
        // One value per field, so that every field renders, empty or as typed.
        while (ClaimValues.Count < ClaimFields.Count)
            ClaimValues.Add("");
        return Page();
    }

    /// <summary>The page again with one message, keeping what was typed except the password.</summary>
    private PageResult Refuse(
        SignUpConfiguration form,
        AuthorizationRequest request,
        string message
    )
    {
        ModelState.Clear();
        Password = "";
        ModelState.AddModelError("", message);
        return Show(form, request);
    }
}
