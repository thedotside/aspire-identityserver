using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.IdentityServer.Hosting;

public static class IdentityServerExtensions
{
    /// <summary>Adds the bundled local development identity server, with an isolated issuer and signing key.</summary>
    public static IResourceBuilder<IdentityServerResource> AddIdentityServer(
        this IDistributedApplicationBuilder builder, string name, Action<IdentityServerOptions>? configure = null)
    {
        if (builder.ExecutionContext.IsPublishMode)
            throw new InvalidOperationException("Aspire IdentityServer is a local development/testing resource and cannot be deployed.");
        var options = new IdentityServerOptions();
        configure?.Invoke(options);
        var directory = Path.GetFullPath(options.RuntimeDirectory, builder.AppHostDirectory);
        var assembly = Path.Combine(directory, "Aspire.IdentityServer.Server.dll");
        if (!File.Exists(assembly))
            throw new FileNotFoundException("The bundled identity server is missing. Build your AppHost, or publish the server and set RuntimeDirectory when developing this repository.", assembly);
        // Aspire's certificate mapping API is experimental; keep its use inside this integration.
#pragma warning disable ASPIRECERTIFICATES001
        var server = builder.AddResource(new IdentityServerResource(name, directory))
            .WithArgs(assembly)
            .WithHttpsEndpoint(env: "ASPNETCORE_HTTPS_PORTS")
            .WithHttpsCertificateConfiguration(context =>
            {
                context.EnvironmentVariables["Kestrel__Certificates__Default__Path"] = context.PfxPath;
                if (context.Password is not null)
                    context.EnvironmentVariables["Kestrel__Certificates__Default__Password"] = context.Password;
                return Task.CompletedTask;
            })
            .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
            .WithEnvironment("IdentityServer__Name", name)
            .WithHttpHealthCheck("/health", endpointName: "https");
#pragma warning restore ASPIRECERTIFICATES001
        server.WithEnvironment("IdentityServer__Issuer", server.GetEndpoint("https"));
        server.WithEnvironment(context => WriteConfiguration(server.Resource, context.EnvironmentVariables));
        return server;
    }

    public static IResourceBuilder<IdentityServerResource> WithTestUser(this IResourceBuilder<IdentityServerResource> server,
        string username, string password, string? displayName = null, string? subjectId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        subjectId ??= $"{server.Resource.Name}:{username}";
        if (server.Resource.Users.Any(u => u.Username == username || u.SubjectId == subjectId))
            throw new ArgumentException("Test usernames and subject IDs must be unique within an identity provider.");
        var secret = server.ApplicationBuilder.AddParameter($"{server.Resource.Name}-user-{server.Resource.Users.Count}-password", () => password, secret: true);
        server.Resource.Users.Add(new TestUser(username, subjectId, displayName ?? username, secret));
        return server;
    }

    public static IdentityApiResource AddApiResource(this IResourceBuilder<IdentityServerResource> server, string audience)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(audience);
        if (server.Resource.Apis.Any(a => a.Audience == audience)) throw new ArgumentException($"Duplicate audience '{audience}'.");
        var api = new IdentityApiResource(server, audience);
        server.Resource.Apis.Add(api);
        return api;
    }

    public static IdentityClient AddAuthorizationCodeClient(this IResourceBuilder<IdentityServerResource> server, string clientId)
        => AddClient(server, clientId, "authorization_code");

    public static IdentityClient AddClientCredentialsClient(this IResourceBuilder<IdentityServerResource> server, string clientId)
        => AddClient(server, clientId, "client_credentials");

    private static IdentityClient AddClient(IResourceBuilder<IdentityServerResource> server, string clientId, string grantType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        if (server.Resource.Clients.Any(c => c.ClientId == clientId)) throw new ArgumentException($"Duplicate client '{clientId}'.");
        var client = new IdentityClient(server, clientId, grantType, server.Resource.Clients.Count);
        server.Resource.Clients.Add(client);
        return client;
    }

    private static void WriteConfiguration(IdentityServerResource server, IDictionary<string, object> environment)
    {
        var scopes = server.Apis.SelectMany(a => a.Scopes).ToHashSet(StringComparer.Ordinal);
        var knownScopes = scopes.Concat(["openid", "profile", "email"]).ToHashSet(StringComparer.Ordinal);
        for (var i = 0; i < server.Apis.Count; i++)
        {
            var api = server.Apis[i];
            if (api.Scopes.Count == 0) throw new InvalidOperationException($"Audience '{api.Audience}' needs at least one scope.");
            environment[$"IdentityServer__Apis__{i}__Audience"] = api.Audience;
            AddList(environment, $"IdentityServer__Apis__{i}__Scopes", api.Scopes);
        }
        for (var i = 0; i < server.Clients.Count; i++)
        {
            var client = server.Clients[i];
            if (client.Scopes.Count == 0 || client.Scopes.Any(s => !knownScopes.Contains(s)))
                throw new InvalidOperationException($"Client '{client.ClientId}' must use declared API scopes or openid/profile/email.");
            if (client.GrantType == "client_credentials" && (client.AuthenticationMethod == "none" || client.Scopes.Any(s => !scopes.Contains(s))))
                throw new InvalidOperationException($"Machine client '{client.ClientId}' requires authentication and API scopes only.");
            if (client.GrantType == "authorization_code" && (client.RedirectUris.Count == 0 || !client.Scopes.Contains("openid")))
                throw new InvalidOperationException($"Interactive client '{client.ClientId}' needs an openid scope and a redirect URI.");
            if (client.GrantType == "authorization_code" && client.AuthenticationMethod == "none" && !client.UsePkce)
                throw new InvalidOperationException("Public clients must use PKCE.");
            var prefix = $"IdentityServer__Clients__{i}";
            environment[$"{prefix}__ClientId"] = client.ClientId;
            environment[$"{prefix}__GrantType"] = client.GrantType;
            environment[$"{prefix}__AuthenticationMethod"] = client.AuthenticationMethod;
            environment[$"{prefix}__RequirePkce"] = client.UsePkce.ToString();
            if (client.ClientSecret is { } secret) environment[$"{prefix}__ClientSecret"] = secret.Resource;
            if (client.PublicJwk is { } jwk) environment[$"{prefix}__PublicJwk"] = jwk;
            AddList(environment, $"{prefix}__Scopes", client.Scopes);
            AddList(environment, $"{prefix}__RedirectUris", client.RedirectUris);
            AddList(environment, $"{prefix}__PostLogoutRedirectUris", client.PostLogoutRedirectUris);
        }
        for (var i = 0; i < server.Users.Count; i++)
        {
            var user = server.Users[i];
            var prefix = $"IdentityServer__Users__{i}";
            environment[$"{prefix}__Username"] = user.Username;
            environment[$"{prefix}__SubjectId"] = user.SubjectId;
            environment[$"{prefix}__DisplayName"] = user.DisplayName;
            environment[$"{prefix}__Password"] = user.Password.Resource;
        }
    }

    private static void AddList<T>(IDictionary<string, object> environment, string prefix, IEnumerable<T> values) where T : notnull
    {
        var index = 0;
        foreach (var value in values) environment[$"{prefix}__{index++}"] = value;
    }
}
