using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting;

/// <summary>Maps client fields to arbitrary .NET configuration paths or environment variable names.</summary>
public sealed class IdentityClientProjection
{
    internal Dictionary<string, string> Fields { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Projects the provider issuer to <paramref name="key" />.</summary>
    public IdentityClientProjection Authority(string key) => Map(key, nameof(Authority));

    /// <summary>Projects the client identifier to <paramref name="key" />.</summary>
    public IdentityClientProjection ClientId(string key) => Map(key, nameof(ClientId));

    /// <summary>Projects the client secret parameter to <paramref name="key" />.</summary>
    public IdentityClientProjection ClientSecret(string key) => Map(key, nameof(ClientSecret));

    /// <summary>Projects the PEM private key parameter to <paramref name="key" />.</summary>
    public IdentityClientProjection PrivateKeyPem(string key) => Map(key, nameof(PrivateKeyPem));

    /// <summary>Projects the Base64 private JWK parameter to <paramref name="key" />.</summary>
    public IdentityClientProjection PrivateKeyBase64(string key) =>
        Map(key, nameof(PrivateKeyBase64));

    /// <summary>Projects the key identifier to <paramref name="key" />.</summary>
    public IdentityClientProjection KeyId(string key) => Map(key, nameof(KeyId));

    /// <summary>Projects the space-delimited scopes to <paramref name="key" />.</summary>
    public IdentityClientProjection Scopes(string key) => Map(key, nameof(Scopes));

    /// <summary>Projects the authentication method to <paramref name="key" />.</summary>
    public IdentityClientProjection AuthenticationMethod(string key) =>
        Map(key, nameof(AuthenticationMethod));

    /// <summary>Projects the single token-exchange audience to <paramref name="key" />.</summary>
    public IdentityClientProjection TokenExchangeAudience(string key) =>
        Map(key, nameof(TokenExchangeAudience));

    private IdentityClientProjection Map(string key, string field)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        key = key.Replace(":", "__", StringComparison.Ordinal);
        if (!Fields.TryAdd(key, field))
            throw new ArgumentException($"Duplicate projection key '{key}'.", nameof(key));
        return this;
    }
}

/// <summary>Projects identity clients and API audiences into consumer configuration.</summary>
public static class IdentityProjectionExtensions
{
    /// <summary>
    /// Projects a client under <c>{section}</c> with the field names of
    /// <c>IdentityClientOptions</c>, and makes the consumer wait for the provider. The fields
    /// are chosen when the consumer starts: credentials and exchange policies declared after
    /// this call are projected too.
    /// </summary>
    public static IResourceBuilder<T> WithIdentityClient<T>(
        this IResourceBuilder<T> consumer,
        IdentityClient client,
        string section
    )
        where T : IResourceWithEnvironment, IResourceWithWaitSupport
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(section);
        return Project(consumer, client, () => DefaultProjection(client, section));
    }

    /// <summary>
    /// Projects only the selected client fields, to arbitrary configuration keys, and makes the
    /// consumer wait for the provider.
    /// </summary>
    public static IResourceBuilder<T> WithIdentityClient<T>(
        this IResourceBuilder<T> consumer,
        IdentityClient client,
        Action<IdentityClientProjection> configure
    )
        where T : IResourceWithEnvironment, IResourceWithWaitSupport
    {
        var projection = new IdentityClientProjection();
        configure(projection);
        return Project(consumer, client, () => projection);
    }

    /// <summary>The fields that the client has when the consumer starts, under one section.</summary>
    private static IdentityClientProjection DefaultProjection(IdentityClient client, string section)
    {
        var projection = new IdentityClientProjection()
            .Authority($"{section}:Authority")
            .ClientId($"{section}:ClientId")
            .Scopes($"{section}:Scopes")
            .AuthenticationMethod($"{section}:AuthenticationMethod");
        if (client.ClientSecret is not null)
            projection.ClientSecret($"{section}:ClientSecret");
        if (client.PrivateKeyPem is not null)
            projection.PrivateKeyPem($"{section}:PrivateKeyPem").KeyId($"{section}:KeyId");
        if (client.PrivateKeyBase64 is not null)
            projection.PrivateKeyBase64($"{section}:PrivateKeyBase64").KeyId($"{section}:KeyId");
        if (client.TokenExchangeAudience is not null)
            projection.TokenExchangeAudience($"{section}:TokenExchangeAudience");
        return projection;
    }

    private static IResourceBuilder<T> Project<T>(
        IResourceBuilder<T> consumer,
        IdentityClient client,
        Func<IdentityClientProjection> select
    )
        where T : IResourceWithEnvironment, IResourceWithWaitSupport
    {
        consumer.WithEnvironment(context =>
        {
            foreach (var (key, field) in select().Fields)
            {
                object? value = field switch
                {
                    "Authority" => client.Authority,
                    "ClientId" => client.ClientId,
                    "ClientSecret" => client.ClientSecret?.Resource,
                    "PrivateKeyPem" => client.PrivateKeyPem?.Resource,
                    "PrivateKeyBase64" => client.PrivateKeyBase64?.Resource,
                    "KeyId" => client.KeyId,
                    "Scopes" => string.Join(' ', client.Scopes),
                    "AuthenticationMethod" => client.AuthenticationMethod,
                    "TokenExchangeAudience" => client.TokenExchangeAudience,
                    _ => throw new InvalidOperationException(
                        $"Unknown projection field '{field}'."
                    ),
                };
                if (value is null)
                    throw new InvalidOperationException(
                        $"Client '{client.ClientId}' has no {field} to project."
                    );
                if (!context.EnvironmentVariables.TryAdd(key, value))
                    throw new InvalidOperationException(
                        $"Configuration key '{key}' is already assigned on '{consumer.Resource.Name}'."
                    );
            }
        });
        return consumer.WaitFor(client.Server);
    }

    /// <summary>
    /// Configures inbound token validation of an API audience: <c>{section}:Authority</c> and
    /// <c>{section}:Audience</c>, without disclosing any client credentials.
    /// </summary>
    public static IResourceBuilder<T> WithIdentityApiResource<T>(
        this IResourceBuilder<T> consumer,
        IdentityApiResource api,
        string section
    )
        where T : IResourceWithEnvironment, IResourceWithWaitSupport
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(section);
        return consumer
            .WithEnvironment(
                $"{section}:Authority".Replace(":", "__", StringComparison.Ordinal),
                api.Authority
            )
            .WithEnvironment(
                $"{section}:Audience".Replace(":", "__", StringComparison.Ordinal),
                api.Audience
            )
            .WaitFor(api.Server);
    }

    /// <summary>Renamed <see cref="WithIdentityApiResource" />: Duende identity resources are openid or profile.</summary>
    [Obsolete(
        "Use WithIdentityApiResource: it projects an API audience, not an identity resource."
    )]
    public static IResourceBuilder<T> WithIdentityResource<T>(
        this IResourceBuilder<T> consumer,
        IdentityApiResource api,
        string section
    )
        where T : IResourceWithEnvironment, IResourceWithWaitSupport =>
        consumer.WithIdentityApiResource(api, section);
}
