using System.Globalization;
using Aspire.Hosting.ApplicationModel;
using Aspire.IdentityServer;
using Aspire.IdentityServer.Hosting;
using Microsoft.Extensions.Hosting;

namespace Aspire.Hosting;

/// <summary>Adds local development identity providers to an Aspire AppHost.</summary>
public static class IdentityServerExtensions
{
    /// <summary>Adds the bundled local development identity server, with an isolated issuer and signing key.</summary>
    public static IResourceBuilder<IdentityServerResource> AddIdentityServer(
        this IDistributedApplicationBuilder builder,
        string name,
        Action<IdentityServerOptions>? configure = null
    )
    {
        if (builder.ExecutionContext.IsPublishMode)
            throw new InvalidOperationException(
                "Aspire IdentityServer is a local development/testing resource and cannot be deployed. "
                    + "Declare it only in run mode, inside if (builder.ExecutionContext.IsRunMode)."
            );
        // The provider inherits the AppHost environment instead of being forced to Development,
        // so its own Development/Testing guard keeps a meaning.
        var environment = builder.Environment.EnvironmentName;
        if (!builder.Environment.IsDevelopment() && !builder.Environment.IsEnvironment("Testing"))
            throw new InvalidOperationException(
                $"Aspire IdentityServer runs only in an AppHost started in Development or Testing, not '{environment}'. "
                    + "Set DOTNET_ENVIRONMENT for the AppHost process."
            );
        var options = new IdentityServerOptions();
        configure?.Invoke(options);
        var directory = Path.GetFullPath(options.RuntimeDirectory, builder.AppHostDirectory);
        var assembly = Path.Combine(directory, "Aspire.IdentityServer.Server.dll");
        if (!File.Exists(assembly))
            throw new FileNotFoundException(
                "The bundled identity server is missing. Build your AppHost, or publish the server and set RuntimeDirectory when developing this repository.",
                assembly
            );
        // Aspire's certificate mapping API is experimental; keep its use inside this integration.
#pragma warning disable ASPIRECERTIFICATES001
        var server = builder
            .AddResource(new IdentityServerResource(name, directory))
            .WithArgs(assembly)
            .WithHttpsEndpoint(env: "ASPNETCORE_HTTPS_PORTS")
            .WithHttpsCertificateConfiguration(context =>
            {
                context.EnvironmentVariables["Kestrel__Certificates__Default__Path"] =
                    context.PfxPath;
                if (context.Password is not null)
                    context.EnvironmentVariables["Kestrel__Certificates__Default__Password"] =
                        context.Password;
                return Task.CompletedTask;
            })
            .WithEnvironment("ASPNETCORE_ENVIRONMENT", environment)
            .WithEnvironment(
                RuntimeContract.EnvironmentVariable,
                RuntimeContract.Version.ToString(CultureInfo.InvariantCulture)
            )
            .WithEnvironment("IdentityServer__Name", name)
            .WithEnvironment(
                "IdentityServer__AcceptTokenEndpointAudience",
                options.AcceptTokenEndpointAudience ? "true" : "false"
            )
            .WithHttpHealthCheck("/health", endpointName: "https")
            .WithOtlpExporter()
            .WithIconName("ShieldKeyhole")
            .WithUrls(context =>
            {
                if (context.GetEndpoint("https") is { } endpoint)
                    context.Urls.Add(
                        new ResourceUrlAnnotation
                        {
                            Url = endpoint.Url.TrimEnd('/') + "/.well-known/openid-configuration",
                            DisplayText = "OpenID discovery",
                            Endpoint = endpoint,
                        }
                    );
            });
#pragma warning restore ASPIRECERTIFICATES001
        server.WithEnvironment("IdentityServer__Issuer", server.GetEndpoint("https"));
        server.WithEnvironment(context =>
            WriteConfiguration(server.Resource, context.EnvironmentVariables)
        );
        builder.Eventing.Subscribe<BeforeStartEvent>(
            (_, _) =>
            {
                Validate(server.Resource);
                return Task.CompletedTask;
            }
        );
        return server;
    }

