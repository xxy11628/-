param(
    [string]$Configuration = "Debug",
    [switch]$SkipRestore
)

# M2 quality gate (docs/spec/07 §6). Merge only when this script passes.
# On a machine without network access, restore first through whatever local feed/source is available
# and then run with -SkipRestore.
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    if (-not $SkipRestore) {
        dotnet restore UltrasoundHost.sln
        if ($LASTEXITCODE -ne 0) { throw "restore failed" }
    }

    dotnet build UltrasoundHost.sln -c $Configuration --no-restore -warnaserror
    if ($LASTEXITCODE -ne 0) { throw "build failed" }

    dotnet test UltrasoundHost.sln -c $Configuration --no-build --no-restore
    if ($LASTEXITCODE -ne 0) { throw "tests failed" }

    dotnet format UltrasoundHost.sln --verify-no-changes --no-restore
    if ($LASTEXITCODE -ne 0) { throw "format check failed" }

    Write-Host "verify: OK"
}
finally {
    Pop-Location
}
