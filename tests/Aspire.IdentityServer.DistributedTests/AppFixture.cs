using System.Diagnostics;
using System.Security.Cryptography;
using Aspire.Hosting;
using Aspire.Hosting.Testing;
using Aspire.IdentityServer.Client;
using Aspire.IdentityServer.Hosting;
using Microsoft.Playwright;
using TUnit.Core.Interfaces;

namespace Aspire.IdentityServer.Tests;

/// <summary>One real sample AppHost and browser, shared by every distributed test class.</summary>
public sealed class AppFixture : IAsyncInitializer, IAsyncDisposable
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
        var provider = builder.CreateResourceBuilder(
            Servers.Single(s => string.Equals(s.Name, "idp-a", StringComparison.Ordinal))
        );
        using (var rsa = RSA.Create(2048))
        {
            var value = TestKeys.Encode(rsa, "fixture-key");
            var publicJwk = TestKeys.Public(rsa, "fixture-key");
            provider
                .AddClientCredentialsClient("imported-jwk")
                .WithBase64PrivateKeyJwt(value)
                .WithScopes("orders.read");
            var secret = builder.AddParameter("fixture-jwk", () => value, secret: true);
            provider
                .AddClientCredentialsClient("parameter-jwk")
                .WithBase64PrivateKeyJwt(secret, publicJwk)
                .WithScopes("orders.read");
            provider
                .AddClientCredentialsClient("legacy-pem")
                .WithPrivateKeyJwt(rsa.ExportPkcs8PrivateKeyPem())
                .WithScopes("orders.read");
            using var other = RSA.Create(2048);
            provider
                .AddClientCredentialsClient("mismatched-jwk")
                .WithBase64PrivateKeyJwt(secret, TestKeys.Public(other, "fixture-key"))
                .WithScopes("orders.read");
        }
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
            PrivateKeyBase64 = registration.PrivateKeyBase64 is { } base64Key
                ? await base64Key.Resource.GetValueAsync(default)
                : null,
            ClientSecret = registration.ClientSecret is { } secret
                ? await secret.Resource.GetValueAsync(default)
                : null,
            KeyId = registration.KeyId,
            Scopes = string.Join(' ', registration.Scopes),
            TokenExchangeAudience = registration.TokenExchangeAudience,
        };
    }

    public async ValueTask DisposeAsync()
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
