using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;

namespace Aspire.IdentityServer.Tests;

public sealed class StartupValidationTests
{
    [Test]
    public async Task Registration_rules_fail_the_apphost_before_any_provider_starts()
    {
        await using var builder =
            await DistributedApplicationTestingBuilder.CreateAsync<Projects.Sample_AppHost>([
                "DcpPublisher:RandomizePorts=true",
            ]);
        builder
            .CreateResourceBuilder(
                builder
                    .Resources.OfType<IdentityServerResource>()
                    .Single(r => string.Equals(r.Name, "idp-a", StringComparison.Ordinal))
            )
            .AddClientCredentialsClient("invalid")
            .WithGeneratedClientSecret()
            .WithScopes("undeclared");
        await using var app = await builder.BuildAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        // The host aggregates startup failures; the rule's own message must be among them.
        var error = await Assert.ThrowsAsync<AggregateException>(() =>
            app.StartAsync(timeout.Token)
        );
        await Assert
            .That(error!.Flatten().InnerExceptions.OfType<InvalidOperationException>())
            .Contains(e => e.Message.Contains("declared API scopes", StringComparison.Ordinal));
    }
}
