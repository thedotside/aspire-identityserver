using System.Text.Json;
using Aspire.IdentityServer.Client;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;

var builder = WebApplication.CreateBuilder(args);
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
builder.Services.AddHttpClient();
var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.MapGet("/health", () => Results.Ok());
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

// Demonstrates outbound OAuth configuration inside an API. No credentials are returned to callers.
if (app.Environment.IsDevelopment())
{
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
            var http = factory.CreateClient();
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
