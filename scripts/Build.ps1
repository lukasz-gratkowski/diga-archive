[CmdletBinding()]
param(
    [ValidateSet('Debug','Release')][string]$Configuration = 'Release',
    [switch]$SkipDependencyDownload,
    [switch]$SkipTests
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    if (-not $SkipDependencyDownload) { & "$PSScriptRoot/Get-Dependencies.ps1" }
    # Locked mode: every package must be the version and the content recorded in the projects' packages.lock.json, taken from
    # the one feed nuget.config names. The configuration is passed because a Release restore also fails on a package with a
    # known high or critical vulnerability (Directory.Build.props). Nothing below restores again.
    dotnet restore Diga.sln --locked-mode "-p:Configuration=$Configuration" --nologo
    if ($LASTEXITCODE -ne 0) { throw 'NuGet restore failed. After a deliberate package change, run "dotnet restore Diga.sln" and commit the packages.lock.json files it rewrites.' }
    if (-not $SkipTests) {
        dotnet test tests/Diga.Tests/Diga.Tests.csproj -c $Configuration --no-restore --nologo --logger 'trx;LogFileName=unit.trx' --results-directory artifacts/test-results --filter 'Category!=Integration'
        if ($LASTEXITCODE -ne 0) { throw 'Unit tests failed.' }
        dotnet test tests/Diga.Tests/Diga.Tests.csproj -c $Configuration --no-build --nologo --logger 'trx;LogFileName=integration.trx' --results-directory artifacts/test-results --filter 'Category=Integration'
        if ($LASTEXITCODE -ne 0) { throw 'Integration tests failed.' }
    }
    dotnet build src/Diga.App/Diga.App.csproj -c $Configuration -p:Platform=x64 --no-restore --nologo
    if ($LASTEXITCODE -ne 0) { throw 'WinUI build failed.' }
}
finally { Pop-Location }
