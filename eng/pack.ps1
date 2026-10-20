param([string]$Version = '0.1.0-alpha.1')
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
Push-Location $repo
try {
    foreach ($project in @('Aspire.IdentityServer.Server', 'Aspire.IdentityServer.Hosting', 'Aspire.IdentityServer.Client')) {
        dotnet pack "src/$project" -c Release -o artifacts/packages "/p:Version=$Version"
        if ($LASTEXITCODE -ne 0) { throw "Packing $project failed." }
    }
    # Only our assemblies belong in these archives; dependencies remain NuGet references.
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    foreach ($name in @('TheDotSide.Aspire.IdentityServer.Runtime', 'TheDotSide.Aspire.IdentityServer.Hosting', 'TheDotSide.Aspire.IdentityServer.Client')) {
        $archive = [System.IO.Compression.ZipFile]::OpenRead((Join-Path $repo "artifacts/packages/$name.$Version.nupkg"))
        try {
            foreach ($entry in $archive.Entries) {
                if ($entry.FullName.EndsWith('.dll') -and !$entry.Name.StartsWith('Aspire.IdentityServer.')) {
                    throw "Unexpected bundled dependency: $($entry.FullName)"
                }
            }
        } finally { $archive.Dispose() }
    }
} finally { Pop-Location }
