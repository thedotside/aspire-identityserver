using Aspire.IdentityServer;
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
    internal List<IdentityTestUser> Users { get; } = [];
    internal List<IdentityScope> IdentityScopes { get; } = [];

    /// <summary>The clients registered with this provider, in declaration order.</summary>
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

/// <summary>A local test account and the profile claims the provider issues for it.</summary>
public sealed class IdentityTestUser
{
    private readonly List<KeyValuePair<string, string>> claims = [];

    internal IdentityTestUser(
        IResourceBuilder<IdentityServerResource> server,
        string username,
        string subjectId,
        string displayName,
        IResourceBuilder<ParameterResource> password
    )
    {
        Server = server;
        Username = username;
        SubjectId = subjectId;
        DisplayName = displayName;
        Password = password;
    }

    /// <summary>The provider of this account.</summary>
    public IResourceBuilder<IdentityServerResource> Server { get; }

    /// <summary>The sign-in name, unique within its provider.</summary>
    public string Username { get; }

    /// <summary>The <c>sub</c> of the tokens issued for this account.</summary>
    public string SubjectId { get; }

    /// <summary>The value of the <c>name</c> claim.</summary>
    public string DisplayName { get; }

    /// <summary>The secret parameter holding the password.</summary>
    public IResourceBuilder<ParameterResource> Password { get; }

    /// <summary>The additional claims, in declaration order; a type may have several values.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Claims => claims.AsReadOnly();

    /// <summary>
    /// Adds profile claims. Several values of one type, such as group memberships, are issued as
    /// a JSON array, a single value as a string. Claims are selected by requested identity scopes,
    /// API resources, or a token exchange's explicit claim list.
    /// </summary>
    public IdentityTestUser WithClaim(string type, params string[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (
            !TokenExchangeClaims.IsUserClaim(type)
            || string.Equals(type, "name", StringComparison.Ordinal)
        )
            throw new ArgumentException(
                "Use a profile claim: protocol claims such as sub are issued by the provider, and name comes from the display name.",
                nameof(type)
            );
        if (values.Length == 0 || values.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Give at least one non-empty claim value.", nameof(values));
        foreach (var value in values)
        {
            var claim = KeyValuePair.Create(type, value);
            if (!claims.Contains(claim))
                claims.Add(claim);
        }
        return this;
    }
}

internal sealed record IdentityScope(string Name, IReadOnlyList<string> UserClaims);

/// <summary>An API audience and the scopes that issue access tokens for it.</summary>
public sealed class IdentityApiResource
{
    private readonly HashSet<string> userClaims = new(StringComparer.Ordinal);

    internal IdentityApiResource(IResourceBuilder<IdentityServerResource> server, string audience)
    {
        Server = server;
        Audience = audience;
    }

    /// <summary>The provider that issues tokens for this API.</summary>
    public IResourceBuilder<IdentityServerResource> Server { get; }

    /// <summary>The audience (<c>aud</c>) of access tokens issued for this API.</summary>
    public string Audience { get; }

    /// <summary>The deferred HTTPS address of the provider, its issuer.</summary>
    public EndpointReference Authority => Server.GetEndpoint("https");
    internal HashSet<string> ScopeNames { get; } = new(StringComparer.Ordinal);

    /// <summary>The scopes that issue tokens for this API.</summary>
    public IReadOnlyCollection<string> Scopes => ScopeNames;

    /// <summary>The user claim types selected for access tokens targeting this API.</summary>
    public IReadOnlyCollection<string> UserClaims => userClaims.ToArray();

    /// <summary>
    /// Adds user claim types to access tokens when any scope of this API is granted.
    /// Values come from the user's profile; no identity scope is required. Machine tokens have
    /// no user profile, and token exchange keeps its own explicit claim selection.
    /// </summary>
    public IdentityApiResource WithUserClaims(params string[] userClaims)
    {
        ArgumentNullException.ThrowIfNull(userClaims);
        if (userClaims.Length == 0 || userClaims.Any(c => !TokenExchangeClaims.IsUserClaim(c)))
            throw new ArgumentException(
                "Select at least one user claim, not protocol claims such as sub, scope, aud or act.",
                nameof(userClaims)
            );
        this.userClaims.UnionWith(userClaims);
        return this;
    }

    /// <summary>Adds API scopes; identity scopes and <c>offline_access</c> are reserved.</summary>
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
