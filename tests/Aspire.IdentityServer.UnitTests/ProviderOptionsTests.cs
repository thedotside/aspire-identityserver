using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.IdentityServer.Tests;

/// <summary>Client and provider options written to the provider environment, without starting it.</summary>
public sealed class ProviderOptionsTests
{
    [Test]
    public async Task Token_lifetimes_reach_the_provider_in_seconds()
    {
        var builder = TestApp.CreateBuilder();
        var server = builder.AddIdentityServer("lifetimes");
        server.AddApiResource("orders").WithScopes("read");
        server
            .AddAuthorizationCodeClient("web")
            .WithScopes("openid", "read", "offline_access")
            .WithRedirectUri(server.GetEndpoint("https"), "/callback")
            .WithAccessTokenLifetime(TimeSpan.FromSeconds(30))
            .WithRefreshTokenLifetime(TimeSpan.FromMinutes(2));
        var values = await ProjectionTests.Evaluate(server);
        await Assert
            .That(values["IdentityServer__Clients__0__AccessTokenLifetime"])
            .IsEqualTo("30");
        await Assert
            .That(values["IdentityServer__Clients__0__AbsoluteRefreshTokenLifetime"])
            .IsEqualTo("120");
    }

    [Test]
    public async Task Lifetimes_are_bounded_and_refresh_lifetimes_need_offline_access()
    {
        var builder = TestApp.CreateBuilder();
        var server = builder.AddIdentityServer("bounded");
        server.AddApiResource("orders").WithScopes("read");
        var machine = server.AddClientCredentialsClient("machine").WithGeneratedClientSecret();
        await Assert
            .That(() => machine.WithAccessTokenLifetime(TimeSpan.Zero))
            .ThrowsExactly<ArgumentOutOfRangeException>();
        await Assert
            .That(() => machine.WithAccessTokenLifetime(TimeSpan.FromDays(2)))
            .ThrowsExactly<ArgumentOutOfRangeException>();
        await Assert
            .That(() => machine.WithRefreshTokenLifetime(TimeSpan.FromMinutes(5)))
            .ThrowsExactly<InvalidOperationException>();
        server
            .AddAuthorizationCodeClient("web")
            .WithScopes("openid", "read")
            .WithRedirectUri(server.GetEndpoint("https"), "/callback")
            .WithRefreshTokenLifetime(TimeSpan.FromMinutes(5));
        machine.WithScopes("read");
        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            ProjectionTests.Evaluate(server)
        );
        await Assert.That(error!.Message).Contains("offline_access");
    }

    [Test]
    public async Task Cors_origins_are_registered_for_browser_clients_only()
    {
        var builder = TestApp.CreateBuilder();
        var server = builder.AddIdentityServer("cors");
        server.AddApiResource("orders").WithScopes("read");
        var spa = builder.AddExecutable("spa", "dotnet", ".").WithHttpsEndpoint(name: "https");
        server
            .AddAuthorizationCodeClient("spa")
            .WithScopes("openid")
            .WithRedirectUri(spa.GetEndpoint("https"), "/")
            .WithAllowedCorsOrigin(spa.GetEndpoint("https"));
        var machine = server
            .AddClientCredentialsClient("machine")
            .WithGeneratedClientSecret()
            .WithScopes("read");
        await Assert
            .That(() => machine.WithAllowedCorsOrigin(spa.GetEndpoint("https")))
            .ThrowsExactly<InvalidOperationException>();
        var values = await ProjectionTests.Evaluate(server);
        var origin = (ReferenceExpression)
            values["IdentityServer__Clients__0__AllowedCorsOrigins__0"];
        await Assert.That(origin.ValueExpression).IsEqualTo("{spa.bindings.https.url}");
    }

    [Test]
    [Arguments("https://oauth.pstmn.io/v1/callback")]
    [Arguments("http://127.0.0.1:7890/callback")]
    [Arguments("http://localhost:7890/callback")]
    [Arguments("com.example.app:/oauth2redirect")]
    public async Task Literal_redirect_uris_accept_tools_loopback_and_native_schemes(string address)
    {
        var builder = TestApp.CreateBuilder();
        var server = builder.AddIdentityServer("literal");
        server
            .AddAuthorizationCodeClient("tool")
            .WithScopes("openid")
            .WithRedirectUri(new Uri(address))
            .WithPostLogoutRedirectUri(new Uri(address));
        var values = await ProjectionTests.Evaluate(server);
        var redirect = (ReferenceExpression)values["IdentityServer__Clients__0__RedirectUris__0"];
        await Assert.That(redirect.ValueExpression).IsEqualTo(address);
        var postLogout = (ReferenceExpression)
            values["IdentityServer__Clients__0__PostLogoutRedirectUris__0"];
        await Assert.That(postLogout.ValueExpression).IsEqualTo(address);
    }

    [Test]
    [Arguments("http://example.com/callback")]
    [Arguments("https://example.com/callback#fragment")]
    [Arguments("javascript:alert(1)")]
    [Arguments("ftp://example.com/callback")]
    public async Task Literal_redirect_uris_refuse_open_http_fragments_and_other_schemes(
        string address
    )
    {
        var server = TestApp.CreateBuilder().AddIdentityServer("refused");
        var client = server.AddAuthorizationCodeClient("tool");
        await Assert
            .That(() => client.WithRedirectUri(new Uri(address)))
            .ThrowsExactly<ArgumentException>();
    }

    [Test]
    public async Task Logout_notification_uris_reach_the_provider()
    {
        var builder = TestApp.CreateBuilder();
        var server = builder.AddIdentityServer("logout");
        var web = builder.AddExecutable("web", "dotnet", ".").WithHttpsEndpoint(name: "https");
        server
            .AddAuthorizationCodeClient("web")
            .WithScopes("openid")
            .WithRedirectUri(web.GetEndpoint("https"), "/signin")
            .WithBackChannelLogoutUri(web.GetEndpoint("https"), "/backchannel-logout")
            .WithFrontChannelLogoutUri(web.GetEndpoint("https"), "/frontchannel-logout");
        var values = await ProjectionTests.Evaluate(server);
        await Assert
            .That(
                (
                    (ReferenceExpression)values["IdentityServer__Clients__0__BackChannelLogoutUri"]
                ).ValueExpression
            )
            .IsEqualTo("{web.bindings.https.url}/backchannel-logout");
        await Assert
            .That(
                (
                    (ReferenceExpression)values["IdentityServer__Clients__0__FrontChannelLogoutUri"]
                ).ValueExpression
            )
            .IsEqualTo("{web.bindings.https.url}/frontchannel-logout");
    }

    [Test]
    public async Task Each_provider_receives_its_own_secret_signing_key_from_the_apphost()
    {
        var builder = TestApp.CreateBuilder();
        var first = builder.AddIdentityServer("first");
        var second = builder.AddIdentityServer("second");
        var firstKey = (ParameterResource)
            (await ProjectionTests.Evaluate(first))["IdentityServer__SigningKey"];
        var secondKey = (ParameterResource)
            (await ProjectionTests.Evaluate(second))["IdentityServer__SigningKey"];
        await Assert.That(firstKey.Secret).IsTrue();
        await Assert.That(firstKey.Name).IsEqualTo("first-signing-key");
        await Assert
            .That(await firstKey.GetValueAsync(default))
            .IsNotEqualTo(await secondKey.GetValueAsync(default));
    }
}
