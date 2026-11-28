using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Encodings.Web;
using Aspire.IdentityServer.Client;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
var authentication = builder.Services.AddAuthentication();

// "par" signs in with the private-key client of provider A through pushed authorization requests.
// "mfa" signs in with the same client and asks provider A for its simulated second factor.
foreach (
    var (provider, section, pushed, acrValues) in new (string, string, bool, string?)[]
    {
        ("a", "IdpA", false, null),
        ("b", "IdpB", false, null),
        ("public", "Public", false, null),
        ("par", "IdpA", true, null),
        ("mfa", "IdpA", false, "urn:sample:loa:mfa"),
    }
)
{
    authentication
        .AddCookie($"cookie-{provider}", o => o.Cookie.Name = $"aspire-sample-{provider}")
        .AddIdentityServerOpenIdConnect(
            $"oidc-{provider}",
            builder.Configuration.GetSection($"Authentication:{section}"),
            options =>
            {
                options.SignInScheme = $"cookie-{provider}";
                // Exercise signed auth_time validation in the ASP.NET Core 11 OIDC handler.
                options.MaxAge = TimeSpan.FromMinutes(5);
                options.CallbackPath = $"/signin-{provider}";
                options.SignedOutCallbackPath = $"/signout-callback-{provider}";
                options.RemoteSignOutPath = $"/signout-{provider}";
                // A cancelled sign-in comes back to the home page, which needs no account,
                // instead of the error that the handler raises without this path.
                options.AccessDeniedPath = "/";
                // The browser tests read max_age from the authorization URL of the other schemes.
                options.PushedAuthorizationBehavior = pushed
                    ? PushedAuthorizationBehavior.Require
                    : PushedAuthorizationBehavior.Disable;
                if (acrValues is not null)
                {
                    options.AdditionalAuthorizationParameters["acr_values"] = acrValues;
                    // The handler drops acr from the signed-in user by default: keep the level.
                    options.ClaimActions.Remove("acr");
                }
            }
        );
}
builder.Services.AddAuthorization();
builder.Services.AddAntiforgery();
builder.Services.AddHttpContextAccessor();
foreach (var (name, section) in new[] { ("private-key", "PrivateKey"), ("secret", "Secret") })
{
    builder
        .Services.AddHttpClient($"orders-{name}", client =>
            client.BaseAddress = new Uri(builder.Configuration["services:api:https:0"]!)
        )
        .AddTokenExchangeHandler(
            builder.Configuration.GetSection($"TokenExchange:{section}"),
            options =>
            {
                options.Scope = "orders.read";
                options.AllowedOrigins.Add(new Uri(builder.Configuration["services:api:https:0"]!));
                options.SubjectTokenProvider = async (services, _, cancellationToken) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var context = services.GetRequiredService<IHttpContextAccessor>().HttpContext;
                    return context is null
                        ? null
                        : await context.GetTokenAsync("cookie-a", "access_token");
                };
            }
        );
}
var app = builder.Build();

// The single-page application of /spa/ is plain static files.
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.MapDefaultEndpoints();
app.MapGet(
        "/delegated-orders/{method}",
        async (string method, IHttpClientFactory clients, CancellationToken cancellationToken) =>
        {
            if (method is not ("private-key" or "secret"))
                return Results.NotFound();
            var http = clients.CreateClient($"orders-{method}");
            using var response = await http.GetAsync("/orders/a", cancellationToken);
            response.EnsureSuccessStatusCode();
            return Results.Json(
                await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(
                    cancellationToken
                )
            );
        }
    )
    .RequireAuthorization(
        new AuthorizationPolicyBuilder("cookie-a").RequireAuthenticatedUser().Build()
    );
app.MapGet("/spa/config.json", (IConfiguration configuration) =>
    new
    {
        authority = configuration["Spa:Authority"],
        clientId = configuration["Spa:ClientId"],
        scopes = configuration["Spa:Scopes"],
    }
);

