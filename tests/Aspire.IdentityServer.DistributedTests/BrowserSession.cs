using System.Collections.Concurrent;
using Microsoft.Playwright;

namespace Aspire.IdentityServer.Tests;

/// <summary>
/// An isolated browser context that validates TLS and records a Playwright trace. The trace is
/// kept in the test results, and attached to the report, only when the test fails.
/// </summary>
public sealed class BrowserSession : IAsyncDisposable
{
    private static readonly ConcurrentDictionary<string, ConcurrentQueue<string>> Pending = new(
        StringComparer.Ordinal
    );
    private readonly string testId;

    private BrowserSession(IBrowserContext context, string testId)
    {
        Context = context;
        this.testId = testId;
    }

    public IBrowserContext Context { get; }

    internal static async Task<BrowserSession> StartAsync(IBrowser browser)
    {
        var context = await browser.NewContextAsync();
        await context.Tracing.StartAsync(new() { Screenshots = true, Snapshots = true });
        return new BrowserSession(context, TestContext.Current!.Id);
    }

    public async ValueTask DisposeAsync()
    {
        // A trace must be saved before its context closes; the hook below decides to keep it.
        var path = Path.Combine(Path.GetTempPath(), $"aspire-idp-trace-{Guid.NewGuid():N}.zip");
        await Context.Tracing.StopAsync(new() { Path = path });
        await Context.DisposeAsync();
        Pending.GetOrAdd(testId, _ => new()).Enqueue(path);
    }

    [AfterEvery(HookType.Test)]
    public static Task KeepTracesOfFailedTests(TestContext context)
    {
        if (!Pending.TryRemove(context.Id, out var traces))
            return Task.CompletedTask;
        var failed = context.Execution.Result?.State is TestState.Failed or TestState.Timeout;
        var directory = Path.Combine(TestContext.ResultsDirectory, "playwright");
        var index = 0;
        while (traces.TryDequeue(out var trace))
        {
            if (!failed)
            {
                File.Delete(trace);
                continue;
            }
            Directory.CreateDirectory(directory);
            var name = string.Concat(
                context.Metadata.TestName.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-')
            );
            var target = Path.Combine(
                directory,
                $"{name}-{index++}-{Path.GetFileNameWithoutExtension(trace)}.zip"
            );
            File.Move(trace, target, overwrite: true);
            context.Output.AttachArtifact(
                target,
                "Playwright trace",
                "Open it with: pwsh playwright.ps1 show-trace <file>"
            );
        }
        return Task.CompletedTask;
    }
}
