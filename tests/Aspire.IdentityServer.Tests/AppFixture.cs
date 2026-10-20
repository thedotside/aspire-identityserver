using System.Diagnostics;
using Aspire.Hosting;
using Aspire.Hosting.Testing;
using Aspire.IdentityServer.Client;
using Aspire.IdentityServer.Hosting;
using Microsoft.Playwright;
using Xunit;

namespace Aspire.IdentityServer.Tests;

[CollectionDefinition("app")]
public sealed class AppCollection : ICollectionFixture<AppFixture>;

public sealed class AppFixture : IAsyncLifetime
{
    private IDistributedApplicationTestingBuilder? builder;
    private IPlaywright? playwright;
    public DistributedApplication App { get; private set; } = null!;
    public IBrowser Browser { get; private set; } = null!;
    public TimeSpan StartupTime { get; private set; }
    public IReadOnlyList<IdentityServerResource> Servers { get; private set; } = [];

    public async Task InitializeAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var watch = Stopwatch.StartNew();
        builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.Sample_AppHost>(
            ["DcpPublisher:RandomizePorts=true"],
            timeout.Token
        );
        Servers = builder.Resources.OfType<IdentityServerResource>().ToList();
        App = await builder.BuildAsync(timeout.Token);
        await App.StartAsync(timeout.Token);
        await Task.WhenAll(
            new[] { "idp-a", "idp-b", "web", "api" }.Select(name =>
                App.ResourceNotifications.WaitForResourceHealthyAsync(name, timeout.Token)
            )
        );
        StartupTime = watch.Elapsed;
        playwright = await Playwright.CreateAsync();
        Browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
    }

    public async Task<IdentityClientOptions> Client(string server, string clientId)
    {
        var registration = Servers
            .Single(s => string.Equals(s.Name, server, StringComparison.Ordinal))
            .RegisteredClients.Single(c =>
                string.Equals(c.ClientId, clientId, StringComparison.Ordinal)
            );
        return new IdentityClientOptions
        {
            Authority = registration.Authority.Url,
            ClientId = registration.ClientId,
            AuthenticationMethod = registration.AuthenticationMethod,
            PrivateKeyPem = registration.PrivateKeyPem is { } key
                ? await key.Resource.GetValueAsync(default)
                : null,
            ClientSecret = registration.ClientSecret is { } secret
                ? await secret.Resource.GetValueAsync(default)
                : null,
            KeyId = registration.KeyId,
            Scopes = string.Join(' ', registration.Scopes),
        };
    }

    public async Task DisposeAsync()
    {
        if (Browser is not null)
            await Browser.DisposeAsync();
        playwright?.Dispose();
        if (App is not null)
            await App.DisposeAsync();
        if (builder is not null)
            await builder.DisposeAsync();
    }
}
