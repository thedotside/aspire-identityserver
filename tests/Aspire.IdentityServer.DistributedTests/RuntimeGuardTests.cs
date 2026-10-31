using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Aspire.IdentityServer.Tests;

/// <summary>Starts the packaged runtime directly, with hostile environment variables.</summary>
public sealed class RuntimeGuardTests
{
    private static readonly string Runtime = Path.Combine(
        AppContext.BaseDirectory,
        "identityserver",
        "Aspire.IdentityServer.Server.dll"
    );

    [Test]
    [Arguments("environment", "only runs in Development or Testing")]
    [Arguments("port", "Set ASPNETCORE_HTTPS_PORTS to a single port")]
    [Arguments("ports", "Set ASPNETCORE_HTTPS_PORTS to a single port")]
    [Arguments("endpoints", "Kestrel endpoints cannot be configured")]
    [Arguments("issuer", "loopback HTTPS address")]
    [Arguments("contract", "configuration contract")]
    public async Task The_runtime_refuses_to_start_outside_its_development_boundary(
        string change,
        string expected
    )
    {
        using var certificate = TemporaryCertificate.Create();
        var environment = Environment(FreePort(), certificate);
        switch (change)
        {
            case "environment":
                environment["ASPNETCORE_ENVIRONMENT"] = "Production";
                break;
            case "port":
                environment.Remove("ASPNETCORE_HTTPS_PORTS");
                break;
            case "ports":
                environment["ASPNETCORE_HTTPS_PORTS"] = "7001;7002";
                break;
            case "endpoints":
                environment["Kestrel__Endpoints__Public__Url"] = "http://0.0.0.0:0";
                break;
            case "issuer":
                environment["IdentityServer__Issuer"] = "https://idp.example";
                break;
            case "contract":
                environment.Remove("IdentityServer__ContractVersion");
                break;
        }
        using var process = Start(environment);
        var output = new StringBuilder();
        process.OutputDataReceived += (_, e) => Append(output, e.Data);
        process.ErrorDataReceived += (_, e) => Append(output, e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        await Assert.That(process.ExitCode).IsNotEqualTo(0);
        await Assert.That(Text(output)).Contains(expected);
    }

    [Test]
    public async Task Environment_urls_cannot_expose_the_runtime_beyond_loopback()
    {
        using var certificate = TemporaryCertificate.Create();
        var port = FreePort();
        var environment = Environment(port, certificate);
        environment["ASPNETCORE_URLS"] = $"https://0.0.0.0:{FreePort()};http://+:{FreePort()}";
        environment["ASPNETCORE_HTTP_PORTS"] = FreePort().ToString(CultureInfo.InvariantCulture);
        using var process = Start(environment);
        var output = new StringBuilder();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        process.OutputDataReceived += (_, e) =>
        {
            Append(output, e.Data);
            if (e.Data?.Contains("Application started", StringComparison.Ordinal) == true)
                started.TrySetResult();
        };
        process.ErrorDataReceived += (_, e) => Append(output, e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(60));
            var listening = Text(output)
                .Split('\n')
                .Where(l => l.Contains("Now listening on:", StringComparison.Ordinal))
                .Select(l => l[(l.IndexOf("Now listening on:", StringComparison.Ordinal) + 17)..])
                .Select(l => l.Trim())
                .ToArray();
            // ListenLocalhost logs one localhost address for its IPv4 and IPv6 loopback sockets.
            await Assert.That(listening).IsEquivalentTo([$"https://localhost:{port}"]);
        }
        finally
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }
    }

    [Test]
    public async Task The_runtime_exports_traces_and_logs_to_the_apphost_otlp_endpoint()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var received = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var receiverBuilder = WebApplication.CreateSlimBuilder();
        receiverBuilder.WebHost.UseUrls("http://127.0.0.1:0");
        receiverBuilder.Logging.ClearProviders();
        await using var receiver = receiverBuilder.Build();
        receiver.MapPost(
            "/v1/{signal}",
            (string signal) =>
            {
                received.Enqueue(signal);
                return Results.Ok();
            }
        );
        await receiver.StartAsync(timeout.Token);
        using var certificate = TemporaryCertificate.Create();
        var port = FreePort();
        var environment = Environment(port, certificate);
        environment["OTEL_EXPORTER_OTLP_ENDPOINT"] = receiver.Urls.Single();
        environment["OTEL_EXPORTER_OTLP_PROTOCOL"] = "http/protobuf";
        environment["OTEL_SERVICE_NAME"] = "guard";
        environment["OTEL_BSP_SCHEDULE_DELAY"] = "200";
        environment["OTEL_BLRP_SCHEDULE_DELAY"] = "200";
        using var process = Start(environment);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data?.Contains("Application started", StringComparison.Ordinal) == true)
                started.TrySetResult();
        };
        process.BeginOutputReadLine();
        try
        {
            await started.Task.WaitAsync(timeout.Token);
            // Chain validation against exactly the temporary certificate of this runtime.
            using var trusted = X509CertificateLoader.LoadPkcs12FromFile(
                certificate.Path,
                certificate.Password
            );
            using var handler = new SocketsHttpHandler();
            handler.SslOptions.CertificateChainPolicy = new X509ChainPolicy
            {
                TrustMode = X509ChainTrustMode.CustomRootTrust,
                RevocationMode = X509RevocationMode.NoCheck,
            };
            handler.SslOptions.CertificateChainPolicy.CustomTrustStore.Add(trusted);
            using var http = new HttpClient(handler);
            using var discovery = await http.GetAsync(
                $"https://localhost:{port}/.well-known/openid-configuration",
                timeout.Token
            );
            discovery.EnsureSuccessStatusCode();
            while (
                !(
                    received.Contains("traces", StringComparer.Ordinal)
                    && received.Contains("logs", StringComparer.Ordinal)
                ) && !timeout.IsCancellationRequested
            )
                await Task.Delay(100, CancellationToken.None);
            await Assert.That(received).Contains("traces");
            await Assert.That(received).Contains("logs");
        }
        finally
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            await receiver.StopAsync(CancellationToken.None);
        }
    }

    private static Dictionary<string, string?> Environment(int port, TemporaryCertificate cert) =>
        new(StringComparer.Ordinal)
        {
            ["ASPNETCORE_ENVIRONMENT"] = "Development",
            ["ASPNETCORE_HTTPS_PORTS"] = port.ToString(CultureInfo.InvariantCulture),
            ["Kestrel__Certificates__Default__Path"] = cert.Path,
            ["Kestrel__Certificates__Default__Password"] = cert.Password,
            ["IdentityServer__ContractVersion"] = "2",
            ["IdentityServer__Name"] = "guard",
            ["IdentityServer__Issuer"] = $"https://localhost:{port}",
        };

    private static Process Start(Dictionary<string, string?> environment)
    {
        var start = new ProcessStartInfo("dotnet", [Runtime])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        // Never inherit the test runner's hosting or provider settings.
        foreach (
            var key in start
                .Environment.Keys.Where(k =>
                    k.StartsWith("ASPNETCORE_", StringComparison.OrdinalIgnoreCase)
                    || k.StartsWith("DOTNET_ENVIRONMENT", StringComparison.OrdinalIgnoreCase)
                    || k.StartsWith("Kestrel__", StringComparison.OrdinalIgnoreCase)
                    || k.StartsWith("IdentityServer__", StringComparison.OrdinalIgnoreCase)
                )
                .ToList()
        )
            start.Environment.Remove(key);
        foreach (var (key, value) in environment)
            start.Environment[key] = value;
        return Process.Start(start)!;
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static void Append(StringBuilder output, string? line)
    {
        if (line is null)
            return;
        lock (output)
            output.AppendLine(line);
    }

    private static string Text(StringBuilder output)
    {
        lock (output)
            return output.ToString();
    }

    private sealed class TemporaryCertificate : IDisposable
    {
        private TemporaryCertificate(string path, string password, string thumbprint)
        {
            Path = path;
            Password = password;
            Thumbprint = thumbprint;
        }

        public string Path { get; }
        public string Password { get; }
        public string Thumbprint { get; }

        public static TemporaryCertificate Create()
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest(
                "CN=localhost",
                key,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1
            );
            var names = new SubjectAlternativeNameBuilder();
            names.AddDnsName("localhost");
            request.CertificateExtensions.Add(names.Build());
            using var certificate = request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddMinutes(-5),
                DateTimeOffset.UtcNow.AddHours(1)
            );
            var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"aspire-idp-guard-{Guid.NewGuid():N}.pfx"
            );
            File.WriteAllBytes(path, certificate.Export(X509ContentType.Pfx, password));
            return new TemporaryCertificate(path, password, certificate.Thumbprint);
        }

        public void Dispose() => File.Delete(Path);
    }
}
