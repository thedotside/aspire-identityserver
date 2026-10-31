using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Aspire.IdentityServer.Client;
using Aspire.IdentityServer.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Playwright;
using TUnit.Core.Interfaces;

namespace Aspire.IdentityServer.Tests;

/// <summary>One real sample AppHost and browser, shared by every distributed test class.</summary>
public sealed class AppFixture : IAsyncInitializer, IAsyncDisposable
{
    private static readonly string[] Required = ["idp-a", "idp-b", "idp-legacy", "web", "api"];
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
        var provider = builder.CreateResourceBuilder(
            builder
                .Resources.OfType<IdentityServerResource>()
                .Single(s => string.Equals(s.Name, "idp-a", StringComparison.Ordinal))
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
        var legacy = builder.AddIdentityServer(
            "idp-legacy",
            options => options.AcceptTokenEndpointAudience = true
        );
        legacy.AddApiResource("legacy-api").WithScopes("legacy.read");
        legacy
            .AddClientCredentialsClient("legacy-worker")
            .WithGeneratedBase64PrivateKeyJwt()
            .WithScopes("legacy.read");
        Servers = builder.Resources.OfType<IdentityServerResource>().ToList();
        App = await builder.BuildAsync(timeout.Token);
        await App.StartAsync(timeout.Token);
        await WaitUntilHealthyAsync(timeout.Token);
        StartupTime = watch.Elapsed;
        // One budget for every browser expectation, including the shared user-token helper.
        Assertions.SetDefaultExpectTimeout(30_000);
        // API requests run in Playwright's Node.js driver, which ignores the operating system's
        // certificate store, where the development certificate is trusted, unless asked to.
        var nodeOptions = Environment.GetEnvironmentVariable("NODE_OPTIONS");
        if (nodeOptions?.Contains("--use-system-ca", StringComparison.Ordinal) != true)
            Environment.SetEnvironmentVariable(
                "NODE_OPTIONS",
                string.IsNullOrWhiteSpace(nodeOptions)
                    ? "--use-system-ca"
                    : nodeOptions + " --use-system-ca"
            );
        playwright = await Playwright.CreateAsync();
        Browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
    }

    /// <summary>
    /// Fails as soon as a resource stops or its HTTPS health check rejects the certificate,
    /// with the resource states and their last log lines, instead of waiting for the timeout.
    /// </summary>
    private async Task WaitUntilHealthyAsync(CancellationToken cancellationToken)
    {
        var latest = new Dictionary<string, CustomResourceSnapshot>(StringComparer.Ordinal);
        try
        {
            await foreach (var change in App.ResourceNotifications.WatchAsync(cancellationToken))
            {
                var name = change.Resource.Name;
                if (!Required.Contains(name, StringComparer.Ordinal))
                    continue;
                var snapshot = change.Snapshot;
                latest[name] = snapshot;
                if (
                    snapshot.State?.Text is { } state
                    && KnownResourceStates.TerminalStates.Contains(state, StringComparer.Ordinal)
                )
                    throw await StartupFailure(
                        $"'{name}' stopped in state {state} (exit code {snapshot.ExitCode}).",
                        latest
                    );
                if (snapshot.HealthReports.Any(r => IsCertificateError(r.ExceptionText)))
                    throw await StartupFailure(
                        $"The HTTPS health check of '{name}' rejects the development certificate. "
                            + "Trust it with `dotnet dev-certs https --trust` (or start the sample "
                            + "once with `aspire run`, which prepares it), then run the tests again.",
                        latest
                    );
                if (
                    Required.All(r =>
                        latest.TryGetValue(r, out var current)
                        && current.HealthStatus == HealthStatus.Healthy
                    )
                )
                    return;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw await StartupFailure(
                "The sample AppHost did not become healthy before the startup timeout.",
                latest
            );
        }
    }

    private async Task<InvalidOperationException> StartupFailure(
        string reason,
        Dictionary<string, CustomResourceSnapshot> states
    )
    {
        var report = new StringBuilder(reason).AppendLine();
        var logs = App.Services.GetRequiredService<ResourceLoggerService>();
        var model = App.Services.GetRequiredService<DistributedApplicationModel>();
        foreach (var name in Required)
        {
            report.Append(CultureInfo.InvariantCulture, $"- {name}: ");
            if (states.TryGetValue(name, out var snapshot))
            {
                report.Append(
                    CultureInfo.InvariantCulture,
                    $"{snapshot.State?.Text ?? "unknown"}, health {snapshot.HealthStatus?.ToString() ?? "unknown"}"
                );
                foreach (
                    var health in snapshot.HealthReports.Where(h => h.ExceptionText is not null)
                )
                    report.Append(
                        CultureInfo.InvariantCulture,
                        $"; {health.Name}: {FirstLine(health.ExceptionText!)}"
                    );
            }
            else
                report.Append("no state received");
            report.AppendLine();
            // Startup logs never contain credentials: the provider filters sensitive values.
            var lines = new List<string>();
            using var collect = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                var resource = model.Resources.Single(r =>
                    string.Equals(r.Name, name, StringComparison.Ordinal)
                );
                await foreach (
                    var batch in logs.GetAllAsync(resource).WithCancellation(collect.Token)
                )
                    lines.AddRange(batch.Select(l => l.Content));
            }
            catch (OperationCanceledException) when (collect.IsCancellationRequested) { }
            foreach (var line in lines.TakeLast(20))
                report.Append("    ").AppendLine(line);
        }
        return new InvalidOperationException(report.ToString());
    }

    private static bool IsCertificateError(string? exception) =>
        exception is not null
        && (
            exception.Contains("certificate", StringComparison.OrdinalIgnoreCase)
            || exception.Contains("SSL connection", StringComparison.OrdinalIgnoreCase)
        );

    private static string FirstLine(string value) =>
        value.Split('\n', 2, StringSplitOptions.TrimEntries)[0];

    /// <summary>A new isolated browser context; TLS errors are never ignored.</summary>
    public Task<BrowserSession> NewBrowserSessionAsync() => BrowserSession.StartAsync(Browser);

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
