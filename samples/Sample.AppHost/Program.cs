using Aspire.IdentityServer.Hosting;

var builder = DistributedApplication.CreateBuilder(args);

var idpA = builder
    .AddIdentityServer("idp-a")
    .WithTestUser("alice", "alice", "Alice A")
    .WithTestUser("bob", "bob", "Bob A");
var idpB = builder.AddIdentityServer("idp-b").WithTestUser("alice", "alice", "Alice B");

var ordersA = idpA.AddApiResource("orders-api-a").WithScopes("orders.read", "orders.write");
var ordersB = idpB.AddApiResource("orders-api-b").WithScopes("orders.read");
var workerA = idpA.AddClientCredentialsClient("worker")
    .WithGeneratedBase64PrivateKeyJwt()
    .WithScopes("orders.read");
var workerB = idpB.AddClientCredentialsClient("worker")
    .WithGeneratedClientSecret()
    .WithScopes("orders.read");

var web = builder
    .AddProject<Projects.Sample_Web>("web")
    .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
    .WithHttpsEndpoint()
    .WithHttpHealthCheck("/health", endpointName: "https");
var browserA = idpA.AddAuthorizationCodeClient("web")
    .WithGeneratedBase64PrivateKeyJwt()
    .RequirePkce()
    .WithScopes("openid", "profile", "orders.read")
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

web.WithIdentityClient(workerA, "TokenExchange:PrivateKey")
    .WithIdentityClient(deviceWithSecret, "TokenExchange:Secret");

web.WithIdentityClient(browserA, "Authentication:IdpA")
    .WithIdentityClient(browserB, "Authentication:IdpB")
    .WithIdentityClient(publicClient, "Authentication:Public");

var api = builder
    .AddProject<Projects.Sample_Api>("api")
    .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
    .WithHttpsEndpoint()
    .WithHttpHealthCheck("/health", endpointName: "https")
    .WithIdentityResource(ordersA, "Authentication:IdpA")
    .WithIdentityResource(ordersB, "Authentication:IdpB")
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

web.WithReference(api);

builder.Build().Run();
