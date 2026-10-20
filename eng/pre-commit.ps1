$ErrorActionPreference = 'Stop'

try {
    $repo = & git rev-parse --show-toplevel
    if ($LASTEXITCODE -ne 0) { throw 'Cannot locate the Git repository.' }
    Push-Location $repo
    try {
        $staged = & git diff --cached --name-only --diff-filter=ACMR -z
        if ($LASTEXITCODE -ne 0) { throw 'Cannot read staged files.' }
        $files = @(($staged -join "`n") -split "`0" | Where-Object {
            $_ -match '\.(cs|csx|csproj|props|targets|xml|config|slnx)$'
        })
        if ($files.Count -eq 0) { exit 0 }

        # Never include unstaged hunks in a commit when re-staging formatted files.
        foreach ($file in $files) {
            & git diff --quiet -- $file
            if ($LASTEXITCODE -eq 1) {
                throw "Partially staged file: $file. Format it and review/re-stage your intended changes before committing."
            }
            if ($LASTEXITCODE -ne 0) { throw "Cannot inspect unstaged changes: $file" }
            if (!(Test-Path -LiteralPath $file -PathType Leaf)) {
                throw "Staged file is missing from the working tree: $file"
            }
        }

        & dotnet tool restore
        if ($LASTEXITCODE -ne 0) { throw 'Cannot restore the local CSharpier tool.' }
        & dotnet csharpier format @files
        if ($LASTEXITCODE -ne 0) { throw 'CSharpier failed; the commit was stopped.' }
        & git add -- @files
        if ($LASTEXITCODE -ne 0) { throw 'Cannot stage the formatted files.' }
    } finally {
        Pop-Location
    }
} catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
}
