using System.ComponentModel;
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

    /// <summary>The test users of this provider, in declaration order.</summary>
    public IReadOnlyList<IdentityTestUser> RegisteredUsers => Users.AsReadOnly();

    /// <summary>
    /// The simulated second factor declared with <c>WithSimulatedMfa</c>, or <see langword="null" />.
    /// </summary>
    public IdentitySimulatedMfa? SimulatedMfa { get; internal set; }
}

/// <summary>
/// A simulated second factor: the fixed code the provider asks for after the password, and the
/// acr values whose request triggers it.
/// </summary>
public sealed class IdentitySimulatedMfa
{
    internal IdentitySimulatedMfa(string code, IReadOnlyList<string> acrValues)
    {
        Code = code;
        AcrValues = acrValues;
    }

    /// <summary>The code to type on the verification page: a fixed development value, not a secret.</summary>
    public string Code { get; }

    /// <summary>The acr values that ask for the code, in declaration order.</summary>
    public IReadOnlyList<string> AcrValues { get; }
}

/// <summary>Options of one local identity provider.</summary>
public sealed class IdentityServerOptions
{
    /// <summary>Overrides the bundled server location when developing this repository.</summary>
    public string RuntimeDirectory { get; set; } =
        Path.Combine(AppContext.BaseDirectory, "identityserver");

    /// <summary>
    /// Requires the draft-ietf-oauth-rfc7523bis profile for private_key_jwt assertions: the issuer
    /// as sole audience and the <c>client-authentication+jwt</c> type. <see langword="true" /> by
    /// default: the provider sets the Duende option of the same name, which itself defaults to
    /// <see langword="false" />. Set <see langword="false" /> only for clients written for
    /// providers that refuse the issuer audience: the provider then also accepts the original
    /// RFC 7523 profile, whose audience is the token endpoint.
    /// </summary>
    public bool StrictClientAssertionAudienceValidation { get; set; } = true;

    /// <summary>
    /// The former name of <see cref="StrictClientAssertionAudienceValidation" />, with the inverse
    /// meaning: <see langword="true" /> also accepts the token endpoint audience.
    /// </summary>
    [Obsolete("Use StrictClientAssertionAudienceValidation = false.")]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public bool AcceptTokenEndpointAudience
    {
        get => !StrictClientAssertionAudienceValidation;
        set => StrictClientAssertionAudienceValidation = !value;
    }
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
    /// a JSON array, a single value as a string. A requested identity scope selects its claims
    /// for the ID token, UserInfo and authorization-code access token; token exchange keeps its
    /// explicit claim selection.
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