    /// <summary>Adds a test user; its password becomes an Aspire secret parameter.</summary>
    public static IResourceBuilder<IdentityServerResource> WithTestUser(
        this IResourceBuilder<IdentityServerResource> server,
        string username,
        string password,
        string? displayName = null,
        string? subjectId = null
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        subjectId ??= $"{server.Resource.Name}:{username}";
        if (
            server.Resource.Users.Any(u =>
                string.Equals(u.Username, username, StringComparison.Ordinal)
                || string.Equals(u.SubjectId, subjectId, StringComparison.Ordinal)
            )
        )
            throw new ArgumentException(
                "Test usernames and subject IDs must be unique within an identity provider.",
                nameof(username)
            );
        var secret = server
            .ApplicationBuilder.AddParameter(
                ParameterName(
                    server,
                    username,
                    "password",
                    $"{server.Resource.Name}-user-{server.Resource.Users.Count}-password"
                ),
                () => password,
                secret: true
            )
            .WithParentRelationship(server);
        server.Resource.Users.Add(
            new TestUser(username, subjectId, displayName ?? username, secret)
        );
        return server;
    }

    /// <summary>Declares an API audience; add its scopes with <see cref="IdentityApiResource.WithScopes" />.</summary>
    public static IdentityApiResource AddApiResource(
        this IResourceBuilder<IdentityServerResource> server,
        string audience
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(audience);
        if (
            server.Resource.Apis.Any(a =>
                string.Equals(a.Audience, audience, StringComparison.Ordinal)
            )
        )
            throw new ArgumentException($"Duplicate audience '{audience}'.", nameof(audience));
        var api = new IdentityApiResource(server, audience);
        server.Resource.Apis.Add(api);
        return api;
    }

    /// <summary>Registers an interactive authorization code client, public with PKCE until given credentials.</summary>
    public static IdentityClient AddAuthorizationCodeClient(
        this IResourceBuilder<IdentityServerResource> server,
        string clientId
    ) => AddClient(server, clientId, "authorization_code");

    /// <summary>Registers a machine client; give it credentials and API scopes.</summary>
    public static IdentityClient AddClientCredentialsClient(
        this IResourceBuilder<IdentityServerResource> server,
        string clientId
    ) => AddClient(server, clientId, "client_credentials");

    private static IdentityClient AddClient(
        IResourceBuilder<IdentityServerResource> server,
        string clientId,
        string grantType
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        if (
            server.Resource.Clients.Any(c =>
                string.Equals(c.ClientId, clientId, StringComparison.Ordinal)
            )
        )
            throw new ArgumentException($"Duplicate client '{clientId}'.", nameof(clientId));
        var client = new IdentityClient(server, clientId, grantType, server.Resource.Clients.Count);
        server.Resource.Clients.Add(client);
        return client;
    }

