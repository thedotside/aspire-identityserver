using Aspire.IdentityServer.Client;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// ASP.NET Core authentication schemes configured from the sections that the AppHost projects
/// with <c>WithIdentityApiResource</c> and <c>WithIdentityClient</c>. Claims keep their JWT names.
/// </summary>
public static class IdentityServerAuthenticationExtensions
{
    /// <summary>
    /// Validates the access tokens of an API audience projected by
    /// <c>WithIdentityApiResource</c>: <c>{section}:Authority</c> and <c>{section}:Audience</c>.
    /// <paramref name="configure" /> runs after these defaults.
    /// </summary>
    public static AuthenticationBuilder AddIdentityServerJwtBearer(
        this AuthenticationBuilder builder,
        string authenticationScheme,
        IConfigurationSection apiConfiguration,
        Action<JwtBearerOptions>? configure = null
    )
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(authenticationScheme);
        ArgumentNullException.ThrowIfNull(apiConfiguration);
        builder
            .Services.AddOptions<JwtBearerOptions>(authenticationScheme)
            .Configure(options =>
            {
                options.Authority = Required(apiConfiguration, "Authority");
                options.Audience = Required(apiConfiguration, "Audience");
                options.MapInboundClaims = false;
                options.TokenValidationParameters.NameClaimType = "name";
                options.TokenValidationParameters.RoleClaimType = "role";
            });
        return builder.AddJwtBearer(authenticationScheme, configure ?? (_ => { }));
    }

    /// <summary>
    /// Signs users in with an authorization-code client projected by
    /// <c>WithIdentityClient(client, section)</c>: authority, client ID, scopes, PKCE and saved
    /// tokens. A <c>client_secret_post</c> client sends its secret; a <c>private_key_jwt</c> client
    /// signs a new assertion, whose audience is the discovered issuer, for the code redemption
    /// and the pushed authorization request. <paramref name="configure" /> runs after these
    /// defaults, for the callback paths, cookies or <c>MaxAge</c> for instance.
    /// </summary>
    public static AuthenticationBuilder AddIdentityServerOpenIdConnect(
        this AuthenticationBuilder builder,
        string authenticationScheme,
        IConfigurationSection clientConfiguration,
        Action<OpenIdConnectOptions>? configure = null
    )
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(authenticationScheme);
        ArgumentNullException.ThrowIfNull(clientConfiguration);
        builder
            .Services.AddOptions<IdentityClientOptions>(authenticationScheme)
            .Bind(clientConfiguration);
        builder
            .Services.AddOptions<OpenIdConnectOptions>(authenticationScheme)
            .Configure<IOptionsMonitor<IdentityClientOptions>>(
                (options, clients) =>
                {
                    var client = clients.Get(authenticationScheme);
                    options.Authority = Required(clientConfiguration, "Authority");
                    options.ClientId = Required(clientConfiguration, "ClientId");
                    options.ClientSecret = string.Equals(
                        client.AuthenticationMethod,
                        "client_secret_post",
                        StringComparison.Ordinal
                    )
                        ? Required(clientConfiguration, "ClientSecret")
                        : null;
                    options.ResponseType = OpenIdConnectResponseType.Code;
                    options.UsePkce = true;
                    options.SaveTokens = true;
                    options.MapInboundClaims = false;
                    options.TokenValidationParameters.NameClaimType = "name";
                    options.TokenValidationParameters.RoleClaimType = "role";
                    options.Scope.Clear();
                    foreach (
                        var scope in client.Scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    )
                        options.Scope.Add(scope);
                }
            )
            // After the caller's configuration, so that its own event handlers are kept.
            .PostConfigure<IOptionsMonitor<IdentityClientOptions>>(
                (options, clients) =>
                {
                    var client = clients.Get(authenticationScheme);
                    if (
                        string.Equals(
                            client.AuthenticationMethod,
                            "private_key_jwt",
                            StringComparison.Ordinal
                        )
                    )
                        SignClientAssertions(options, client);
                }
            );
        return builder.AddOpenIdConnect(authenticationScheme, configure ?? (_ => { }));
    }

    private static void SignClientAssertions(
        OpenIdConnectOptions options,
        IdentityClientOptions client
    )
    {
        var redeemCode = options.Events.OnAuthorizationCodeReceived;
        options.Events.OnAuthorizationCodeReceived = async context =>
        {
            if (context.TokenEndpointRequest is { } request)
            {
                request.ClientAssertionType = PrivateKeyJwt.AssertionType;
                request.ClientAssertion = PrivateKeyJwt.CreateAssertion(
                    client,
                    await IssuerAsync(context.Options, context.HttpContext.RequestAborted)
                        .ConfigureAwait(false)
                );
            }
            await redeemCode(context).ConfigureAwait(false);
        };
        var push = options.Events.OnPushAuthorization;
        options.Events.OnPushAuthorization = async context =>
        {
            context.ProtocolMessage.ClientAssertionType = PrivateKeyJwt.AssertionType;
            context.ProtocolMessage.ClientAssertion = PrivateKeyJwt.CreateAssertion(
                client,
                await IssuerAsync(context.Options, context.HttpContext.RequestAborted)
                    .ConfigureAwait(false)
            );
            context.HandleClientAuthentication();
            await push(context).ConfigureAwait(false);
        };
    }

    /// <summary>The issuer of the discovery document: the audience of client assertions.</summary>
    private static async Task<string> IssuerAsync(
        OpenIdConnectOptions options,
        CancellationToken cancellationToken
    )
    {
        var manager = options.ConfigurationManager
            ?? throw new InvalidOperationException("The OpenID Connect metadata are unavailable.");
        var configuration = await manager
            .GetConfigurationAsync(cancellationToken)
            .ConfigureAwait(false);
        return configuration.Issuer;
    }

    private static string Required(IConfigurationSection section, string key) =>
        section[key] is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException(
                $"Configure {section.Path}:{key}; the AppHost projects it with TheDotSide.Aspire.IdentityServer."
            );
}
