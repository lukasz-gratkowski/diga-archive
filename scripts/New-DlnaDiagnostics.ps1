[CmdletBinding()]
param([string]$OutputDirectory = '')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $root 'artifacts/diagnostics' }
[xml]$properties = Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props')
$version = [string]$properties.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Version must have three numeric parts.' }
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$stage = Join-Path $root ('artifacts/diagnostics-package-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Test-DlnaRecorder.ps1'), (Join-Path $PSScriptRoot 'Run-DlnaDiagnostics.cmd') -Destination $stage
Copy-Item -LiteralPath (Join-Path $root 'docs/DLNA-DIAGNOSTICS.md') -Destination (Join-Path $stage 'README.md')
Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination $stage
$commit = & git -C $root rev-parse HEAD
if ($LASTEXITCODE -ne 0) { throw 'Package assembly requires Git source provenance.' }
$dirty = @(& git -C $root status --porcelain -- Directory.Build.props LICENSE scripts/New-DlnaDiagnostics.ps1 scripts/Test-DlnaRecorder.ps1 scripts/Run-DlnaDiagnostics.cmd docs/DLNA-DIAGNOSTICS.md)
if ($LASTEXITCODE -ne 0) { throw 'Could not inspect diagnostic source provenance.' }
@{ Version=$version; Commit=$commit; ModifiedSources=($dirty.Count -gt 0); BuiltUtc=[DateTime]::UtcNow.ToString('o') } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $stage 'BUILDINFO.json') -Encoding utf8
$files = @(Get-ChildItem -LiteralPath $stage -File)
$files | ForEach-Object {
    '{0}  {1}' -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name
} | Set-Content -LiteralPath (Join-Path $stage 'SHA256SUMS.txt') -Encoding utf8
$destination = Join-Path $OutputDirectory "DIGA-$version-dlna-diagnostics.zip"
# No overwrite: the caller chooses a fresh directory for each verified package.
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $destination
Write-Host "Standalone diagnostic package: $([IO.Path]::GetFullPath($destination))"
