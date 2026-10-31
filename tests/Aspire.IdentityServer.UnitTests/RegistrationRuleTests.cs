using System.Security.Cryptography;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using TUnit.Assertions.Enums;

namespace Aspire.IdentityServer.Tests;

/// <summary>Every registration rule of the hosting API, without starting an AppHost.</summary>
public sealed class RegistrationRuleTests
{
    [Test]
    [Arguments("user-empty", "username")]
    [Arguments("user-duplicate", "unique within an identity provider")]
    [Arguments("subject-duplicate", "unique within an identity provider")]
    [Arguments("audience-empty", "audience")]
    [Arguments("audience-duplicate", "Duplicate audience")]
    [Arguments("client-duplicate", "Duplicate client")]
    [Arguments("api-scope-space", "single OAuth scope token")]
    [Arguments("api-scope-quote", "single OAuth scope token")]
    [Arguments("api-scope-non-ascii", "single OAuth scope token")]
    [Arguments("api-scope-openid", "reserved")]
    [Arguments("api-scope-offline", "reserved")]
    [Arguments("client-scope-backslash", "single OAuth scope token")]
    [Arguments("secret-not-secret", "Aspire secret parameter")]
    [Arguments("key-not-secret", "Aspire secret parameter")]
    [Arguments("pem-1024", "at least 2048 bits")]
    [Arguments("public-jwk-private", "public RSA JWK")]
    [Arguments("callback-relative", "absolute callback path")]
    [Arguments("callback-network", "absolute callback path")]
    [Arguments("callback-fragment", "absolute callback path")]
    [Arguments("callback-backslash", "absolute callback path")]
    public async Task Invalid_arguments_are_rejected_when_declared(string rule, string message)
    {
        var builder = TestApp.CreateBuilder();
        var idp = builder.AddIdentityServer("rules");
        var web = idp.AddAuthorizationCodeClient("web");
        using var weak = RSA.Create(1024);
        using var strong = RSA.Create(2048);
        Action declare = rule switch
        {
            "user-empty" => () => idp.WithTestUser(" ", "pwd"),
            "user-duplicate" => () => idp.WithTestUser("alice", "a").WithTestUser("alice", "b"),
            "subject-duplicate" => () =>
                idp.WithTestUser("alice", "a", subjectId: "s")
                    .WithTestUser("bob", "b", subjectId: "s"),
            "audience-empty" => () => idp.AddApiResource(""),
            "audience-duplicate" => () =>
            {
                idp.AddApiResource("orders");
                idp.AddApiResource("orders");
            },
            "client-duplicate" => () => idp.AddClientCredentialsClient("web"),
            "api-scope-space" => () => idp.AddApiResource("a").WithScopes("orders read"),
            "api-scope-quote" => () => idp.AddApiResource("a").WithScopes("orders\"read"),
            "api-scope-non-ascii" => () =>
                idp.AddApiResource("a").WithScopes("commandes.lecture-é"),
            "api-scope-openid" => () => idp.AddApiResource("a").WithScopes("openid"),
            "api-scope-offline" => () => idp.AddApiResource("a").WithScopes("offline_access"),
            "client-scope-backslash" => () => web.WithScopes("orders\\read"),
            "secret-not-secret" => () =>
                idp.AddClientCredentialsClient("m")
                    .WithClientSecret(builder.AddParameter("plain", () => "value")),
            "key-not-secret" => () =>
                idp.AddClientCredentialsClient("m")
                    .WithPrivateKeyJwt(
                        builder.AddParameter("plain-key", () => "value"),
                        TestKeys.Public(strong, "kid")
                    ),
            "pem-1024" => () =>
                idp.AddClientCredentialsClient("m")
                    .WithPrivateKeyJwt(weak.ExportPkcs8PrivateKeyPem()),
            "public-jwk-private" => () =>
                idp.AddClientCredentialsClient("m")
                    .WithPrivateKeyJwt(
                        builder.AddParameter("key", () => "value", secret: true),
                        System.Text.Encoding.UTF8.GetString(
                            Convert.FromBase64String(TestKeys.Encode(strong, "kid"))
                        )
                    ),
            "callback-relative" => () => web.WithRedirectUri(idp.GetEndpoint("https"), "signin"),
            "callback-network" => () => web.WithRedirectUri(idp.GetEndpoint("https"), "//evil/x"),
            "callback-fragment" => () => web.WithRedirectUri(idp.GetEndpoint("https"), "/cb#x"),
            "callback-backslash" => () =>
                web.WithPostLogoutRedirectUri(idp.GetEndpoint("https"), "/cb\\x"),
            _ => throw new ArgumentOutOfRangeException(nameof(rule)),
        };
        var error = await Assert.That(declare).Throws<ArgumentException>();
        await Assert.That(error!.Message).Contains(message, StringComparison.OrdinalIgnoreCase);
    }

