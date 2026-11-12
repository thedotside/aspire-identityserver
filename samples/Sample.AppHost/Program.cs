var builder = DistributedApplication.CreateBuilder(args);

#if NET11_0_OR_GREATER
const string targetFramework = "net11.0";
#else
const string targetFramework = "net10.0";
#endif

// The teams identity scope gives clients that request it the team claims of the user.
var idpA = builder
    .AddIdentityServer("idp-a")
    .WithIdentityScope("teams", "team")
    .WithIdentityScope("communities", "member_of");
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

// Aspire 13.6 does not infer a target framework for multi-target project launches.
// This sample-only annotation selects the same framework as the running AppHost.
#pragma warning disable ASPIREPROJECTS001
var web = builder
    .AddProject<Projects.Sample_Web>("web")
    .WithAnnotation(
        new ProjectLaunchArgsOverrideAnnotation([
            "run",
            "--framework",
            targetFramework,
            "--no-build",
            "--no-launch-profile",
            "--project",
        ])
    )
    .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
    .WithHttpsEndpoint()
    .WithHttpHealthCheck("/health", endpointName: "https");
#pragma warning restore ASPIREPROJECTS001
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
    .WithPostLogoutRedirectUri(web.GetEndpoint("https"), "/signout-callback-a");
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

// The machine client keeps client_credentials and can additionally act for this local user.
workerA.WithTokenExchangeFrom(browserA, ordersA, userClaims: ["name"]);
var deviceWithSecret = idpA.AddClientCredentialsClient("device-secret")
    .WithGeneratedClientSecret()
    .WithScopes("orders.read", "orders.write")
    .WithTokenExchangeFrom(browserA, ordersA);

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

#pragma warning disable ASPIREPROJECTS001
var api = builder
    .AddProject<Projects.Sample_Api>("api")
    .WithAnnotation(
        new ProjectLaunchArgsOverrideAnnotation([
            "run",
            "--framework",
            targetFramework,
            "--no-build",
            "--no-launch-profile",
            "--project",
        ])
    )
    .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
    .WithHttpsEndpoint()
    .WithHttpHealthCheck("/health", endpointName: "https")
    .WithIdentityApiResource(ordersA, "Authentication:IdpA")
    .WithIdentityApiResource(ordersB, "Authentication:IdpB")
    .WithIdentityClient(workerA, "Integrations:Accounting:OAuth")
    .WithIdentityClient(
        workerB,
        projection =>
            projection
                .Authority("Warehouse:OAuth:Issuer")
                .ClientId("Warehouse:OAuth:Client")
                .ClientSecret("Warehouse:OAuth:Secret")
                .Scopes("Warehouse:OAuth:Scope")
    );
#pragma warning restore ASPIREPROJECTS001

web.WithReference(api);

builder.Build().Run();
