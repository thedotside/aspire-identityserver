using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http;

namespace Aspire.IdentityServer.Client;

/// <summary>The discovery cache and token backchannel shared by every grant handler.</summary>
internal static class TokenBackchannel
{
    /// <summary>Registers them once, whatever the number of handlers.</summary>
    internal static void Register(IServiceCollection services)
    {
        if (services.Any(d => d.ServiceType == typeof(TokenExchangeMetadata)))
            return;
        services.AddSingleton(provider => new TokenExchangeMetadata(
            provider.GetService<TimeProvider>() ?? TimeProvider.System
        ));
        services.AddHttpClient(TokenExchangeHttpClientExtensions.BackchannelClientName);
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<
                IHttpMessageHandlerBuilderFilter,
                FreshClientAssertionFilter
            >()
        );
    }
}
