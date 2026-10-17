using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.IdentityServer.Hosting;

/// <summary>Maps client fields to arbitrary .NET configuration paths or environment variable names.</summary>
public sealed class IdentityClientProjection
{
    internal Dictionary<string, string> Fields { get; } = new(StringComparer.OrdinalIgnoreCase);
    public IdentityClientProjection Authority(string key) => Map(key, nameof(Authority));
    public IdentityClientProjection ClientId(string key) => Map(key, nameof(ClientId));
    public IdentityClientProjection ClientSecret(string key) => Map(key, nameof(ClientSecret));
    public IdentityClientProjection PrivateKeyPem(string key) => Map(key, nameof(PrivateKeyPem));
    public IdentityClientProjection KeyId(string key) => Map(key, nameof(KeyId));
    public IdentityClientProjection Scopes(string key) => Map(key, nameof(Scopes));
    public IdentityClientProjection AuthenticationMethod(string key) => Map(key, nameof(AuthenticationMethod));

    private IdentityClientProjection Map(string key, string field)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        key = key.Replace(":", "__", StringComparison.Ordinal);
        if (!Fields.TryAdd(key, field)) throw new ArgumentException($"Duplicate projection key '{key}'.", nameof(key));
        return this;
    }
}

public static class IdentityProjectionExtensions
{
    public static IResourceBuilder<T> WithIdentityClient<T>(this IResourceBuilder<T> consumer, IdentityClient client,
        string section) where T : IResourceWithEnvironment, IResourceWithWaitSupport
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(section);
        return consumer.WithIdentityClient(client, p =>
        {
            p.Authority($"{section}:Authority").ClientId($"{section}:ClientId")
                .Scopes($"{section}:Scopes").AuthenticationMethod($"{section}:AuthenticationMethod");
            if (client.ClientSecret is not null) p.ClientSecret($"{section}:ClientSecret");
            if (client.PrivateKeyPem is not null) p.PrivateKeyPem($"{section}:PrivateKeyPem").KeyId($"{section}:KeyId");
        });
    }

    public static IResourceBuilder<T> WithIdentityClient<T>(this IResourceBuilder<T> consumer, IdentityClient client,
        Action<IdentityClientProjection> configure) where T : IResourceWithEnvironment, IResourceWithWaitSupport
    {
        var projection = new IdentityClientProjection();
        configure(projection);
        consumer.WithEnvironment(context =>
        {
            foreach (var (key, field) in projection.Fields)
            {
                object? value = field switch
                {
                    "Authority" => client.Authority,
                    "ClientId" => client.ClientId,
                    "ClientSecret" => client.ClientSecret?.Resource,
                    "PrivateKeyPem" => client.PrivateKeyPem?.Resource,
                    "KeyId" => client.KeyId,
                    "Scopes" => string.Join(' ', client.Scopes),
                    "AuthenticationMethod" => client.AuthenticationMethod,
                    _ => throw new InvalidOperationException($"Unknown projection field '{field}'.")
                };
                if (value is null) throw new InvalidOperationException($"Client '{client.ClientId}' has no {field} to project.");
                if (!context.EnvironmentVariables.TryAdd(key, value))
                    throw new InvalidOperationException($"Configuration key '{key}' is already assigned on '{consumer.Resource.Name}'.");
            }
        });
        return consumer.WaitFor(client.Server);
    }

    /// <summary>Configures inbound token validation without disclosing any client credentials.</summary>
    public static IResourceBuilder<T> WithIdentityResource<T>(this IResourceBuilder<T> consumer, IdentityApiResource api,
        string section) where T : IResourceWithEnvironment, IResourceWithWaitSupport
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(section);
        return consumer.WithEnvironment($"{section}:Authority".Replace(":", "__"), api.Authority)
            .WithEnvironment($"{section}:Audience".Replace(":", "__"), api.Audience)
            .WaitFor(api.Server);
    }
}
