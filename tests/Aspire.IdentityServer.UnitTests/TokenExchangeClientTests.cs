using Aspire.IdentityServer.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.IdentityServer.Tests;

public sealed class TokenExchangeClientTests
{
    [Test]
    public async Task Backchannel_cannot_install_its_own_exchange_handler()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();
        var client = services.AddHttpClient(
            TokenExchangeHttpClientExtensions.BackchannelClientName
        );
        await Assert
            .That(() => client.AddTokenExchangeHandler(configuration.GetSection("Device")))
            .ThrowsExactly<ArgumentException>();
    }

    [Test]
    [Arguments("http://localhost/connect/token")]
    [Arguments("/connect/token")]
    [Arguments("https://user:password@localhost/connect/token")]
    public async Task Helper_requires_an_absolute_https_endpoint_without_credentials(
        string endpoint
    )
    {
        await Assert
            .That(() =>
                TokenExchange.CreateRequest(
                    new IdentityClientOptions { ClientId = "device" },
                    endpoint,
                    "subject",
                    "orders"
                )
            )
            .ThrowsExactly<ArgumentException>();
    }

    [Test]
    public async Task Public_clients_cannot_build_an_exchange_request()
    {
        await Assert
            .That(() =>
                TokenExchange.CreateRequest(
                    new IdentityClientOptions { ClientId = "public" },
                    "https://localhost/connect/token",
                    "subject",
                    "orders"
                )
            )
            .ThrowsExactly<ArgumentException>();
    }
}
