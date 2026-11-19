using System.Globalization;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.IdentityServer.Hosting;

/// <summary>
/// Dashboard commands of a provider: its registrations without secrets, and a token for one of
/// its machine clients. Both run in the AppHost, against the provider's real endpoints.
/// </summary>
internal static class DashboardCommands
{
    internal const string Registrations = "show-registrations";
    internal const string RequestToken = "request-client-token";

    /// <summary>Adds the commands once every client is declared, before the provider starts.</summary>
    internal static void Add(IResourceBuilder<IdentityServerResource> server)
    {
        var resource = server.Resource;
        server.WithCommand(
            Registrations,
            "Show registrations",
            async context =>
                CommandResults.Success(
                    "Registrations",
                    await MarkdownAsync(resource, context.CancellationToken).ConfigureAwait(false),
                    CommandResultFormat.Markdown,
                    true
                ),
            new CommandOptions
            {
                Description =
                    "Lists the clients, API resources, identity scopes and test users of this provider, without secrets.",
                IconName = "TextBulletListSquare",
                UpdateState = WhenRunning,
            }
        );
        var machines = resource
            .Clients.Where(c =>
                string.Equals(c.GrantType, "client_credentials", StringComparison.Ordinal)
            )
            .ToList();
        if (machines.Count == 0)
            return;
        server.WithCommand(
            RequestToken,
            "Request a client token",
            context => RequestTokenAsync(resource, context),
            new CommandOptions
            {
                Description =
                    "Requests a client-credentials token for a machine client and shows it with its claims.",
                IconName = "Key",
                UpdateState = WhenRunning,
                Arguments =
                [
                    new InteractionInput
                    {
                        Name = "client",
                        Label = "Machine client",
                        InputType = InputType.Choice,
                        Required = true,
                        Options = machines
                            .Select(c => KeyValuePair.Create(c.ClientId, c.ClientId))
                            .ToList(),
                    },
                ],
            }
        );
    }

    private static ResourceCommandState WhenRunning(UpdateCommandStateContext context) =>
        string.Equals(
            context.ResourceSnapshot.State?.Text,
            KnownResourceStates.Running,
            StringComparison.Ordinal
        )
            ? ResourceCommandState.Enabled
            : ResourceCommandState.Disabled;

    private static async Task<ExecuteCommandResult> RequestTokenAsync(
        IdentityServerResource server,
        ExecuteCommandContext context
    )
    {
        var clientId = context.Arguments?["client"].Value;
        var client = server.Clients.SingleOrDefault(c =>
            string.Equals(c.ClientId, clientId, StringComparison.Ordinal)
            && string.Equals(c.GrantType, "client_credentials", StringComparison.Ordinal)
        );
        if (client is null)
            return CommandResults.Failure(
                $"'{clientId}' is not a machine client of {server.Name}."
            );
        try
        {
            using var http = new HttpClient();
            var metadata = await http.GetFromJsonAsync<JsonElement>(
                    client.Authority.Url + "/.well-known/openid-configuration",
                    context.CancellationToken
                )
                .ConfigureAwait(false);
            var issuer = metadata.GetProperty("issuer").GetString()!;
            var fields = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = client.ClientId,
                ["scope"] = string.Join(' ', client.Scopes),
            };
            await AuthenticateAsync(client, issuer, fields, context.CancellationToken)
                .ConfigureAwait(false);
            using var content = new FormUrlEncodedContent(fields);
            using var response = await http.PostAsync(
                    metadata.GetProperty("token_endpoint").GetString(),
                    content,
                    context.CancellationToken
                )
                .ConfigureAwait(false);
            var answer = await response
                .Content.ReadFromJsonAsync<JsonElement>(context.CancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return CommandResults.Failure(
                    $"The provider refused the token request of '{client.ClientId}': {Text(answer, "error")} {Text(answer, "error_description")}".TrimEnd()
                );
            var token = answer.GetProperty("access_token").GetString()!;
            using var claims = JsonDocument.Parse(Base64UrlDecode(token.Split('.')[1]));
            var shown = JsonSerializer.Serialize(
                new
                {
                    access_token = token,
                    token_type = Text(answer, "token_type"),
                    expires_in = answer.TryGetProperty("expires_in", out var lifetime)
                        ? lifetime.GetInt32()
                        : (int?)null,
                    scope = Text(answer, "scope"),
                    claims = claims.RootElement,
                },
                Indented
            );
            return CommandResults.Success(
                $"Token for {client.ClientId}",
                shown,
                CommandResultFormat.Json,
                true
            );
        }
        catch (Exception error) when (error is HttpRequestException or JsonException)
        {
            return CommandResults.Failure(error.Message);
        }
    }

