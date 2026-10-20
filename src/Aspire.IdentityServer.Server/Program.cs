using System.Security.Claims;
using Aspire.IdentityServer.Server;
using Duende.IdentityServer;
using Duende.IdentityServer.Models;
using Duende.IdentityServer.Test;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Secret = Duende.IdentityServer.Models.Secret;

namespace Aspire.IdentityServer.Server;

internal static class ServerProgram
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        // Bind the development provider to loopback, including behind the Aspire proxy.
        if (
            int.TryParse(
                builder.Configuration["HTTPS_PORTS"],
                System.Globalization.CultureInfo.InvariantCulture,
                out var port
            )
        )
            builder.WebHost.UseUrls($"https://localhost:{port}");
        if (!builder.Environment.IsDevelopment() && !builder.Environment.IsEnvironment("Testing"))
            throw new InvalidOperationException(
                "This identity server only runs in Development or Testing."
            );
        var config =
            builder.Configuration.GetSection("IdentityServer").Get<ServerConfiguration>()
            ?? throw new InvalidOperationException("Missing IdentityServer configuration.");
        if (
            !Uri.TryCreate(config.Issuer, UriKind.Absolute, out var issuer)
            || !string.Equals(issuer.Scheme, "https", StringComparison.Ordinal)
            || !issuer.IsLoopback
        )
            throw new InvalidOperationException(
                "The development issuer must use a loopback HTTPS address."
            );

        builder
            .Services.AddDataProtection()
            .UseEphemeralDataProtectionProvider()
            .SetApplicationName($"AspireIdentityServer:{config.Name}");
        builder.Services.AddRazorPages();
        builder.Services.AddAntiforgery(o => o.Cookie.Name = $"aspire-idp-{config.Name}.csrf");
        builder
            .Services.AddIdentityServer(options =>
            {
                options.IssuerUri = config.Issuer;
                options.KeyManagement.Enabled = false;
                options.UserInteraction.LoginUrl = "/Account/Login";
                options.UserInteraction.LogoutUrl = "/Account/Logout";
                options.UserInteraction.ErrorUrl = "/Error";
                options.Authentication.CheckSessionCookieName = $"aspire-idp-{config.Name}.session";
            })
            .AddInMemoryIdentityResources([
                new IdentityResources.OpenId(),
                new IdentityResources.Profile(),
                new IdentityResources.Email(),
            ])
            .AddInMemoryApiScopes(
                config
                    .Apis.SelectMany(a => a.Scopes)
                    .Distinct(StringComparer.Ordinal)
                    .Select(s => new ApiScope(s))
            )
            .AddInMemoryApiResources(
                config.Apis.Select(a => new ApiResource(a.Audience) { Scopes = a.Scopes })
            )
            .AddInMemoryClients(
                config.Clients.Select(c => new Client
                {
                    ClientId = c.ClientId,
                    AllowedGrantTypes = [c.GrantType],
                    RequireClientSecret = !string.Equals(
                        c.AuthenticationMethod,
                        "none",
                        StringComparison.Ordinal
                    ),
                    ClientSecrets = c.AuthenticationMethod switch
                    {
                        "private_key_jwt" =>
                        [
                            new Secret
                            {
                                Type = IdentityServerConstants.SecretTypes.JsonWebKey,
                                Value = c.PublicJwk!,
                            },
                        ],
                        "client_secret_post" => [new Secret(c.ClientSecret!.Sha256())],
                        "none"
                            when string.Equals(
                                c.GrantType,
                                "authorization_code",
                                StringComparison.Ordinal
                            ) && c.RequirePkce => [],
                        _ => throw new InvalidOperationException(
                            $"Invalid authentication for client '{c.ClientId}'."
                        ),
                    },
                    RequirePkce = c.RequirePkce,
                    RequireConsent = false,
                    AllowedScopes = c.Scopes,
                    RedirectUris = c.RedirectUris,
                    PostLogoutRedirectUris = c.PostLogoutRedirectUris,
                    AccessTokenLifetime = 300,
                    AlwaysIncludeUserClaimsInIdToken = true,
                })
            )
            .AddTestUsers(
                config
                    .Users.Select(u => new TestUser
                    {
                        Username = u.Username,
                        Password = u.Password,
                        SubjectId = u.SubjectId,
                        Claims = [new Claim("name", u.DisplayName)],
                    })
                    .ToList()
            )
            .AddJwtBearerClientAuthentication()
            .AddDeveloperSigningCredential(persistKey: false);
        builder.Services.Configure<CookieAuthenticationOptions>(
            IdentityServerConstants.DefaultCookieAuthenticationScheme,
            o => o.Cookie.Name = $"aspire-idp-{config.Name}"
        );

        var app = builder.Build();
        app.Use(
            async (context, next) =>
            {
                context.Response.Headers.XContentTypeOptions = "nosniff";
                context.Response.Headers.CacheControl = "no-store";
                await next();
            }
        );
        app.UseIdentityServer();
        app.UseAuthorization();
        app.MapRazorPages();
        app.MapGet("/health", () => Results.Ok(new { status = "ready" }));
        app.MapGet("/", () => Results.Redirect("/.well-known/openid-configuration"));
        app.Run();
    }
}
