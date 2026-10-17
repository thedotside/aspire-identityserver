using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.IdentityServer.Hosting;

/// <summary>A local, ephemeral OAuth/OIDC server process managed by Aspire.</summary>
public sealed class IdentityServerResource(string name, string runtimeDirectory)
    : ExecutableResource(name, "dotnet", runtimeDirectory)
{
    internal List<IdentityClient> Clients { get; } = [];
    internal List<IdentityApiResource> Apis { get; } = [];
    internal List<TestUser> Users { get; } = [];
    public IReadOnlyList<IdentityClient> RegisteredClients => Clients.AsReadOnly();
}

/// <summary>Overrides the bundled server location when developing this repository.</summary>
public sealed class IdentityServerOptions
{
    public string RuntimeDirectory { get; set; } = Path.Combine(AppContext.BaseDirectory, "identityserver");
}

internal sealed record TestUser(string Username, string SubjectId, string DisplayName,
    IResourceBuilder<ParameterResource> Password);

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
                throw new ArgumentException($"'{scope}' is reserved for identity or session scopes.");
            ScopeNames.Add(scope);
        }
        return this;
    }
}
