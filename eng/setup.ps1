$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
Push-Location $repo
try {
    & dotnet tool restore
    if ($LASTEXITCODE -ne 0) { throw 'Could not restore local .NET tools.' }

    $hooksPath = & git config --local --get core.hooksPath
    if ($LASTEXITCODE -notin 0, 1) { throw 'Could not read the local Git hook configuration.' }
    if ($hooksPath -and $hooksPath -ne '.githooks') {
        throw "An existing core.hooksPath is configured: $hooksPath. Integrate the CSharpier hook with that setup first."
    }
    $defaultHook = & git rev-parse --git-path hooks/pre-commit
    if ($LASTEXITCODE -ne 0) { throw 'Could not locate the default Git hook directory.' }
    if (!$hooksPath -and (Test-Path -LiteralPath $defaultHook)) {
        throw 'An existing default pre-commit hook is installed. Integrate it before enabling .githooks.'
    }
    & git config --local core.hooksPath .githooks
    if ($LASTEXITCODE -ne 0) { throw 'Could not enable the repository Git hooks.' }
    Write-Host 'Local tools restored; .githooks/pre-commit enabled for this checkout.'
} finally {
    Pop-Location
}
