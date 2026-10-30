using Aspire.IdentityServer.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aspire.IdentityServer.Tests;

public sealed class TokenExchangeClientTests
{
    [Fact]
    public void Backchannel_cannot_install_its_own_exchange_handler()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();
        var client = services.AddHttpClient(
            TokenExchangeHttpClientExtensions.BackchannelClientName
        );
        Assert.Throws<ArgumentException>(() =>
            client.AddTokenExchangeHandler(configuration.GetSection("Device"))
        );
    }

    [Theory]
    [InlineData("http://localhost/connect/token")]
    [InlineData("/connect/token")]
    [InlineData("https://user:password@localhost/connect/token")]
    public void Helper_requires_an_absolute_https_endpoint_without_credentials(string endpoint)
    {
        Assert.Throws<ArgumentException>(() =>
            TokenExchange.CreateRequest(
                new IdentityClientOptions { ClientId = "device" },
                endpoint,
                "subject",
                "orders"
            )
        );
    }

    [Fact]
    public void Public_clients_cannot_build_an_exchange_request()
    {
        Assert.Throws<ArgumentException>(() =>
            TokenExchange.CreateRequest(
                new IdentityClientOptions { ClientId = "public" },
                "https://localhost/connect/token",
                "subject",
                "orders"
            )
        );
    }
}
