using System.Security.Cryptography;
using System.Text;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.IdentityServer.Hosting;

/// <summary>
/// The only form of a shared secret sent to the provider: Base64(SHA-256(UTF-8 secret)), the value
/// Duende stores. It is computed when the provider starts; the secret itself never enters the
/// provider environment.
/// </summary>
internal sealed class ClientSecretHash(ParameterResource secret)
    : IValueProvider,
        IValueWithReferences
{
    public IEnumerable<object> References => [secret];

    public async ValueTask<string?> GetValueAsync(CancellationToken cancellationToken = default)
    {
        var value = await secret.GetValueAsync(cancellationToken).ConfigureAwait(false);
        return value is null
            ? null
            : Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }
}
