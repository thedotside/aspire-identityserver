using System.Collections.Concurrent;
using System.Diagnostics.Tracing;
using System.Security.Cryptography;
using System.Text;
using Aspire.IdentityServer.Client;
using Microsoft.IdentityModel.Logging;

namespace Aspire.IdentityServer.Tests;

// IdentityModel's diagnostic switches and event source are process-wide.
[NotInParallel]
public sealed class PrivateKeyDiagnosticsTests
{
    [Test]
    public async Task Private_input_does_not_enter_identitymodel_logs_even_when_sensitive_diagnostics_are_enabled()
    {
        using var rsa = RSA.Create(2048);
        var value = TestKeys.Encode(rsa, "diagnostics-key");
        var raw = Encoding.UTF8.GetString(Convert.FromBase64String(value));
        using var listener = new IdentityModelListener();
        var previousPii = IdentityModelEventSource.ShowPII;
        var previousArtifacts = IdentityModelEventSource.LogCompleteSecurityArtifact;
        try
        {
            IdentityModelEventSource.ShowPII = true;
            IdentityModelEventSource.LogCompleteSecurityArtifact = true;
            foreach (
                var input in new[]
                {
                    value,
                    Convert.ToBase64String(Encoding.UTF8.GetBytes(raw + "private-tail")),
                    "invalid-private-base64!",
                }
            )
            {
                var options = new IdentityClientOptions
                {
                    ClientId = "worker",
                    PrivateKeyBase64 = input,
                };
                if (string.Equals(input, value, StringComparison.Ordinal))
                    _ = PrivateKeyJwt.CreateAssertion(options, "https://localhost/connect/token");
                else
                {
                    var error = Assert.ThrowsExactly<ArgumentException>(() =>
                        PrivateKeyJwt.CreateAssertion(options, "https://localhost/connect/token")
                    );
                    await Assert.That(error.InnerException).IsNull();
                    await Assert
                        .That(error.ToString().Contains(input, StringComparison.Ordinal))
                        .IsFalse();
                    await Assert
                        .That(error.ToString().Contains(raw, StringComparison.Ordinal))
                        .IsFalse();
                }
            }
            using var document = System.Text.Json.JsonDocument.Parse(raw);
            var sensitive = new[] { value, raw, "invalid-private-base64!" }.Concat(
                new[] { "d", "p", "q", "dp", "dq", "qi" }.Select(n =>
                    document.RootElement.GetProperty(n).GetString()!
                )
            );
            var leaked = listener.Messages.Any(m =>
                sensitive.Any(s => m.Contains(s, StringComparison.Ordinal))
            );
            await Assert.That(leaked).IsFalse(); // Never print captured credentials on failure.
        }
        finally
        {
            IdentityModelEventSource.ShowPII = previousPii;
            IdentityModelEventSource.LogCompleteSecurityArtifact = previousArtifacts;
        }
    }

    private sealed class IdentityModelListener : EventListener
    {
        internal ConcurrentQueue<string> Messages { get; } = new();

        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (
                string.Equals(
                    eventSource.Name,
                    "Microsoft.IdentityModel.EventSource",
                    StringComparison.Ordinal
                )
            )
                EnableEvents(eventSource, EventLevel.Verbose);
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            if (eventData.Payload is not null)
                foreach (var value in eventData.Payload)
                    Messages.Enqueue(value?.ToString() ?? "");
        }
    }
}
