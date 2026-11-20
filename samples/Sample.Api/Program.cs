using System.Text.Json;
using Aspire.IdentityServer.Client;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
var auth = builder.Services.AddAuthentication();
foreach (var (scheme, section) in new[] { ("a", "IdpA"), ("b", "IdpB") })
{
    auth.AddJwtBearer(
        scheme,
        options =>
        {
            options.Authority = builder.Configuration[$"Authentication:{section}:Authority"];
            options.Audience = builder.Configuration[$"Authentication:{section}:Audience"];
            options.MapInboundClaims = false;
        }
    );
}
builder.Services.AddAuthorization();

// A token request carries a one-time assertion: a retried copy would be refused as a replay.
// The opt-out from ServiceDefaults resilience is still experimental in Microsoft.Extensions.
#pragma warning disable EXTEXP0001
builder.Services.AddHttpClient("oauth").RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001

// The handler caches the client-credentials token and signs a new assertion for each attempt:
// the resilience handlers of ServiceDefaults can stay on this client.
builder
    .Services.AddHttpClient("accounting")
    .AddClientCredentialsHandler(builder.Configuration.GetSection("Integrations:Accounting:OAuth"));
var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.MapDefaultEndpoints();
foreach (var scheme in new[] { "a", "b" })
{
    app.MapGet(
            $"/orders/{scheme}",
            (HttpContext context) =>
                new
                {
                    issuer = context.User.FindFirst("iss")?.Value,
                    audience = context.User.FindFirst("aud")?.Value,
                    client = context.User.FindFirst("client_id")?.Value,
                    subject = context.User.FindFirst("sub")?.Value,
                    name = context.User.FindFirst("name")?.Value,
                    actor = context.User.FindFirst("act")?.Value,
                    orders = new[] { "order-001" },
                }
        )
        .RequireAuthorization(
            new AuthorizationPolicyBuilder(scheme)
                .RequireAuthenticatedUser()
                .RequireAssertion(ctx =>
                    ctx.User.FindAll("scope").Any(c => c.Value.Split(' ').Contains("orders.read"))
                )
                .Build()
        );
}

// Direct relay: a signed user access token must also carry the required membership.
app.MapGet("/memberships/a", () => Results.Ok(new { authorized = true }))
    .RequireAuthorization(
        new AuthorizationPolicyBuilder("a")
            .RequireAuthenticatedUser()
            .RequireClaim("member_of", "product.readers")
            .Build()
    );

// Demonstrates outbound OAuth configuration inside an API. No credentials are returned to callers.
if (app.Environment.IsDevelopment())
{
    // The API calls itself as a partner API, with the cached token of its accounting client.
    app.MapGet(
        "/outbound/accounting/orders",
        async (
            HttpContext context,
            IHttpClientFactory factory,
            CancellationToken cancellationToken
        ) =>
        {
            var partner = new Uri($"{context.Request.Scheme}://{context.Request.Host}/orders/a");
            var orders = await factory
                .CreateClient("accounting")
                .GetFromJsonAsync<JsonElement>(partner, cancellationToken);
            return Results.Json(orders);
        }
    );
    app.MapGet(
        "/outbound/{provider}",
        async (
            string provider,
            IConfiguration configuration,
            IHttpClientFactory factory,
            CancellationToken cancellationToken
        ) =>
        {
            IdentityClientOptions client;
            if (string.Equals(provider, "a", StringComparison.Ordinal))
                client = configuration
                    .GetSection("Integrations:Accounting:OAuth")
                    .Get<IdentityClientOptions>()!;
            else if (string.Equals(provider, "b", StringComparison.Ordinal))
                client = new IdentityClientOptions
                {
                    Authority = configuration["Warehouse:OAuth:Issuer"]!,
                    ClientId = configuration["Warehouse:OAuth:Client"]!,
                    ClientSecret = configuration["Warehouse:OAuth:Secret"],
                    Scopes = configuration["Warehouse:OAuth:Scope"]!,
                    AuthenticationMethod = "client_secret_post",
                };
            else
                return Results.NotFound();
            var http = factory.CreateClient("oauth");
            var metadata = await http.GetFromJsonAsync<JsonElement>(
                $"{client.Authority}/.well-known/openid-configuration",
                cancellationToken
            );
            var tokenEndpoint = metadata.GetProperty("token_endpoint").GetString()!;
            var issuer = metadata.GetProperty("issuer").GetString()!;
            var fields = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["client_id"] = client.ClientId,
                ["grant_type"] = "client_credentials",
                ["scope"] = client.Scopes,
            };
            if (
                string.Equals(
                    client.AuthenticationMethod,
                    "private_key_jwt",
                    StringComparison.Ordinal
                )
            )
            {
                fields["client_assertion_type"] = PrivateKeyJwt.AssertionType;
                fields["client_assertion"] = PrivateKeyJwt.CreateAssertion(client, issuer);
            }
            else
                fields["client_secret"] = client.ClientSecret!;
            using var response = await http.PostAsync(
                tokenEndpoint,
                new FormUrlEncodedContent(fields),
                cancellationToken
            );
            response.EnsureSuccessStatusCode();
            var token = (await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken))
                .GetProperty("access_token")
                .GetString();
            // Return only evidence of successful token acquisition; validation is exercised at /orders by tests.
            return Results.Ok(new { provider, tokenAcquired = !string.IsNullOrEmpty(token) });
        }
    );
}
app.Run();
