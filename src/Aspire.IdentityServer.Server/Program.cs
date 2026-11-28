using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using Aspire.IdentityServer.Server;
using Duende.IdentityServer;
using Duende.IdentityServer.Models;
using Duende.IdentityServer.ResponseHandling;
using Duende.IdentityServer.Test;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
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
        var config = builder.Configuration.GetSection("IdentityServer").Get<ServerConfiguration>()
            ?? throw new InvalidOperationException("Missing IdentityServer configuration.");
        if (config.ContractVersion != RuntimeContract.Version)
            throw new InvalidOperationException(
                $"This runtime reads configuration contract {RuntimeContract.Version}, but the AppHost wrote {config.ContractVersion}. "
                    + "Use the same version of TheDotSide.Aspire.IdentityServer.Hosting and TheDotSide.Aspire.IdentityServer.Runtime."
            );
        if (
            !Uri.TryCreate(config.Issuer, UriKind.Absolute, out var issuer)
            || !string.Equals(issuer.Scheme, "https", StringComparison.Ordinal)
            || !issuer.IsLoopback
        )
            throw new InvalidOperationException(
                "The development issuer must use a loopback HTTPS address."
            );
        if (
            config
                .Clients.SelectMany(c => c.TokenExchanges)
                .Any(p =>
                    p.SubjectIssuer is not null
                    && (
                        !Uri.TryCreate(p.SubjectIssuer, UriKind.Absolute, out var trusted)
                        || !string.Equals(trusted.Scheme, "https", StringComparison.Ordinal)
                        || !trusted.IsLoopback
                        || string.IsNullOrWhiteSpace(p.SubjectProvider)
                        || string.Equals(p.SubjectIssuer, config.Issuer, StringComparison.Ordinal)
                    )
                )
        )
            throw new InvalidOperationException(
                "A trusted provider of a token exchange needs another loopback HTTPS issuer and its name."
            );
        if (
            config
                .Clients.SelectMany(c => c.TokenExchanges)
                .Any(p => p.Scopes is not ("FromSubjectToken" or "FromMachineClient"))
        )
            throw new InvalidOperationException(
                "Each token exchange policy needs FromSubjectToken or FromMachineClient scopes."
            );

        if (
            config.SimulatedMfa is { } simulatedMfa
            && (
                !SimulatedMfa.IsValidCode(simulatedMfa.Code)
                || simulatedMfa.AcrValues.Count == 0
                || simulatedMfa.AcrValues.Any(string.IsNullOrWhiteSpace)
            )
        )
            throw new InvalidOperationException(
                "A simulated MFA needs a code of 4 to 10 digits and at least one acr value."
            );
        if (
            config.SignUp is { Enabled: true } signUp
            && !SignUpForm.HasValidClaimTypes(signUp.ClaimTypes)
        )
            throw new InvalidOperationException(
                "A sign-up form needs distinct profile claim types issued as text, without protocol claims such as sub or typed claims such as email_verified."
            );
        // Traces, metrics and logs reach the Aspire dashboard when the AppHost provides OTLP.
        if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
            builder
                .Services.AddOpenTelemetry()
                .WithTracing(tracing =>
                    tracing
                        .AddAspNetCoreInstrumentation()
                        .AddHttpClientInstrumentation()
                        .AddSource(
                            Tracing.TraceNames.Basic,
                            Tracing.TraceNames.Cache,
                            Tracing.TraceNames.Services,
                            Tracing.TraceNames.Store,
                            Tracing.TraceNames.Validation
                        )
                )
                .WithMetrics(metrics =>
                    metrics
                        .AddAspNetCoreInstrumentation()
                        .AddHttpClientInstrumentation()
                        .AddMeter(Telemetry.ServiceName)
                )
                .WithLogging(logging => { }, options => options.IncludeFormattedMessage = true)
                .UseOtlpExporter();
        builder
            .Services.AddDataProtection()
            .UseEphemeralDataProtectionProvider()
            .SetApplicationName($"AspireIdentityServer:{config.Name}");
        builder.Services.AddRazorPages();
        builder.Services.AddAntiforgery(o =>
        {
            o.Cookie.Name = $"aspire-idp-{config.Name}.csrf";
            o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        });
        builder.Services.AddSingleton(config);
        builder.Services.AddSingleton(TimeProvider.System);
        // Discovery and keys of the providers that token-exchange policies trust.
        builder.Services.AddHttpClient(ForeignIssuerKeys.ClientName);
        builder.Services.AddSingleton<ForeignIssuerKeys>();
        var users = config
            .Users.Select(u => new TestUser
            {
                Username = u.Username,
                Password = u.Password,
                SubjectId = u.SubjectId,
                Claims =
                [
                    new Claim("name", u.DisplayName),
                    .. u.Claims.Select(c => new Claim(c.Type, c.Value)),
                ],
            })
            .ToList();
        var accounts = new LocalAccounts(users);
        builder
            .Services.AddIdentityServer(options =>
            {
                options.IssuerUri = config.Issuer;
                options.KeyManagement.Enabled = false;
                // RFC 9068 and RFC 8693: scope is one space-delimited string, as real providers emit.
                options.EmitScopesAsSpaceDelimitedStringInJwt = true;
                // draft-ietf-oauth-rfc7523bis: the issuer as sole audience and an explicit type,
                // unless the AppHost accepts the token endpoint audience for legacy clients.
                options.StrictClientAssertionAudienceValidation =
                    !config.AcceptTokenEndpointAudience;
                // Registered client keys and the helper only use RS256: advertise and accept no other.
                options.SupportedClientAssertionSigningAlgorithms =
                [
                    Microsoft.IdentityModel.Tokens.SecurityAlgorithms.RsaSha256,
                ];
                options.UserInteraction.LoginUrl = "/Account/Login";
                options.UserInteraction.LogoutUrl = "/Account/Logout";
                options.UserInteraction.ErrorUrl = "/Error";
                options.Authentication.CheckSessionCookieName = $"aspire-idp-{config.Name}.session";
                // Cookies are not isolated by port. On localhost the provider and its clients are
                // same-site, so Lax keeps check-session and front-channel logout working while the
                // session stays out of cross-site requests.
                options.Authentication.CookieSameSiteMode = SameSiteMode.Lax;
                options.Authentication.CheckSessionCookieSameSiteMode = SameSiteMode.Lax;
                options.Logging.TokenRequestSensitiveValuesFilter.Add("subject_token");
                options.Logging.TokenRequestSensitiveValuesFilter.Add("actor_token");
                // The levels of the simulated second factor, as a provider advertises its acr values.
                if (config.SimulatedMfa is { } levels)
                    options.Discovery.CustomEntries["acr_values_supported"] =
                        levels.AcrValues.ToArray();
                // With a sign-up page, Duende advertises prompt=create, sends those requests to the
                // page, then resumes them. Without one, it keeps refusing prompt=create.
                if (config.SignUp is { Enabled: true })
                    options.UserInteraction.CreateAccountUrl = SignUpForm.PagePath;
            })
            .AddInMemoryIdentityResources([
                new IdentityResources.OpenId(),
                new IdentityResources.Profile(),
                new IdentityResources.Email(),
                .. config.IdentityScopes.Select(s => new IdentityResource(s.Name, s.UserClaims)),
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
                        "client_secret_post" when c.ClientSecretHash is { Length: > 0 } hash =>
                        [
                            new Secret(hash),
                        ],
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
                    // Duende grants offline_access through this flag, not as an allowed scope.
                    AllowOfflineAccess = c.Scopes.Contains(
                        "offline_access",
                        StringComparer.Ordinal
                    ),
                    AllowedScopes = c
                        .Scopes.Where(s =>
                            !string.Equals(s, "offline_access", StringComparison.Ordinal)
                        )
                        .ToList(),
                    RedirectUris = c.RedirectUris,
                    PostLogoutRedirectUris = c.PostLogoutRedirectUris,
                    AllowedCorsOrigins = c.AllowedCorsOrigins,
                    BackChannelLogoutUri = c.BackChannelLogoutUri,
                    FrontChannelLogoutUri = c.FrontChannelLogoutUri,
                    AccessTokenLifetime = c.AccessTokenLifetime ?? 300,
                    // Duende's default absolute lifetime: 30 days.
                    AbsoluteRefreshTokenLifetime = c.AbsoluteRefreshTokenLifetime ?? 2_592_000,
                    AlwaysIncludeUserClaimsInIdToken = true,
                })
            )
            // Also registers Duende's test-user validators; only its fixed store is replaced below.
            .AddTestUsers(users)
            .AddJwtBearerClientAuthentication()
            .AddExtensionGrantValidator<TokenExchangeGrantValidator>()
            .AddProfileService<TokenExchangeProfileService>()
            // Only a form-declaring provider supports prompt=create; this refuses it without openid.
            .AddCustomAuthorizeRequestValidator<CreatePromptValidator>()
            .AddSigningCredential(SigningKey(config), SecurityAlgorithms.RsaSha256);
        // The sign-up page adds accounts while requests read them: each request resolves the
        // store of the current snapshot, from the sign-in page to the profile service. A singleton
        // must take LocalAccounts, never TestUserStore: it would keep the snapshot of its creation.
        builder.Services.AddSingleton(accounts);
        builder.Services.Replace(ServiceDescriptor.Transient(_ => accounts.Current));
        // Registered after Duende's generator, so that it replaces it: a declared level requested
        // with acr_values that the session lacks asks for the simulated code.
        if (config.SimulatedMfa is not null)
            builder.Services.AddTransient<
                IAuthorizeInteractionResponseGenerator,
                StepUpInteractionResponseGenerator
            >();
        builder.Services.Configure<CookieAuthenticationOptions>(
            IdentityServerConstants.DefaultCookieAuthenticationScheme,
            o =>
            {
                o.Cookie.Name = $"aspire-idp-{config.Name}";
                o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            }
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
                // The sign-in pages are never framed and never leak their return URL. No
                // form-action: after login, the redirect chain legitimately leaves this origin.
                if (
                    context.Request.Path.StartsWithSegments(
                        "/Account",
                        StringComparison.OrdinalIgnoreCase
                    )
                    || context.Request.Path.StartsWithSegments(
                        "/Error",
                        StringComparison.OrdinalIgnoreCase
                    )
                )
                {
                    context.Response.Headers.ContentSecurityPolicy =
                        "default-src 'self'; base-uri 'none'; object-src 'none'; frame-ancestors 'none'";
                    context.Response.Headers["Referrer-Policy"] = "no-referrer";
                }
                await next();
            }
        );
        app.UseIdentityServer();
        app.UseAuthorization();
        app.MapRazorPages();
        app.MapGet("/Account/signed-out.js", () =>
            Results.Text(Pages.Account.LogoutModel.RedirectScript, "text/javascript; charset=utf-8")
        );
        // Read at startup, so that a runtime built without its assets fails here. nosniff
        // applies: each asset needs its exact media type.
        var stylesheet = ProviderAssets.Read("site.css");
        var icon = ProviderAssets.Read("icon.svg");
        app.MapGet("/Account/site.css", () => Results.Text(stylesheet, "text/css; charset=utf-8"));
        app.MapGet("/Account/icon.svg", () => Results.Text(icon, "image/svg+xml; charset=utf-8"));
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

    /// <summary>The RS256 key that the AppHost generated for this provider, kept across restarts.</summary>
    private static RsaSecurityKey SigningKey(ServerConfiguration config)
    {
        if (string.IsNullOrWhiteSpace(config.SigningKey))
            throw new InvalidOperationException(
                "The AppHost must provide the provider's signing key."
            );
        var (parameters, keyId) = RsaJwk.ReadPrivateBase64(config.SigningKey);
        try
        {
            var rsa = RSA.Create();
            rsa.ImportParameters(parameters);
            return new RsaSecurityKey(rsa) { KeyId = keyId };
        }
        finally
        {
            RsaJwk.Clear(parameters);
        }
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