    [Test]
    [Arguments("pkce-machine", "PKCE only applies")]
    [Arguments("redirect-machine", "Only interactive clients")]
    [Arguments("double-authentication", "already configured")]
    public async Task Invalid_operations_are_rejected_when_declared(string rule, string message)
    {
        var builder = TestApp.CreateBuilder();
        var idp = builder.AddIdentityServer("rules");
        var machine = idp.AddClientCredentialsClient("machine");
        Action declare = rule switch
        {
            "pkce-machine" => () => machine.RequirePkce(),
            "redirect-machine" => () => machine.WithRedirectUri(idp.GetEndpoint("https"), "/cb"),
            "double-authentication" => () =>
                machine.WithGeneratedClientSecret().WithGeneratedPrivateKeyJwt(),
            _ => throw new ArgumentOutOfRangeException(nameof(rule)),
        };
        var error = await Assert.That(declare).ThrowsExactly<InvalidOperationException>();
        await Assert.That(error!.Message).Contains(message);
    }

    [Test]
    [Arguments("api-without-scope", "needs at least one scope")]
    [Arguments("client-without-scope", "must use declared API scopes")]
    [Arguments("client-undeclared-scope", "must use declared API scopes")]
    [Arguments("machine-without-authentication", "requires authentication and API scopes only")]
    [Arguments("machine-identity-scope", "requires authentication and API scopes only")]
    [Arguments("interactive-without-redirect", "needs an openid scope and a redirect URI")]
    [Arguments("interactive-without-openid", "needs an openid scope and a redirect URI")]
    [Arguments("public-without-pkce", "'public' must use PKCE")]
    public async Task Inconsistent_registrations_fail_before_any_resource_starts(
        string rule,
        string message
    )
    {
        var builder = TestApp.CreateBuilder();
        var idp = builder.AddIdentityServer("rules");
        idp.AddApiResource("orders").WithScopes("orders.read");
        var callback = idp.GetEndpoint("https");
        switch (rule)
        {
            case "api-without-scope":
                idp.AddApiResource("empty");
                break;
            case "client-without-scope":
                idp.AddClientCredentialsClient("m").WithGeneratedClientSecret();
                break;
            case "client-undeclared-scope":
                idp.AddClientCredentialsClient("m").WithGeneratedClientSecret().WithScopes("x");
                break;
            case "machine-without-authentication":
                idp.AddClientCredentialsClient("m").WithScopes("orders.read");
                break;
            case "machine-identity-scope":
                idp.AddClientCredentialsClient("m")
                    .WithGeneratedClientSecret()
                    .WithScopes("orders.read", "profile");
                break;
            case "interactive-without-redirect":
                idp.AddAuthorizationCodeClient("web").WithScopes("openid");
                break;
            case "interactive-without-openid":
                idp.AddAuthorizationCodeClient("web")
                    .WithScopes("orders.read")
                    .WithRedirectUri(callback, "/cb");
                break;
            case "public-without-pkce":
                idp.AddAuthorizationCodeClient("public")
                    .RequirePkce(false)
                    .WithScopes("openid")
                    .WithRedirectUri(callback, "/cb");
                break;
        }
        var error = await Assert
            .That(() => IdentityServerExtensions.Validate(idp.Resource))
            .ThrowsExactly<InvalidOperationException>();
        await Assert.That(error!.Message).Contains(message);
    }

    [Test]
    public async Task A_confidential_client_may_disable_pkce()
    {
        var idp = TestApp.CreateBuilder().AddIdentityServer("rules");
        idp.AddAuthorizationCodeClient("web")
            .WithGeneratedClientSecret()
            .RequirePkce(false)
            .WithScopes("openid")
            .WithRedirectUri(idp.GetEndpoint("https"), "/cb");
        await Assert.That(() => IdentityServerExtensions.Validate(idp.Resource)).ThrowsNothing();
    }

