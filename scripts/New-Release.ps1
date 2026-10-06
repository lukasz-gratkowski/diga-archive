[CmdletBinding()]
param([string]$IsccPath = '', [switch]$SkipBuild)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    [xml]$properties = Get-Content Directory.Build.props
    $version = [string]$properties.Project.PropertyGroup.Version
    if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Version must be a three-part numeric version.' }
    $commit = (& git rev-parse HEAD)
    if ($LASTEXITCODE -ne 0) { throw 'Release assembly requires a committed Git checkout.' }
    $workingChanges = & git status --porcelain
    if ($LASTEXITCODE -ne 0 -or $workingChanges) { throw 'Commit or preserve all working-tree changes before assembling a release.' }
    $submoduleStatus = & git submodule status --recursive
    if ($LASTEXITCODE -ne 0 -or ($submoduleStatus | Where-Object { $_ -match '^[-+U]' })) { throw 'Initialize submodules at their exact committed pins before assembling a release.' }
    if (-not $SkipBuild) { & "$PSScriptRoot/Build.ps1" }
    if (-not $IsccPath) {
        $IsccPath = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") |
            Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    }
    if (-not $IsccPath) { throw 'Install Inno Setup 6 or supply -IsccPath.' }
    $artifactRoot = [IO.Path]::GetFullPath((Join-Path $root 'artifacts')) + [IO.Path]::DirectorySeparatorChar
    if ((Test-Path -LiteralPath $artifactRoot) -and ((Get-Item -LiteralPath $artifactRoot -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Release assembly refuses a redirected artifacts directory.' }
    $buildId = [Guid]::NewGuid().ToString('N')
    $publish = Join-Path $root "artifacts/publish-$version-$buildId"
    $release = Join-Path $root "artifacts/release-$version-$buildId"
    New-Item -ItemType Directory -Force $publish,$release | Out-Null
    dotnet publish src/Diga.App/Diga.App.csproj -c Release -r win-x64 -p:Platform=x64 --self-contained true -o $publish --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Self-contained publish failed.' }
    foreach ($needed in @('Diga.exe','App.xbf','Diga.pri','Assets/BrandMark.png','Assets/Diga.ico','tools/ffmpeg.exe','tools/ffprobe.exe','tools/MediaInfo.dll','tools/native/extract_meihdfs.exe','tools/native/udf_dump.exe','tools/native/dvd-vr-meihdfs.exe','tools/native/dvd-vr-udf.exe')) {
        if (-not (Test-Path -LiteralPath (Join-Path $publish $needed))) { throw "Missing release file: $needed" }
    }
    Copy-Item LICENSE,THIRD-PARTY-NOTICES.md,README.md -Destination $publish -Force
    Copy-Item docs -Destination $publish -Recurse -Force
    New-Item -ItemType Directory -Path (Join-Path $publish 'diagnostics') | Out-Null
    Copy-Item scripts/Test-DlnaRecorder.ps1,scripts/Run-DlnaDiagnostics.cmd -Destination (Join-Path $publish 'diagnostics')
    Copy-Item docs/DLNA-DIAGNOSTICS.md -Destination (Join-Path $publish 'diagnostics/README.md')
    $upstreamCommit = (& git -C third_party/panasonic-rec rev-parse HEAD)
    if ($LASTEXITCODE -ne 0) { throw 'Cannot resolve the pinned Panasonic source commit.' }
    @{ Version=$version; Commit=$commit; PanasonicCommit=$upstreamCommit; BuiltUtc=[DateTime]::UtcNow.ToString('o'); Architecture='x64' } |
        ConvertTo-Json | Set-Content "$publish/BUILDINFO.json" -Encoding utf8
    # The complete payload must not name the build account or checkout; nothing is packaged if it does.
    & "$PSScriptRoot/Test-BuildPathLeak.ps1" -PublishDirectory $publish -BuildRoot $root
    # Sign here with your own Authenticode certificate before the installer is assembled.
    & $IsccPath "/DAppVersion=$version" "/DPublishDir=$publish" "/O$release" installer/Diga.iss
    if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
    Compress-Archive -Path "$publish/*" -DestinationPath "$release/DIGA-$version-win-x64-portable.zip" -Force
    & "$PSScriptRoot/New-DlnaDiagnostics.ps1" -OutputDirectory $release
    # Include the exact application and submodule source, not GitHub's submodule-empty auto archive.
    $stage = Join-Path $root ('artifacts/source-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $stage | Out-Null
    $tracked = & git ls-files --recurse-submodules
    if ($LASTEXITCODE -ne 0) { throw 'Could not enumerate source files.' }
    foreach ($relative in $tracked) {
        $source = Join-Path $root $relative
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { continue }
        $destination = Join-Path $stage $relative
        New-Item -ItemType Directory -Force (Split-Path $destination -Parent) | Out-Null
        Copy-Item -LiteralPath $source -Destination $destination
    }
    @{ Version=$version; Commit=$commit; PanasonicCommit=$upstreamCommit } |
        ConvertTo-Json | Set-Content (Join-Path $stage 'SOURCEINFO.json') -Encoding utf8
    Compress-Archive -Path "$stage/*" -DestinationPath "$release/DIGA-$version-source.zip" -Force
    # Cleanup only the unique stage we created, under this workspace's artifacts directory.
    $resolvedStage = [IO.Path]::GetFullPath($stage)
    if (-not $resolvedStage.StartsWith($artifactRoot,[StringComparison]::OrdinalIgnoreCase)) { throw 'Source stage escaped artifacts.' }
    if ((Get-Item -LiteralPath $resolvedStage -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Source stage became a reparse point; refusing cleanup.' }
    Remove-Item -LiteralPath $resolvedStage -Recurse -Force
    $assets = Get-ChildItem $release -File | Where-Object Name -ne 'SHA256SUMS.txt'
    $assets | ForEach-Object { '{0}  {1}' -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name } |
        Set-Content "$release/SHA256SUMS.txt" -Encoding utf8
    foreach ($expectedAsset in @("DIGA-$version-win-x64-setup.exe", "DIGA-$version-win-x64-portable.zip", "DIGA-$version-source.zip", "DIGA-$version-dlna-diagnostics.zip", 'SHA256SUMS.txt')) {
        if (-not (Test-Path -LiteralPath (Join-Path $release $expectedAsset) -PathType Leaf)) { throw "Release asset missing: $expectedAsset" }
    }
    $workingChanges = & git status --porcelain
    if ($LASTEXITCODE -ne 0 -or $workingChanges) { throw 'Source files changed while the release was being assembled; no outputs were promoted.' }
    # Publish only a fully assembled fresh payload. Previous outputs are retained as
    # recoverable directories; stale binaries/assets can never enter this release.
    foreach ($pair in @(@{ Stage=$publish; Final=(Join-Path $root 'artifacts/publish') }, @{ Stage=$release; Final=(Join-Path $root 'artifacts/release') })) {
        $stagePath = [IO.Path]::GetFullPath($pair.Stage)
        $finalPath = [IO.Path]::GetFullPath($pair.Final)
        $previousPath = $finalPath + '-previous-' + $buildId
        foreach ($candidate in @($stagePath,$finalPath,$previousPath)) {
            if (-not $candidate.StartsWith($artifactRoot,[StringComparison]::OrdinalIgnoreCase)) { throw 'Release promotion escaped artifacts.' }
            if ((Test-Path -LiteralPath $candidate) -and ((Get-Item -LiteralPath $candidate -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Release promotion refuses reparse-point directories.' }
        }
        if (Test-Path -LiteralPath $finalPath) { Move-Item -LiteralPath $finalPath -Destination $previousPath }
        Move-Item -LiteralPath $stagePath -Destination $finalPath
    }
    Write-Host "Release artifacts: $(Join-Path $root 'artifacts/release')"
}
finally { Pop-Location }