// OpenID Connect Back-Channel Logout: the provider posts a signed logout token when a session
// ends. This sample only records its session ID; an application would end that session.
var loggedOutSessions = new ConcurrentQueue<string>();
app.MapPost(
    "/backchannel-logout-a",
    async (HttpContext context, IOptionsMonitor<OpenIdConnectOptions> oidc) =>
    {
        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        var options = oidc.Get("oidc-a");
        var metadata = await options.ConfigurationManager!.GetConfigurationAsync(
            context.RequestAborted
        );
        var result = await new JsonWebTokenHandler().ValidateTokenAsync(
            form["logout_token"].ToString(),
            new TokenValidationParameters
            {
                ValidIssuer = metadata.Issuer,
                ValidAudience = options.ClientId,
                IssuerSigningKeys = metadata.SigningKeys,
            }
        );
        if (
            !result.IsValid
            || !result.Claims.TryGetValue("events", out var events)
            || events
                .ToString()
                ?.Contains(
                    "http://schemas.openid.net/event/backchannel-logout",
                    StringComparison.Ordinal
                ) != true
            || !result.Claims.TryGetValue("sid", out var sid)
        )
            return Results.BadRequest();
        loggedOutSessions.Enqueue(sid.ToString()!);
        return Results.Ok();
    }
);
app.MapGet("/backchannel-logouts", () => loggedOutSessions.ToArray());
app.MapGet("/", async (HttpContext context, IAntiforgery antiforgery) =>
{
    var token = antiforgery.GetAndStoreTokens(context);
    var rows = new List<string>();
    foreach (var provider in new[] { "a", "b", "public", "par", "mfa" })
    {
        var result = await context.AuthenticateAsync($"cookie-{provider}");
        var name = HtmlEncoder.Default.Encode(result.Principal?.Identity?.Name ?? "Signed out");
        // Provider A also opens its sign-up page, as a "Create an account" button would.
        var signUp = string.Equals(provider, "a", StringComparison.Ordinal)
            ? " <a href='/signup/a'>Create an account</a>"
            : "";
        rows.Add(
            $"<section><h2>Provider {provider}</h2><p id='user-{provider}'>{name}</p><a href='/login/{provider}'>Sign in {provider}</a>{signUp}"
                + $"<form method='post' action='/logout/{provider}'><input type='hidden' name='{token.FormFieldName}' value='{token.RequestToken}'/><button>Sign out {provider}</button></form></section>"
        );
    }
    return Results.Content(
        "<!doctype html><html lang='en'><head><meta charset='utf-8'/><title>Two identity providers</title></head><body><h1>Aspire IdentityServer</h1>"
            + string.Join("", rows)
            + "</body></html>",
        "text/html"
    );
});
foreach (var provider in new[] { "a", "b", "public", "par", "mfa" })
{
    app.MapGet($"/login/{provider}", () =>
        Results.Challenge(new AuthenticationProperties { RedirectUri = "/" }, [$"oidc-{provider}"])
    );
    app.MapPost($"/logout/{provider}", async (HttpContext context, IAntiforgery antiforgery) =>
    {
        await antiforgery.ValidateRequestAsync(context);
        await context.SignOutAsync(
            $"oidc-{provider}",
            new AuthenticationProperties { RedirectUri = "/" }
        );
        await context.SignOutAsync($"cookie-{provider}");
    });
    app.MapGet($"/me/{provider}", (HttpContext context) =>
            new
            {
                name = context.User.Identity?.Name,
                subject = context.User.FindFirst("sub")?.Value,
                email = context.User.FindFirst("email")?.Value,
                sid = context.User.FindFirst("sid")?.Value,
                acr = context.User.FindFirst("acr")?.Value,
                amr = context.User.FindAll("amr").Select(c => c.Value).ToArray(),
            }
        )
        .RequireAuthorization(
            new AuthorizationPolicyBuilder($"cookie-{provider}").RequireAuthenticatedUser().Build()
        );
}

// prompt=create opens the sign-up page of provider A, directly or through pushed authorization.
foreach (var provider in new[] { "a", "par" })
{
    app.MapGet($"/signup/{provider}", () =>
        Results.Challenge(
            new OpenIdConnectChallengeProperties { RedirectUri = "/", Prompt = "create" },
            [$"oidc-{provider}"]
        )
    );
}
app.Run();