    [Test]
    public async Task Publish_mode_refuses_the_provider_and_points_to_run_mode()
    {
        var builder = DistributedApplication.CreateBuilder(
            new DistributedApplicationOptions
            {
                DisableDashboard = true,
                Args = ["--environment", "Development", "--operation", "publish"],
            }
        );
        await Assert.That(builder.ExecutionContext.IsPublishMode).IsTrue();
        var error = await Assert
            .That(() => builder.AddIdentityServer("idp"))
            .ThrowsExactly<InvalidOperationException>();
        await Assert.That(error!.Message).Contains("IsRunMode");
    }

    [Test]
    public async Task The_provider_environment_matches_its_configuration_contract()
    {
        var builder = TestApp.CreateBuilder();
        var idp = builder.AddIdentityServer("contract").WithTestUser("alice", "alice", "Alice");
        var orders = idp.AddApiResource("orders").WithScopes("orders.read", "orders.write");
        var web = idp.AddAuthorizationCodeClient("web")
            .WithGeneratedClientSecret()
            .WithScopes("openid", "orders.read")
            .WithRedirectUri(idp.GetEndpoint("https"), "/signin")
            .WithPostLogoutRedirectUri(idp.GetEndpoint("https"), "/signout");
        idp.AddClientCredentialsClient("worker")
            .WithGeneratedBase64PrivateKeyJwt()
            .WithScopes("orders.read")
            .WithTokenExchangeFrom(web, orders, "name");
        var values = await ProjectionTests.Evaluate(idp);
        var snapshot = values
            .Where(p =>
                p.Key.StartsWith("IdentityServer__", StringComparison.Ordinal)
                || p.Key.StartsWith("ASPNETCORE_", StringComparison.Ordinal)
            )
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p =>
                $"{p.Key}={p.Value switch
                {
                    string text when p.Key.EndsWith("__PublicJwk", StringComparison.Ordinal) => "<public-jwk>",
                    string text => text,
                    EndpointReference => "<endpoint>",
                    EndpointReferenceExpression => "<port>",
                    ReferenceExpression => "<expression>",
                    ParameterResource { Secret: true } => "<secret-parameter>",
                    IValueProvider => "<secret-hash>",
                    _ => p.Value.GetType().Name,
                }}"
            )
            .ToArray();
        string[] expected =
        [
            "ASPNETCORE_ENVIRONMENT=Development",
            "ASPNETCORE_HTTPS_PORTS=<port>",
            "IdentityServer__AcceptTokenEndpointAudience=false",
            "IdentityServer__Apis__0__Audience=orders",
            "IdentityServer__Apis__0__Scopes__0=orders.read",
            "IdentityServer__Apis__0__Scopes__1=orders.write",
            "IdentityServer__Clients__0__AuthenticationMethod=client_secret_post",
            "IdentityServer__Clients__0__ClientId=web",
            "IdentityServer__Clients__0__ClientSecretHash=<secret-hash>",
            "IdentityServer__Clients__0__GrantType=authorization_code",
            "IdentityServer__Clients__0__PostLogoutRedirectUris__0=<expression>",
            "IdentityServer__Clients__0__RedirectUris__0=<expression>",
            "IdentityServer__Clients__0__RequirePkce=True",
            "IdentityServer__Clients__0__Scopes__0=openid",
            "IdentityServer__Clients__0__Scopes__1=orders.read",
            "IdentityServer__Clients__1__AuthenticationMethod=private_key_jwt",
            "IdentityServer__Clients__1__ClientId=worker",
            "IdentityServer__Clients__1__GrantType=client_credentials",
            "IdentityServer__Clients__1__PublicJwk=<public-jwk>",
            "IdentityServer__Clients__1__RequirePkce=True",
            "IdentityServer__Clients__1__Scopes__0=orders.read",
            "IdentityServer__Clients__1__TokenExchanges__0__Audience=orders",
            "IdentityServer__Clients__1__TokenExchanges__0__SubjectClientId=web",
            "IdentityServer__Clients__1__TokenExchanges__0__UserClaims__0=name",
            "IdentityServer__ContractVersion=2",
            "IdentityServer__Issuer=<endpoint>",
            "IdentityServer__Name=contract",
            "IdentityServer__Users__0__DisplayName=Alice",
            "IdentityServer__Users__0__Password=<secret-parameter>",
            "IdentityServer__Users__0__SubjectId=contract:alice",
            "IdentityServer__Users__0__Username=alice",
        ];
        // Any change of this list is a contract change: increment RuntimeContract.Version.
        await Assert.That(snapshot).IsEquivalentTo(expected, CollectionOrdering.Matching);
    }
}
