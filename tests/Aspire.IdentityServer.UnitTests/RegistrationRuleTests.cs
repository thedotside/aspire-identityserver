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
    [Arguments("user-claim-protocol", "profile claim")]
    [Arguments("user-claim-name", "display name")]
    [Arguments("user-claim-empty", "non-empty claim value")]
    [Arguments("identity-scope-builtin", "built-in scope")]
    [Arguments("identity-scope-offline", "built-in scope")]
    [Arguments("identity-scope-space", "single OAuth scope token")]
    [Arguments("identity-scope-duplicate", "Duplicate identity scope")]
    [Arguments("identity-scope-without-claim", "at least one user claim")]
    [Arguments("identity-scope-protocol-claim", "protocol claims")]
    [Arguments("exchange-scopes-undefined", "scopes")]
    [Arguments("mfa-code-short", "4 to 10 digits")]
    [Arguments("mfa-code-letters", "4 to 10 digits")]
    [Arguments("mfa-no-level", "at least one acr value")]
    [Arguments("mfa-level-space", "single acr value")]
    [Arguments("mfa-level-idp", "reserved")]
    [Arguments("mfa-level-tenant", "reserved")]
    [Arguments("mfa-level-duplicate", "Duplicate acr value")]
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
            "user-claim-protocol" => () => idp.AddTestUser("alice", "a").WithClaim("sub", "x"),
            "user-claim-name" => () => idp.AddTestUser("alice", "a").WithClaim("name", "Alice"),
            "user-claim-empty" => () => idp.AddTestUser("alice", "a").WithClaim("email", " "),
            "identity-scope-builtin" => () => idp.WithIdentityScope("profile", "team"),
            "identity-scope-offline" => () => idp.WithIdentityScope("offline_access", "team"),
            "identity-scope-space" => () => idp.WithIdentityScope("my teams", "team"),
            "identity-scope-duplicate" => () =>
                idp.WithIdentityScope("teams", "team").WithIdentityScope("teams", "group"),
            "identity-scope-without-claim" => () => idp.WithIdentityScope("teams"),
            "identity-scope-protocol-claim" => () => idp.WithIdentityScope("teams", "sub"),
            "exchange-scopes-undefined" => () =>
                idp.AddClientCredentialsClient("m")
                    .WithTokenExchangeFrom(
                        web,
                        idp.AddApiResource("orders"),
                        (TokenExchangeScopes)42
                    ),
            "mfa-code-short" => () => idp.WithSimulatedMfa("123", "urn:test:mfa"),
            "mfa-code-letters" => () => idp.WithSimulatedMfa("12ab56", "urn:test:mfa"),
            "mfa-no-level" => () => idp.WithSimulatedMfa("111111"),
            "mfa-level-space" => () => idp.WithSimulatedMfa("111111", "urn:a b"),
            "mfa-level-idp" => () => idp.WithSimulatedMfa("111111", "idp:x"),
            "mfa-level-tenant" => () => idp.WithSimulatedMfa("111111", "tenant:x"),
            "mfa-level-duplicate" => () =>
                idp.WithSimulatedMfa("111111", "urn:test:mfa", "urn:test:mfa"),
            _ => throw new ArgumentOutOfRangeException(nameof(rule)),
        };
        var error = await Assert.That(declare).Throws<ArgumentException>();
        await Assert.That(error!.Message).Contains(message, StringComparison.OrdinalIgnoreCase);
    }

    [Test]
    [Arguments("pkce-machine", "PKCE only applies")]
    [Arguments("redirect-machine", "Only interactive clients")]
    [Arguments("double-authentication", "already configured")]
    [Arguments("mfa-twice", "already declares a simulated MFA")]
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
            "mfa-twice" => () =>
                idp.WithSimulatedMfa("111111", "urn:test:mfa")
                    .WithSimulatedMfa("222222", "urn:test:high"),
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
    [Arguments("identity-scope-is-api-scope", "both an identity scope and an API scope")]
    [Arguments("client-undeclared-identity-scope", "must use declared API scopes")]
    [Arguments("machine-offline-access", "requires authentication and API scopes only")]
    [Arguments("machine-custom-identity-scope", "requires authentication and API scopes only")]
    [Arguments("delegation-without-machine-scope", "allowed to this machine client")]
    [Arguments("narrowing-without-shared-scope", "shared with 'web'")]
    public async Task Inconsistent_registrations_are_refused_by_the_startup_validation(
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
            case "identity-scope-is-api-scope":
                idp.WithIdentityScope("orders.read", "team");
                break;
            case "client-undeclared-identity-scope":
                idp.AddAuthorizationCodeClient("web")
                    .WithScopes("openid", "teams")
                    .WithRedirectUri(callback, "/cb");
                break;
            case "machine-offline-access":
                idp.AddClientCredentialsClient("m")
                    .WithGeneratedClientSecret()
                    .WithScopes("orders.read", "offline_access");
                break;
            case "machine-custom-identity-scope":
                idp.WithIdentityScope("teams", "team");
                idp.AddClientCredentialsClient("m")
                    .WithGeneratedClientSecret()
                    .WithScopes("orders.read", "teams");
                break;
            case "delegation-without-machine-scope":
            case "narrowing-without-shared-scope":
                idp.AddApiResource("other").WithScopes("other.read");
                var interactive = idp.AddAuthorizationCodeClient("web")
                    .WithScopes("openid")
                    .WithRedirectUri(callback, "/cb");
                var orders = idp.AddApiResource("delegated").WithScopes("delegated.read");
                idp.AddClientCredentialsClient("m")
                    .WithGeneratedClientSecret()
                    .WithScopes("other.read")
                    .WithTokenExchangeFrom(
                        interactive,
                        orders,
                        string.Equals(
                            rule,
                            "delegation-without-machine-scope",
                            StringComparison.Ordinal
                        )
                            ? TokenExchangeScopes.FromMachineClient
                            : TokenExchangeScopes.FromSubjectToken
                    );
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
    public async Task An_interactive_client_may_renew_its_session_and_request_custom_profile_scopes()
    {
        var idp = TestApp
            .CreateBuilder()
            .AddIdentityServer("rules")
            .WithIdentityScope("teams", "team");
        var orders = idp.AddApiResource("orders").WithScopes("orders.read", "orders.admin");
        var web = idp.AddAuthorizationCodeClient("web")
            .WithGeneratedClientSecret()
            .WithScopes("openid", "profile", "email", "teams", "offline_access")
            .WithRedirectUri(idp.GetEndpoint("https"), "/cb");
        // Delegation does not need the user's token to carry any API scope.
        idp.AddClientCredentialsClient("orders-worker")
            .WithGeneratedClientSecret()
            .WithScopes("orders.read", "orders.admin")
            .WithTokenExchangeFrom(web, orders, TokenExchangeScopes.FromMachineClient, "email");
        await Assert.That(() => IdentityServerExtensions.Validate(idp.Resource)).ThrowsNothing();
    }

    [Test]
    public async Task Test_users_keep_their_claims_in_order_without_duplicates()
    {
        var idp = TestApp.CreateBuilder().AddIdentityServer("rules");
        var alice = idp.AddTestUser("alice", "alice", "Alice")
            .WithClaim("team", "sales", "support")
            .WithClaim("email", "alice@example.test")
            .WithClaim("team", "support");
        await Assert.That(alice.SubjectId).IsEqualTo("rules:alice");
        await Assert.That(alice.DisplayName).IsEqualTo("Alice");
        await Assert
            .That(alice.Claims.Select(c => $"{c.Key}={c.Value}").ToArray())
            .IsEquivalentTo(
                ["team=sales", "team=support", "email=alice@example.test"],
                CollectionOrdering.Matching
            );
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
    public async Task Communities_is_projected_as_an_identity_scope_and_cannot_be_an_api_scope()
    {
        var idp = TestApp
            .CreateBuilder()
            .AddIdentityServer("communities")
            .WithIdentityScope("communities", "member_of");
        var xp = idp.AddApiResource("xp").WithScopes("xp");
        idp.AddAuthorizationCodeClient("bff")
            .WithGeneratedClientSecret()
            .WithScopes("openid", "communities", "xp")
            .WithRedirectUri(idp.GetEndpoint("https"), "/signin-oidc");
        idp.AddClientCredentialsClient("technical").WithGeneratedClientSecret().WithScopes("xp");
        var environment = await ProjectionTests.Evaluate(idp);
        await Assert
            .That(environment["IdentityServer__IdentityScopes__0__Name"])
            .IsEqualTo("communities");
        await Assert
            .That(environment["IdentityServer__IdentityScopes__0__UserClaims__0"])
            .IsEqualTo("member_of");
        await Assert.That(environment["IdentityServer__Apis__0__Scopes__0"]).IsEqualTo("xp");
        await Assert.That(environment.ContainsKey("IdentityServer__Apis__0__Scopes__1")).IsFalse();
        xp.WithScopes("communities");
        var error = await Assert
            .That(() => IdentityServerExtensions.Validate(idp.Resource))
            .ThrowsExactly<InvalidOperationException>();
        await Assert.That(error!.Message).Contains("both an identity scope and an API scope");
    }

    [Test]
    public async Task The_provider_environment_matches_its_configuration_contract()
    {
        var builder = TestApp.CreateBuilder();
        var idp = builder
            .AddIdentityServer("contract")
            .WithIdentityScope("teams", "team")
            .WithSimulatedMfa("111111", "urn:test:mfa");
        idp.AddTestUser("alice", "alice", "Alice").WithClaim("team", "sales", "support");
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
            "IdentityServer__Clients__1__TokenExchanges__0__Scopes=FromSubjectToken",
            "IdentityServer__Clients__1__TokenExchanges__0__SubjectClientId=web",
            "IdentityServer__Clients__1__TokenExchanges__0__UserClaims__0=name",
            "IdentityServer__ContractVersion=5",
            "IdentityServer__IdentityScopes__0__Name=teams",
            "IdentityServer__IdentityScopes__0__UserClaims__0=team",
            "IdentityServer__Issuer=<endpoint>",
            "IdentityServer__Name=contract",
            "IdentityServer__SigningKey=<secret-parameter>",
            "IdentityServer__SimulatedMfa__AcrValues__0=urn:test:mfa",
            "IdentityServer__SimulatedMfa__Code=111111",
            "IdentityServer__Users__0__Claims__0__Type=team",
            "IdentityServer__Users__0__Claims__0__Value=sales",
            "IdentityServer__Users__0__Claims__1__Type=team",
            "IdentityServer__Users__0__Claims__1__Value=support",
            "IdentityServer__Users__0__DisplayName=Alice",
            "IdentityServer__Users__0__Password=<secret-parameter>",
            "IdentityServer__Users__0__SubjectId=contract:alice",
            "IdentityServer__Users__0__Username=alice",
        ];
        // Any change of this list is a contract change: increment RuntimeContract.Version.
        await Assert.That(snapshot).IsEquivalentTo(expected, CollectionOrdering.Matching);
    }
}
