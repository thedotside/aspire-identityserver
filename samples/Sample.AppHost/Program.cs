var builder = DistributedApplication.CreateBuilder(args);

#if NET11_0_OR_GREATER
const string targetFramework = "net11.0";
#else
const string targetFramework = "net10.0";
#endif

// The teams identity scope gives clients that request it the team claims of the user.
// Requesting urn:sample:loa:mfa or urn:sample:loa:high in acr_values asks for the simulated
// second factor 111111 after the password.
var idpA = builder
    .AddIdentityServer("idp-a")
    .WithIdentityScope("teams", "team")
    .WithIdentityScope("communities", "member_of")
    .WithSimulatedMfa("111111", "urn:sample:loa:mfa", "urn:sample:loa:high");
idpA.AddTestUser("alice", "alice", "Alice A")
    .WithClaim("email", "alice@example.test")
    .WithClaim("given_name", "Alice")
    .WithClaim("family_name", "Anderson")
    .WithClaim("team", "sales", "support")
    .WithClaim("member_of", "product.readers", "product.contributors");
idpA.WithTestUser("bob", "bob", "Bob A");
var idpB = builder.AddIdentityServer("idp-b").WithTestUser("alice", "alice", "Alice B");

// communities remains an identity scope of the interactive client, never an API scope.
var ordersA = idpA.AddApiResource("orders-api-a").WithScopes("orders.read", "orders.write");
var ordersB = idpB.AddApiResource("orders-api-b").WithScopes("orders.read");
var workerA = idpA.AddClientCredentialsClient("worker")
    .WithGeneratedBase64PrivateKeyJwt()
    .WithScopes("orders.read");
var workerB = idpB.AddClientCredentialsClient("worker")
    .WithGeneratedClientSecret()
    .WithScopes("orders.read");

// Build and run each service for the same framework as the AppHost.
var web = builder
    .AddDotnetProject("web", "../Sample.Web/Sample.Web.csproj", options =>
        options.ExcludeLaunchProfile = true
    )
    .WithBuildEnvironment("TargetFramework", targetFramework)
    .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
    .WithHttpsEndpoint()
    .WithHttpHealthCheck("/health", endpointName: "https");
var browserA = idpA.AddAuthorizationCodeClient("web")
    .WithGeneratedBase64PrivateKeyJwt()
    .RequirePkce()
    .WithScopes(
        "openid",
        "profile",
        "email",
        "teams",
        "communities",
        "offline_access",
        "orders.read"
    )
    .WithRedirectUri(web.GetEndpoint("https"), "/signin-a")
    .WithPostLogoutRedirectUri(web.GetEndpoint("https"), "/signout-callback-a")
    // The "par" scheme of the web sample: the same client, through pushed authorization requests.
    .WithRedirectUri(web.GetEndpoint("https"), "/signin-par")
    .WithPostLogoutRedirectUri(web.GetEndpoint("https"), "/signout-callback-par")
    // The "mfa" scheme of the web sample: the same client, asking for the urn:sample:loa:mfa level.
    .WithRedirectUri(web.GetEndpoint("https"), "/signin-mfa")
    .WithPostLogoutRedirectUri(web.GetEndpoint("https"), "/signout-callback-mfa")
    // The web sample records the logout tokens posted when a provider session ends.
    .WithBackChannelLogoutUri(web.GetEndpoint("https"), "/backchannel-logout-a");
var browserB = idpB.AddAuthorizationCodeClient("web")
    .WithGeneratedClientSecret()
    .RequirePkce()
    .WithScopes("openid", "profile", "orders.read")
    .WithRedirectUri(web.GetEndpoint("https"), "/signin-b")
    .WithPostLogoutRedirectUri(web.GetEndpoint("https"), "/signout-callback-b");
var publicClient = idpA.AddAuthorizationCodeClient("public-web")
    .RequirePkce()
    .WithScopes("openid", "profile", "orders.read")
    .WithRedirectUri(web.GetEndpoint("https"), "/signin-public")
    .WithPostLogoutRedirectUri(web.GetEndpoint("https"), "/signout-callback-public");

// A single-page application served by the web sample: a public client with PKCE, whose browser
// code redeems the code and calls UserInfo across origins.
var spa = idpA.AddAuthorizationCodeClient("spa")
    .RequirePkce()
    .WithScopes("openid", "profile")
    .WithRedirectUri(web.GetEndpoint("https"), "/spa/")
    .WithAllowedCorsOrigin(web.GetEndpoint("https"));
web.WithIdentityClient(spa, "Spa");

// The machine client keeps client_credentials and can additionally act for this local user.
workerA.WithTokenExchangeFrom(browserA, ordersA, userClaims: ["name"]);
var deviceWithSecret = idpA.AddClientCredentialsClient("device-secret")
    .WithGeneratedClientSecret()
    .WithScopes("orders.read", "orders.write")
    .WithTokenExchangeFrom(browserA, ordersA);

// Two providers: provider A's partner exchanges the access tokens of provider B's web users for
// provider A's API. The trust is explicit and directional: provider B trusts nothing of A.
idpA.AddClientCredentialsClient("partner")
    .WithGeneratedClientSecret()
    .WithScopes("orders.read")
    .WithTokenExchangeFrom(browserB, ordersA, userClaims: ["name"]);

// Delegation: this machine client chooses, for each user, API scopes that the user's token
// does not carry, and forwards the selected profile claims.
idpA.AddClientCredentialsClient("delegate")
    .WithGeneratedClientSecret()
    .WithScopes("orders.read", "orders.write")
    .WithTokenExchangeFrom(
        browserA,
        ordersA,
        TokenExchangeScopes.FromMachineClient,
        userClaims: ["email", "team", "member_of"]
    );

web.WithIdentityClient(workerA, "TokenExchange:PrivateKey")
    .WithIdentityClient(deviceWithSecret, "TokenExchange:Secret");

web.WithIdentityClient(browserA, "Authentication:IdpA")
    .WithIdentityClient(browserB, "Authentication:IdpB")
    .WithIdentityClient(publicClient, "Authentication:Public");

var api = builder
    .AddDotnetProject("api", "../Sample.Api/Sample.Api.csproj", options =>
        options.ExcludeLaunchProfile = true
    )
    .WithBuildEnvironment("TargetFramework", targetFramework)
    .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
    .WithHttpsEndpoint()
    .WithHttpHealthCheck("/health", endpointName: "https")
    .WithIdentityApiResource(ordersA, "Authentication:IdpA")
    .WithIdentityApiResource(ordersB, "Authentication:IdpB")
    .WithIdentityClient(workerA, "Integrations:Accounting:OAuth")
    .WithIdentityClient(workerB, projection =>
        projection
            .Authority("Warehouse:OAuth:Issuer")
            .ClientId("Warehouse:OAuth:Client")
            .ClientSecret("Warehouse:OAuth:Secret")
            .Scopes("Warehouse:OAuth:Scope")
    );

web.WithReference(api);

builder.Build().Run();
