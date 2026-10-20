param([string]$Version = '0.1.0-alpha.1')
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$feed = Join-Path $repo 'artifacts/packages'
if (!(Test-Path (Join-Path $feed "TheDotSide.Aspire.IdentityServer.Hosting.$Version.nupkg"))) {
    throw 'Build the packages with eng/pack.ps1 first.'
}
# Use a unique directory and NuGet cache so a prior build cannot mask a broken package.
$consumer = Join-Path ([System.IO.Path]::GetTempPath()) ("aspire-identityserver-smoke-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $consumer | Out-Null
$escapedFeed = [System.Security.SecurityElement]::Escape($feed)
@"
<configuration><packageSources><clear/><add key="local" value="$escapedFeed"/><add key="nuget" value="https://api.nuget.org/v3/index.json"/></packageSources></configuration>
"@ | Set-Content (Join-Path $consumer 'NuGet.Config')
'<Project />' | Set-Content (Join-Path $consumer 'Directory.Build.props')
'<Project />' | Set-Content (Join-Path $consumer 'Directory.Build.targets')
'<Project><PropertyGroup><ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally></PropertyGroup></Project>' |
    Set-Content (Join-Path $consumer 'Directory.Packages.props')
@"
<Project Sdk="Microsoft.NET.Sdk">
  <Sdk Name="Aspire.AppHost.Sdk" Version="13.6.1" />
  <PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><IsAspireHost>true</IsAspireHost><AspireUseCliBundle>true</AspireUseCliBundle></PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Aspire.Hosting.AppHost" Version="13.6.1" />
    <PackageReference Include="TheDotSide.Aspire.IdentityServer.Hosting" Version="$Version" />
    <PackageReference Include="TheDotSide.Aspire.IdentityServer.Client" Version="$Version" />
  </ItemGroup>
</Project>
"@ | Set-Content (Join-Path $consumer 'Consumer.csproj')
@'
using System.Net.Http.Json;
using System.Text.Json;
using Aspire.IdentityServer.Hosting;
using Aspire.IdentityServer.Client;
var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions { Args = args, DisableDashboard = true });
var idp = builder.AddIdentityServer("package-idp");
idp.AddApiResource("package-api").WithScopes("read");
var client = idp.AddClientCredentialsClient("package-client").WithGeneratedPrivateKeyJwt().WithScopes("read");
await using var app = builder.Build();
using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
await app.StartAsync(timeout.Token);
try
{
    await app.ResourceNotifications.WaitForResourceHealthyAsync("package-idp", timeout.Token);
    using var http = new HttpClient();
    var metadata = await http.GetFromJsonAsync<JsonElement>($"{client.Authority.Url}/.well-known/openid-configuration", timeout.Token);
    var endpoint = metadata.GetProperty("token_endpoint").GetString()!;
    var options = new IdentityClientOptions { ClientId = client.ClientId, KeyId = client.KeyId, PrivateKeyPem = await client.PrivateKeyPem!.Resource.GetValueAsync(timeout.Token) };
    using var response = await http.PostAsync(endpoint, new FormUrlEncodedContent(new Dictionary<string, string>
    {
        ["grant_type"] = "client_credentials", ["scope"] = "read", ["client_id"] = client.ClientId,
        ["client_assertion_type"] = PrivateKeyJwt.AssertionType, ["client_assertion"] = PrivateKeyJwt.CreateAssertion(options, endpoint)
    }), timeout.Token);
    response.EnsureSuccessStatusCode();
    var token = await response.Content.ReadFromJsonAsync<JsonElement>(timeout.Token);
    if (string.IsNullOrWhiteSpace(token.GetProperty("access_token").GetString())) throw new Exception("Missing access token.");
    Console.WriteLine("PACKAGE_SMOKE_OK: packaged provider started and authenticated the packaged client helper.");
}
finally { await app.StopAsync(CancellationToken.None); }
'@ | Set-Content (Join-Path $consumer 'Program.cs')
Push-Location $consumer
try {
    dotnet restore --packages (Join-Path $consumer 'packages')
    if ($LASTEXITCODE -ne 0) { throw 'Package consumer restore failed.' }
    $assets = Get-Content (Join-Path $consumer 'obj/project.assets.json') -Raw | ConvertFrom-Json
    $restoredLibraries = $assets.libraries.PSObject.Properties.Name
    foreach ($name in @('TheDotSide.Aspire.IdentityServer.Hosting', 'TheDotSide.Aspire.IdentityServer.Runtime', 'TheDotSide.Aspire.IdentityServer.Client')) {
        if ($restoredLibraries -notcontains "$name/$Version") { throw "The renamed package $name was not restored." }
    }
    if ($restoredLibraries -match '^Aspire\.IdentityServer\.(Hosting|Runtime|Client)/') {
        throw 'The consumer still depends on an old IdentityServer package identifier.'
    }
    dotnet build --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Package consumer build failed.' }
    # Direct execution keeps the finite smoke check independent of the interactive Aspire CLI.
    $runOutput = & dotnet (Join-Path $consumer 'bin/Debug/net10.0/Consumer.dll') 2>&1
    $runResult = $LASTEXITCODE
    $runOutput | Write-Output
    if ($runResult -ne 0 -or !($runOutput -match 'PACKAGE_SMOKE_OK:')) { throw 'Package consumer failed.' }
    Write-Host "Verified independent consumer: $consumer"
} finally { Pop-Location }
