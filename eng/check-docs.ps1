# Checks that the versions the documentation states match the repository files. Tool versions
# that Renovate bumps are not cited in the guides; these ones change deliberately.
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$version = ([xml](Get-Content (Join-Path $repo 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
$aspire = (([xml](Get-Content (Join-Path $repo 'Directory.Packages.props') -Raw)).Project.ItemGroup.PackageVersion |
    Where-Object Include -eq 'Aspire.Hosting').Version
$expectations = @(
    @{ File = 'README.md'; Text = "Version: ``$version``" }
    @{ File = 'README.md'; Text = "Aspire $aspire or later" }
    @{ File = 'docs/README.md'; Text = "version ``$version``, sur .NET 10 et .NET 11 et Aspire $aspire ou ultérieur" }
    @{ File = 'docs/demarrage.md'; Text = "Aspire ``$aspire`` ou ultérieur" }
    @{ File = 'docs/demarrage.md'; Text = "<PackageReference Include=`"Aspire.Hosting.AppHost`" Version=`"$aspire`" />" }
    @{ File = 'docs/demarrage.md'; Text = "Include=`"TheDotSide.Aspire.IdentityServer.Hosting`" Version=`"$version`"" }
    @{ File = 'docs/tests.md'; Text = "<PackageReference Include=`"Aspire.Hosting.Testing`" Version=`"$aspire`" />" }
)
$failures = @(foreach ($expectation in $expectations) {
    $content = Get-Content (Join-Path $repo $expectation.File) -Raw
    if (!$content.Contains($expectation.Text)) { "$($expectation.File) must state: $($expectation.Text)" }
})
if ($failures) { $failures | ForEach-Object { Write-Error $_ -ErrorAction Continue }; exit 1 }
Write-Host "Documented versions match: package $version, Aspire floor $aspire."
