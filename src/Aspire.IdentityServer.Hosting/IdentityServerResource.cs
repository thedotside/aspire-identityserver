using Aspire.IdentityServer.Hosting;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// A local, ephemeral OAuth/OIDC server process managed by Aspire. As a service-discovery
/// resource, <c>WithReference(provider)</c> gives a consumer its HTTPS address.
/// </summary>
public sealed class IdentityServerResource(string name, string runtimeDirectory)
    : ExecutableResource(name, DotnetHost.Path, runtimeDirectory),
        IResourceWithServiceDiscovery
{
    internal List<IdentityClient> Clients { get; } = [];
    internal List<IdentityApiResource> Apis { get; } = [];
    internal List<TestUser> Users { get; } = [];
    public IReadOnlyList<IdentityClient> RegisteredClients => Clients.AsReadOnly();
}

/// <summary>Options of one local identity provider.</summary>
public sealed class IdentityServerOptions
{
    /// <summary>Overrides the bundled server location when developing this repository.</summary>
    public string RuntimeDirectory { get; set; } =
        Path.Combine(AppContext.BaseDirectory, "identityserver");

    /// <summary>
    /// Also accepts private_key_jwt assertions in the original RFC 7523 profile, whose audience is
    /// the token endpoint. By default the provider requires the draft-ietf-oauth-rfc7523bis profile:
    /// the issuer as sole audience and the <c>client-authentication+jwt</c> type. Enable this only
    /// for clients written for providers that refuse the issuer audience.
    /// </summary>
    public bool AcceptTokenEndpointAudience { get; set; }
}

internal sealed record TestUser(
    string Username,
    string SubjectId,
    string DisplayName,
    IResourceBuilder<ParameterResource> Password
);

/// <summary>An API audience and the scopes that issue access tokens for it.</summary>
public sealed class IdentityApiResource
{
    internal IdentityApiResource(IResourceBuilder<IdentityServerResource> server, string audience)
    {
        Server = server;
        Audience = audience;
    }

    public IResourceBuilder<IdentityServerResource> Server { get; }
    public string Audience { get; }
    public EndpointReference Authority => Server.GetEndpoint("https");
    internal HashSet<string> ScopeNames { get; } = new(StringComparer.Ordinal);
    public IReadOnlyCollection<string> Scopes => ScopeNames;

    public IdentityApiResource WithScopes(params string[] scopes)
    {
        foreach (var scope in scopes)
        {
            IdentityClient.ValidateScope(scope);
            if (scope is "openid" or "profile" or "email" or "offline_access")
                throw new ArgumentException(
                    $"'{scope}' is reserved for identity or session scopes.",
                    nameof(scopes)
                );
            ScopeNames.Add(scope);
        }
        return this;
    }
}
