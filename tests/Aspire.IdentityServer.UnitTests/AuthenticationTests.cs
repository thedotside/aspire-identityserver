using System.Security.Claims;
using System.Security.Cryptography;
using Aspire.IdentityServer.Client;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Aspire.IdentityServer.Tests;

/// <summary>The ASP.NET Core helpers read projected sections: no network, no provider.</summary>
public sealed class AuthenticationTests
{
    [Test]
    public async Task Jwt_bearer_validates_the_projected_audience_with_jwt_claim_names()
    {
        await using var services = Services(
            new(StringComparer.Ordinal)
            {
                ["Inbound:Authority"] = "https://idp.test",
                ["Inbound:Audience"] = "orders-api",
            },
            (auth, configuration) =>
                auth.AddIdentityServerJwtBearer("api", configuration.GetSection("Inbound"))
        );
        var options = services.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>().Get("api");
        await Assert.That(options.Authority).IsEqualTo("https://idp.test");
        await Assert.That(options.Audience).IsEqualTo("orders-api");
        await Assert.That(options.MapInboundClaims).IsFalse();
        await Assert.That(options.TokenValidationParameters.NameClaimType).IsEqualTo("name");
    }

    [Test]
    public async Task A_missing_projected_value_fails_with_its_configuration_key()
    {
        await using var services = Services(
            new(StringComparer.Ordinal) { ["Inbound:Audience"] = "orders-api" },
            (auth, configuration) =>
                auth.AddIdentityServerJwtBearer("api", configuration.GetSection("Inbound"))
        );
        var error = await Assert
            .That(() => services.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>().Get("api"))
            .ThrowsExactly<InvalidOperationException>();
        await Assert.That(error!.Message).Contains("Inbound:Authority");
    }

    [Test]
    public async Task Open_id_connect_uses_the_projected_secret_client_and_keeps_overrides()
    {
        await using var services = Services(
            new(StringComparer.Ordinal)
            {
                ["Web:Authority"] = "https://idp.test",
                ["Web:ClientId"] = "web",
                ["Web:AuthenticationMethod"] = "client_secret_post",
                ["Web:ClientSecret"] = "web-secret",
                ["Web:Scopes"] = "openid profile orders.read",
            },
            (auth, configuration) =>
                auth.AddIdentityServerOpenIdConnect(
                    "oidc",
                    configuration.GetSection("Web"),
                    options => options.CallbackPath = "/signin-web"
                )
        );
        var options = services
            .GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>()
            .Get("oidc");
        await Assert.That(options.Authority).IsEqualTo("https://idp.test");
        await Assert.That(options.ClientId).IsEqualTo("web");
        await Assert.That(options.ClientSecret).IsEqualTo("web-secret");
        await Assert.That(options.ResponseType).IsEqualTo(OpenIdConnectResponseType.Code);
        await Assert.That(options.UsePkce).IsTrue();
        await Assert.That(options.SaveTokens).IsTrue();
        await Assert.That(options.MapInboundClaims).IsFalse();
        await Assert
            .That(options.Scope.ToArray())
            .IsEquivalentTo(["openid", "profile", "orders.read"]);
        await Assert.That(options.CallbackPath.Value).IsEqualTo("/signin-web");
    }

    [Test]
    public async Task A_private_key_client_signs_the_code_redemption_and_the_pushed_request()
    {
        using var rsa = RSA.Create(2048);
        await using var services = Services(
            new(StringComparer.Ordinal)
            {
                ["Web:Authority"] = "https://idp.test",
                ["Web:ClientId"] = "web",
                ["Web:AuthenticationMethod"] = "private_key_jwt",
                ["Web:PrivateKeyBase64"] = TestKeys.Encode(rsa, "web-key"),
                ["Web:Scopes"] = "openid",
            },
            (auth, configuration) =>
                auth.AddIdentityServerOpenIdConnect(
                    "oidc",
                    configuration.GetSection("Web"),
                    options =>
                        options.ConfigurationManager =
                            new StaticConfigurationManager<OpenIdConnectConfiguration>(
                                new OpenIdConnectConfiguration { Issuer = "https://idp.test" }
                            )
                )
        );
        var options = services
            .GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>()
            .Get("oidc");
        await Assert.That(options.ClientSecret).IsNull();
        var http = new DefaultHttpContext { RequestServices = services };
        var scheme = new AuthenticationScheme("oidc", null, typeof(OpenIdConnectHandler));

        var pushed = new PushedAuthorizationContext(
            http,
            scheme,
            options,
            new OpenIdConnectMessage(),
            new AuthenticationProperties()
        );
        await options.Events.PushAuthorization(pushed);
        await Assert.That(pushed.HandledClientAuthentication).IsTrue();
        await Assert
            .That(pushed.ProtocolMessage.ClientAssertionType)
            .IsEqualTo(PrivateKeyJwt.AssertionType);
        var pushedAssertion = new JsonWebToken(pushed.ProtocolMessage.ClientAssertion);
        await Assert.That(pushedAssertion.Audiences.Single()).IsEqualTo("https://idp.test");
        await Assert.That(pushedAssertion.Subject).IsEqualTo("web");

        var redeemed = new AuthorizationCodeReceivedContext(
            http,
            scheme,
            options,
            new AuthenticationProperties()
        )
        {
            TokenEndpointRequest = new OpenIdConnectMessage(),
        };
        await options.Events.AuthorizationCodeReceived(redeemed);
        var codeAssertion = new JsonWebToken(redeemed.TokenEndpointRequest.ClientAssertion);
        await Assert.That(codeAssertion.Audiences.Single()).IsEqualTo("https://idp.test");
        // Each request carries its own assertion: a replayed one would be refused.
        await Assert.That(codeAssertion.Id).IsNotEqualTo(pushedAssertion.Id);
    }

    [Test]
    [Arguments("orders.write", true)]
    [Arguments("orders.delete", false)]
    public async Task Scope_policies_read_the_space_delimited_scope_claim(
        string required,
        bool allowed
    )
    {
        await using var services = new ServiceCollection()
            .AddLogging()
            .AddAuthorization()
            .BuildServiceProvider();
        var policy = new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .RequireScope(required, "orders.admin")
            .Build();
        var user = new ClaimsPrincipal(
            new ClaimsIdentity([new Claim("scope", "orders.read orders.write")], "Bearer")
        );
        var result = await services
            .GetRequiredService<IAuthorizationService>()
            .AuthorizeAsync(user, policy);
        await Assert.That(result.Succeeded).IsEqualTo(allowed);
    }

    private static ServiceProvider Services(
        Dictionary<string, string?> values,
        Action<AuthenticationBuilder, IConfiguration> configure
    )
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection().AddLogging();
        configure(services.AddAuthentication(), configuration);
        return services.BuildServiceProvider(validateScopes: true);
    }
}
