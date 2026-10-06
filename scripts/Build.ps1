[CmdletBinding()]
param(
    [ValidateSet('Debug','Release')][string]$Configuration = 'Release',
    [string]$GccPath = '',
    [switch]$SkipDependencyDownload,
    [switch]$SkipTests
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    if (-not $SkipDependencyDownload) { & "$PSScriptRoot/Get-Dependencies.ps1" }
    & "$PSScriptRoot/Build-Native.ps1" -GccPath $GccPath
    dotnet restore Diga.sln --nologo
    if ($LASTEXITCODE -ne 0) { throw 'NuGet restore failed.' }
    if (-not $SkipTests) {
        dotnet test tests/Diga.Tests/Diga.Tests.csproj -c $Configuration --nologo --logger 'trx;LogFileName=unit.trx' --results-directory artifacts/test-results --filter 'Category!=Integration'
        if ($LASTEXITCODE -ne 0) { throw 'Unit tests failed.' }
        dotnet test tests/Diga.Tests/Diga.Tests.csproj -c $Configuration --no-build --nologo --logger 'trx;LogFileName=integration.trx' --results-directory artifacts/test-results --filter 'Category=Integration'
        if ($LASTEXITCODE -ne 0) { throw 'Integration tests failed.' }
    }
    dotnet build src/Diga.App/Diga.App.csproj -c $Configuration -p:Platform=x64 --no-restore --nologo
    if ($LASTEXITCODE -ne 0) { throw 'WinUI build failed.' }
}
finally { Pop-Location }
