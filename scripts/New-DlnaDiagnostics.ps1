[CmdletBinding()]
param([string]$OutputDirectory = '',
    # The folder to take Test-DlnaRecorder.ps1 and Run-DlnaDiagnostics.cmd from, and README.md if it holds one. New-Release.ps1
    # passes the payload's diagnostics folder, so that a signed release puts the same signed script, with the launcher and the
    # instructions that go with it, into the kit as into the installer. Default: this scripts folder and docs/DLNA-DIAGNOSTICS.md.
    [string]$SourceDirectory = '')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $root 'artifacts/diagnostics' }
if (-not $SourceDirectory) { $SourceDirectory = $PSScriptRoot }
[xml]$properties = Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props')
$version = [string]$properties.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Version must have three numeric parts.' }
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$stage = Join-Path $root ('artifacts/diagnostics-package-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage | Out-Null
Copy-Item -LiteralPath (Join-Path $SourceDirectory 'Test-DlnaRecorder.ps1'), (Join-Path $SourceDirectory 'Run-DlnaDiagnostics.cmd') -Destination $stage
$instructions = Join-Path $SourceDirectory 'README.md'
if (-not (Test-Path -LiteralPath $instructions -PathType Leaf)) { $instructions = Join-Path $root 'docs/DLNA-DIAGNOSTICS.md' }
Copy-Item -LiteralPath $instructions -Destination (Join-Path $stage 'README.md')
Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination $stage
$commit = & git -C $root rev-parse HEAD
if ($LASTEXITCODE -ne 0) { throw 'Package assembly requires Git source provenance.' }
$dirty = @(& git -C $root status --porcelain -- Directory.Build.props LICENSE scripts/New-DlnaDiagnostics.ps1 scripts/Test-DlnaRecorder.ps1 scripts/Run-DlnaDiagnostics.cmd docs/DLNA-DIAGNOSTICS.md)
if ($LASTEXITCODE -ne 0) { throw 'Could not inspect diagnostic source provenance.' }
# The commit's time, not the clock's, here and on the ZIP entries: packaging the same commit again gives the same kit.
$commitTime = [DateTimeOffset]::Parse((& git -C $root log -1 --format=%cI $commit), [Globalization.CultureInfo]::InvariantCulture).ToUniversalTime()
[ordered]@{ Version=$version; Commit=$commit; ModifiedSources=($dirty.Count -gt 0); CommitUtc=$commitTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'") } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $stage 'BUILDINFO.json') -Encoding utf8
$files = @(Get-ChildItem -LiteralPath $stage -File | Sort-Object Name)
$files | ForEach-Object {
    '{0}  {1}' -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name
} | Set-Content -LiteralPath (Join-Path $stage 'SHA256SUMS.txt') -Encoding utf8
$destination = Join-Path $OutputDirectory "DIGA-$version-dlna-diagnostics.zip"
# No overwrite: the caller chooses a fresh directory for each verified package.
& "$PSScriptRoot/Write-ZipArchive.ps1" -Directory $stage -Destination $destination -EntryTime $commitTime
Write-Host "Standalone diagnostic package: $([IO.Path]::GetFullPath($destination))"
