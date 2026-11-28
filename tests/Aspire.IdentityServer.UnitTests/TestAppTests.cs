using Microsoft.Extensions.Configuration;

namespace Aspire.IdentityServer.Tests;

public sealed class TestAppTests
{
    [Test]
    public async Task Test_builders_watch_no_configuration_file()
    {
        // Unit tests build application models in parallel and never dispose them: a file watcher
        // per builder exhausts the 128 inotify instances of a Linux runner, and the providers of
        // the distributed tests, which run at the same time, then fail to start.
        var watched = ((IConfigurationRoot)TestApp.CreateBuilder().Configuration)
            .Providers.OfType<FileConfigurationProvider>()
            .Where(p => p.Source.ReloadOnChange)
            .Select(p => p.Source.Path)
            .ToArray();
        await Assert.That(watched).IsEmpty();
    }
}
