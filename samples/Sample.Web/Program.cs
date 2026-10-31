using System.Net.Http.Json;
using System.Text.Encodings.Web;
using Aspire.IdentityServer.Client;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
var authentication = builder.Services.AddAuthentication();
foreach (var (provider, section) in new[] { ("a", "IdpA"), ("b", "IdpB"), ("public", "Public") })
{
    var client = builder
        .Configuration.GetSection($"Authentication:{section}")
        .Get<IdentityClientOptions>()!;
    authentication
        .AddCookie($"cookie-{provider}", o => o.Cookie.Name = $"aspire-sample-{provider}")
        .AddOpenIdConnect(
            $"oidc-{provider}",
            options =>
            {
                options.SignInScheme = $"cookie-{provider}";
                options.Authority = client.Authority;
                options.ClientId = client.ClientId;
                options.ClientSecret = client.ClientSecret;
                options.ResponseType = "code";
                options.UsePkce = true;
                options.SaveTokens = true;
                options.MapInboundClaims = false;
                options.TokenValidationParameters.NameClaimType = "name";
                options.CallbackPath = $"/signin-{provider}";
                options.SignedOutCallbackPath = $"/signout-callback-{provider}";
                options.RemoteSignOutPath = $"/signout-{provider}";
                options.PushedAuthorizationBehavior = PushedAuthorizationBehavior.Disable;
                options.Scope.Clear();
                foreach (
                    var scope in client.Scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                )
                    options.Scope.Add(scope);
                if (
                    string.Equals(
                        client.AuthenticationMethod,
                        "private_key_jwt",
                        StringComparison.Ordinal
                    )
                )
                {
                    options.Events.OnAuthorizationCodeReceived = async context =>
                    {
                        var metadata =
                            await context.Options.ConfigurationManager!.GetConfigurationAsync(
                                context.HttpContext.RequestAborted
                            );
                        context.TokenEndpointRequest!.ClientAssertionType =
                            PrivateKeyJwt.AssertionType;
                        context.TokenEndpointRequest.ClientAssertion =
                            PrivateKeyJwt.CreateAssertion(client, metadata.Issuer);
                    };
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
        .Services.AddHttpClient(
            $"orders-{name}",
            client => client.BaseAddress = new Uri(builder.Configuration["services:api:https:0"]!)
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
app.MapGet(
    "/",
    async (HttpContext context, IAntiforgery antiforgery) =>
    {
        var token = antiforgery.GetAndStoreTokens(context);
        var rows = new List<string>();
        foreach (var provider in new[] { "a", "b", "public" })
        {
            var result = await context.AuthenticateAsync($"cookie-{provider}");
            var name = HtmlEncoder.Default.Encode(result.Principal?.Identity?.Name ?? "Signed out");
            rows.Add(
                $"<section><h2>Provider {provider}</h2><p id='user-{provider}'>{name}</p><a href='/login/{provider}'>Sign in {provider}</a>"
                    + $"<form method='post' action='/logout/{provider}'><input type='hidden' name='{token.FormFieldName}' value='{token.RequestToken}'/><button>Sign out {provider}</button></form></section>"
            );
        }
        return Results.Content(
            "<!doctype html><html lang='en'><head><meta charset='utf-8'/><title>Two identity providers</title></head><body><h1>Aspire IdentityServer</h1>"
                + string.Join("", rows)
                + "</body></html>",
            "text/html"
        );
    }
);
foreach (var provider in new[] { "a", "b", "public" })
{
    app.MapGet(
        $"/login/{provider}",
        () =>
            Results.Challenge(
                new AuthenticationProperties { RedirectUri = "/" },
                [$"oidc-{provider}"]
            )
    );
    app.MapPost(
        $"/logout/{provider}",
        async (HttpContext context, IAntiforgery antiforgery) =>
        {
            await antiforgery.ValidateRequestAsync(context);
            await context.SignOutAsync(
                $"oidc-{provider}",
                new AuthenticationProperties { RedirectUri = "/" }
            );
            await context.SignOutAsync($"cookie-{provider}");
        }
    );
    app.MapGet(
            $"/me/{provider}",
            (HttpContext context) =>
                new
                {
                    name = context.User.Identity?.Name,
                    subject = context.User.FindFirst("sub")?.Value,
                }
        )
        .RequireAuthorization(
            new AuthorizationPolicyBuilder($"cookie-{provider}").RequireAuthenticatedUser().Build()
        );
}
app.Run();