    /// <summary>
    /// A dashboard-friendly name, <c>{provider}-{owner}-{suffix}</c>, for a generated parameter;
    /// the index-based fallback keeps names valid and unique for any owner.
    /// </summary>
    internal static string ParameterName(
        IResourceBuilder<IdentityServerResource> server,
        string owner,
        string suffix,
        string fallback
    )
    {
        var slug = string.Join(
            '-',
            new string(
                owner
                    .Select(c => char.IsAsciiLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-')
                    .ToArray()
            ).Split('-', StringSplitOptions.RemoveEmptyEntries)
        );
        // Aspire resource names: ASCII letters, digits and single hyphens, at most 64 characters.
        var candidate = $"{server.Resource.Name}-{slug}-{suffix}";
        return
            slug.Length > 0
            && candidate.Length <= 64
            && !server.ApplicationBuilder.Resources.Any(r =>
                string.Equals(r.Name, candidate, StringComparison.OrdinalIgnoreCase)
            )
            ? candidate
            : fallback;
    }

    /// <summary>
    /// Checks every registration rule of a provider. It runs before the AppHost starts any
    /// resource, so a misconfiguration fails the AppHost instead of one provider process.
    /// </summary>
    internal static void Validate(IdentityServerResource server)
    {
        var scopes = server.Apis.SelectMany(a => a.Scopes).ToHashSet(StringComparer.Ordinal);
        var knownScopes = scopes
            .Concat(["openid", "profile", "email"])
            .ToHashSet(StringComparer.Ordinal);
        foreach (var api in server.Apis)
            if (api.Scopes.Count == 0)
                throw new InvalidOperationException(
                    $"Audience '{api.Audience}' needs at least one scope."
                );
        foreach (var client in server.Clients)
        {
            if (client.Scopes.Count == 0 || client.Scopes.Any(s => !knownScopes.Contains(s)))
                throw new InvalidOperationException(
                    $"Client '{client.ClientId}' must use declared API scopes or openid/profile/email."
                );
            if (
                string.Equals(client.GrantType, "client_credentials", StringComparison.Ordinal)
                && (
                    string.Equals(client.AuthenticationMethod, "none", StringComparison.Ordinal)
                    || client.Scopes.Any(s => !scopes.Contains(s))
                )
            )
                throw new InvalidOperationException(
                    $"Machine client '{client.ClientId}' requires authentication and API scopes only."
                );
            if (
                string.Equals(client.GrantType, "authorization_code", StringComparison.Ordinal)
                && (
                    client.RedirectUris.Count == 0
                    || !client.Scopes.Contains("openid", StringComparer.Ordinal)
                )
            )
                throw new InvalidOperationException(
                    $"Interactive client '{client.ClientId}' needs an openid scope and a redirect URI."
                );
            if (
                string.Equals(client.GrantType, "authorization_code", StringComparison.Ordinal)
                && string.Equals(client.AuthenticationMethod, "none", StringComparison.Ordinal)
                && !client.UsePkce
            )
                throw new InvalidOperationException(
                    $"Public client '{client.ClientId}' must use PKCE."
                );
            foreach (var policy in client.TokenExchanges)
                if (
                    !policy
                        .Api.Scopes.Intersect(client.Scopes, StringComparer.Ordinal)
                        .Intersect(policy.SubjectClient.Scopes, StringComparer.Ordinal)
                        .Any()
                )
                    throw new InvalidOperationException(
                        $"Token exchange for '{client.ClientId}' requires an API scope shared with '{policy.SubjectClient.ClientId}'."
                    );
        }
    }

    private static void WriteConfiguration(
        IdentityServerResource server,
        IDictionary<string, object> environment
    )
    {
        Validate(server);
        for (var i = 0; i < server.Apis.Count; i++)
        {
            var api = server.Apis[i];
            environment[$"IdentityServer__Apis__{i}__Audience"] = api.Audience;
            AddList(environment, $"IdentityServer__Apis__{i}__Scopes", api.Scopes);
        }
        for (var i = 0; i < server.Clients.Count; i++)
        {
            var client = server.Clients[i];
            var prefix = $"IdentityServer__Clients__{i}";
            environment[$"{prefix}__ClientId"] = client.ClientId;
            environment[$"{prefix}__GrantType"] = client.GrantType;
            environment[$"{prefix}__AuthenticationMethod"] = client.AuthenticationMethod;
            environment[$"{prefix}__RequirePkce"] = client.UsePkce.ToString();
            if (client.ClientSecret is { } secret)
                environment[$"{prefix}__ClientSecretHash"] = new ClientSecretHash(secret.Resource);
            if (client.PublicJwk is { } jwk)
                environment[$"{prefix}__PublicJwk"] = jwk;
            AddList(environment, $"{prefix}__Scopes", client.Scopes);
            for (var policyIndex = 0; policyIndex < client.TokenExchanges.Count; policyIndex++)
            {
                var policy = client.TokenExchanges[policyIndex];
                var policyPrefix = $"{prefix}__TokenExchanges__{policyIndex}";
                environment[$"{policyPrefix}__SubjectClientId"] = policy.SubjectClient.ClientId;
                environment[$"{policyPrefix}__Audience"] = policy.Api.Audience;
                AddList(environment, $"{policyPrefix}__UserClaims", policy.UserClaims);
            }
            AddList(environment, $"{prefix}__RedirectUris", client.RedirectUris);
            AddList(
                environment,
                $"{prefix}__PostLogoutRedirectUris",
                client.PostLogoutRedirectUris
            );
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

    private static void AddList<T>(
        IDictionary<string, object> environment,
        string prefix,
        IEnumerable<T> values
    )
        where T : notnull
    {
        var index = 0;
        foreach (var value in values)
            environment[$"{prefix}__{index++}"] = value;
    }
}
