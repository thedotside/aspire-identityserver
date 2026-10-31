using System.Globalization;
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
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        // Bind the development provider to loopback in code, including behind the Aspire proxy.
        // Endpoints declared in code replace URLS and *_PORTS; extra configured endpoints are refused.
        var port = LoopbackPort(builder.Configuration);
        builder.WebHost.ConfigureKestrel(kestrel =>
            kestrel.ListenLocalhost(port, listen => listen.UseHttps())
        );
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
        builder.Services.AddSingleton(config);
        builder.Services.AddSingleton(TimeProvider.System);
        builder
            .Services.AddIdentityServer(options =>
            {
                options.IssuerUri = config.Issuer;
                options.KeyManagement.Enabled = false;
                // RFC 9068 and RFC 8693: scope is one space-delimited string, as real providers emit.
                options.EmitScopesAsSpaceDelimitedStringInJwt = true;
                // Registered client keys and the helper only use RS256: advertise and accept no other.
                options.SupportedClientAssertionSigningAlgorithms =
                [
                    Microsoft.IdentityModel.Tokens.SecurityAlgorithms.RsaSha256,
                ];
                options.UserInteraction.LoginUrl = "/Account/Login";
                options.UserInteraction.LogoutUrl = "/Account/Logout";
                options.UserInteraction.ErrorUrl = "/Error";
                options.Authentication.CheckSessionCookieName = $"aspire-idp-{config.Name}.session";
                options.Logging.TokenRequestSensitiveValuesFilter.Add("subject_token");
                options.Logging.TokenRequestSensitiveValuesFilter.Add("actor_token");
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
                    AllowedGrantTypes =
                        c.TokenExchanges.Count == 0
                            ? [c.GrantType]
                            : [c.GrantType, TokenExchangeGrantValidator.GrantTypeValue],
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
            .AddExtensionGrantValidator<TokenExchangeGrantValidator>()
            .AddProfileService<TokenExchangeProfileService>()
            .AddDeveloperSigningCredential(persistKey: false);
        builder.Services.Configure<CookieAuthenticationOptions>(
            IdentityServerConstants.DefaultCookieAuthenticationScheme,
            o => o.Cookie.Name = $"aspire-idp-{config.Name}"
        );

        var app = builder.Build();
        app.Logger.LogWarning(
            "Identity provider '{Name}' ({Environment}) is for local development and tests only. "
                + "Never expose it, and never accept its tokens outside development.",
            config.Name,
            app.Environment.EnvironmentName
        );
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
        await app.StartAsync(CancellationToken.None);
        if (app.Urls.Count == 0 || app.Urls.Any(url => !new Uri(url).IsLoopback))
        {
            await app.StopAsync(CancellationToken.None);
            throw new InvalidOperationException(
                $"The development identity provider must listen on loopback only, not {string.Join(", ", app.Urls)}."
            );
        }
        await app.WaitForShutdownAsync(CancellationToken.None);
    }

    private static int LoopbackPort(IConfiguration configuration)
    {
        if (configuration.GetSection("Kestrel:Endpoints").GetChildren().Any())
            throw new InvalidOperationException(
                "Kestrel endpoints cannot be configured: this identity provider listens on HTTPS loopback only."
            );
        if (
            !int.TryParse(
                configuration["HTTPS_PORTS"],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var port
            ) || port is < 1 or > 65535
        )
            throw new InvalidOperationException(
                "Set ASPNETCORE_HTTPS_PORTS to a single port: this identity provider listens on HTTPS loopback only."
            );
        return port;
    }
}