    /// <summary>Client authentication as the client's own projection performs it.</summary>
    private static async Task AuthenticateAsync(
        IdentityClient client,
        string issuer,
        Dictionary<string, string> fields,
        CancellationToken cancellationToken
    )
    {
        if (client.ClientSecret is { } secret)
        {
            fields["client_secret"] =
                await secret.Resource.GetValueAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new HttpRequestException(
                    $"Client '{client.ClientId}' has no secret value."
                );
            return;
        }
        using var rsa = RSA.Create();
        string keyId;
        if (client.PrivateKeyBase64 is { } base64)
        {
            var value =
                await base64.Resource.GetValueAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new HttpRequestException($"Client '{client.ClientId}' has no key value.");
            var (parameters, kid) = RsaJwk.ReadPrivateBase64(value);
            try
            {
                rsa.ImportParameters(parameters);
            }
            finally
            {
                RsaJwk.Clear(parameters);
            }
            keyId = kid;
        }
        else if (client.PrivateKeyPem is { } pem)
        {
            rsa.ImportFromPem(
                await pem.Resource.GetValueAsync(cancellationToken).ConfigureAwait(false)
                    ?? throw new HttpRequestException(
                        $"Client '{client.ClientId}' has no key value."
                    )
            );
            keyId = client.KeyId!;
        }
        else
            throw new HttpRequestException($"Client '{client.ClientId}' has no credentials.");
        // draft-ietf-oauth-rfc7523bis, as the provider requires: the issuer as sole audience.
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var header = Base64UrlEncode(
            JsonSerializer.SerializeToUtf8Bytes(
                new
                {
                    alg = "RS256",
                    typ = "client-authentication+jwt",
                    kid = keyId,
                }
            )
        );
        var payload = Base64UrlEncode(
            JsonSerializer.SerializeToUtf8Bytes(
                new
                {
                    iss = client.ClientId,
                    sub = client.ClientId,
                    aud = issuer,
                    jti = Guid.NewGuid().ToString("N"),
                    iat = now,
                    nbf = now - 5,
                    exp = now + 60,
                }
            )
        );
        var signature = rsa.SignData(
            Encoding.ASCII.GetBytes($"{header}.{payload}"),
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1
        );
        fields["client_assertion_type"] = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer";
        fields["client_assertion"] = $"{header}.{payload}.{Base64UrlEncode(signature)}";
    }

    private static async Task<string> MarkdownAsync(
        IdentityServerResource server,
        CancellationToken cancellationToken
    )
    {
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"## {Cell(server.Name)}\n\n");
        text.Append(
            "### Clients\n\n| Client | Grant | Authentication | Scopes | Redirect URIs |\n| --- | --- | --- | --- | --- |\n"
        );
        foreach (var client in server.Clients)
        {
            var redirects = new List<string>();
            foreach (var redirect in client.RedirectUris)
                redirects.Add(
                    await redirect.GetValueAsync(cancellationToken).ConfigureAwait(false) ?? ""
                );
            text.Append(
                CultureInfo.InvariantCulture,
                $"| {Cell(client.ClientId)} | {client.GrantType} | {client.AuthenticationMethod} | {Cell(string.Join(' ', client.Scopes))} | {Cell(string.Join(' ', redirects))} |\n"
            );
        }
        text.Append("\n### API resources\n\n| Audience | Scopes |\n| --- | --- |\n");
        foreach (var api in server.Apis)
            text.Append(
                CultureInfo.InvariantCulture,
                $"| {Cell(api.Audience)} | {Cell(string.Join(' ', api.Scopes))} |\n"
            );
        text.Append("\n### Identity scopes\n\n| Scope | User claims |\n| --- | --- |\n");
        foreach (var scope in server.IdentityScopes)
            text.Append(
                CultureInfo.InvariantCulture,
                $"| {Cell(scope.Name)} | {Cell(string.Join(' ', scope.UserClaims))} |\n"
            );
        text.Append(
            "\n### Test users\n\n| Username | Subject | Display name | Claims |\n| --- | --- | --- | --- |\n"
        );
        foreach (var user in server.Users)
            text.Append(
                CultureInfo.InvariantCulture,
                $"| {Cell(user.Username)} | {Cell(user.SubjectId)} | {Cell(user.DisplayName)} | {Cell(string.Join(", ", user.Claims.Select(c => $"{c.Key}={c.Value}")))} |\n"
            );
        text.Append(
            "\nSecrets, keys and passwords are Aspire secret parameters: see the parameters of this provider.\n"
        );
        return text.ToString();
    }

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private static string Cell(string value) =>
        value.Replace("|", "\\|", StringComparison.Ordinal).Replace('\n', ' ');

    private static string? Text(JsonElement payload, string name) =>
        payload.ValueKind == JsonValueKind.Object
        && payload.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
    {
        var base64 = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(
            base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '=')
        );
    }
}
