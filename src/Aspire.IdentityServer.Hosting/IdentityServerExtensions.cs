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
        server.AddTestUser(username, password, displayName, subjectId);
        return server;
    }

    /// <summary>
    /// Adds a test user and returns it, to give it profile claims with
    /// <see cref="IdentityTestUser.WithClaim" />. Its password becomes an Aspire secret parameter.
    /// </summary>
    public static IdentityTestUser AddTestUser(
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
        var user = new IdentityTestUser(
            server,
            username,
            subjectId,
            displayName ?? username,
            secret
        );
        server.Resource.Users.Add(user);
        return user;
    }

    /// <summary>
    /// Declares an identity scope beyond <c>openid</c>, <c>profile</c> and <c>email</c>: an
    /// interactive client that requests it receives these user claims in its ID token.
    /// </summary>
    public static IResourceBuilder<IdentityServerResource> WithIdentityScope(
        this IResourceBuilder<IdentityServerResource> server,
        string name,
        params string[] userClaims
    )
    {
        IdentityClient.ValidateScope(name);
        ArgumentNullException.ThrowIfNull(userClaims);
        if (name is "openid" or "profile" or "email" or "offline_access")
            throw new ArgumentException($"'{name}' is a built-in scope.", nameof(name));
        if (
            server.Resource.IdentityScopes.Any(s =>
                string.Equals(s.Name, name, StringComparison.Ordinal)
            )
        )
            throw new ArgumentException($"Duplicate identity scope '{name}'.", nameof(name));
        if (userClaims.Length == 0 || userClaims.Any(c => !TokenExchangeClaims.IsUserClaim(c)))
            throw new ArgumentException(
                "Select at least one user claim, not protocol claims such as sub, scope, aud or act.",
                nameof(userClaims)
            );
        server.Resource.IdentityScopes.Add(
            new IdentityScope(name, userClaims.Distinct(StringComparer.Ordinal).ToArray())
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
        foreach (var identityScope in server.IdentityScopes)
            if (scopes.Contains(identityScope.Name))
                throw new InvalidOperationException(
                    $"Scope '{identityScope.Name}' cannot be both an identity scope and an API scope."
                );
        // offline_access asks for a refresh token: only a user session can be renewed that way.
        var knownScopes = scopes
            .Concat(["openid", "profile", "email", "offline_access"])
            .Concat(server.IdentityScopes.Select(s => s.Name))
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
                    $"Client '{client.ClientId}' must use declared API scopes, identity scopes or offline_access."
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
                    policy.Scopes == TokenExchangeScopes.FromMachineClient
                    && !policy.Api.Scopes.Intersect(client.Scopes, StringComparer.Ordinal).Any()
                )
                    throw new InvalidOperationException(
                        $"Token exchange for '{client.ClientId}' requires a scope of '{policy.Api.Audience}' allowed to this machine client."
                    );
                else if (
                    policy.Scopes == TokenExchangeScopes.FromSubjectToken
                    && !policy
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
            AddList(environment, $"IdentityServer__Apis__{i}__UserClaims", api.UserClaims);
        }
        for (var i = 0; i < server.IdentityScopes.Count; i++)
        {
            var scope = server.IdentityScopes[i];
            environment[$"IdentityServer__IdentityScopes__{i}__Name"] = scope.Name;
            AddList(
                environment,
                $"IdentityServer__IdentityScopes__{i}__UserClaims",
                scope.UserClaims
            );
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
                environment[$"{policyPrefix}__Scopes"] = policy.Scopes.ToString();
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
            for (var claimIndex = 0; claimIndex < user.Claims.Count; claimIndex++)
            {
                var (type, value) = user.Claims[claimIndex];
                environment[$"{prefix}__Claims__{claimIndex}__Type"] = type;
                environment[$"{prefix}__Claims__{claimIndex}__Value"] = value;
            }
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
