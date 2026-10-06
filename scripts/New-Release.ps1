<#
.SYNOPSIS
Assembles a release from one committed source tree: the application folder, the installer, the portable package, the
diagnostics kit, the source archive and their checksums.

.DESCRIPTION
The work has two stages, so that the release workflow can run them in separate jobs and keep the signing identity away
from everything that builds and tests:

  Publish   Builds and tests (unless -SkipBuild), publishes the self-contained application, checks what the folder holds
            and leaves it in artifacts/publish. Nothing is signed; a signing identity is neither needed nor looked at.
  Package   Takes artifacts/publish as the Publish stage left it. With a signing identity (scripts/Sign-Files.ps1 says how
            one is configured) it signs the project's own programs and the diagnostics script. It then compiles the
            installer, which signs itself and its uninstaller the same way, and writes the packages and SHA256SUMS.txt to
            artifacts/release. The finished application folder replaces artifacts/publish.

Without -Stage both run in turn. Either way the checkout must be a clean Git clone, before and after.

The installer is compiled with the one Inno Setup version that scripts/Get-InnoSetup.ps1 pins and fetches. -IsccPath names
another copy of the compiler; it must report exactly that version.
#>
[CmdletBinding()]
param([ValidateSet('All', 'Publish', 'Package')][string]$Stage = 'All', [string]$IsccPath = '', [switch]$SkipBuild)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
# A finished folder replaces the previous one only when everything before succeeded. The previous one is kept as a
# recoverable directory; stale binaries or assets can never enter this release.
function Move-ToFinal([string]$Staged, [string]$Final) {
    $stagePath = [IO.Path]::GetFullPath($Staged)
    $finalPath = [IO.Path]::GetFullPath($Final)
    $previousPath = $finalPath + '-previous-' + $buildId
    foreach ($candidate in @($stagePath, $finalPath, $previousPath)) {
        if (-not $candidate.StartsWith($artifactRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Release promotion escaped artifacts.' }
        if ((Test-Path -LiteralPath $candidate) -and ((Get-Item -LiteralPath $candidate -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Release promotion refuses reparse-point directories.' }
    }
    if (Test-Path -LiteralPath $finalPath) { Move-Item -LiteralPath $finalPath -Destination $previousPath }
    Move-Item -LiteralPath $stagePath -Destination $finalPath
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
Push-Location $root
try {
    [xml]$properties = Get-Content Directory.Build.props
    $version = [string]$properties.Project.PropertyGroup.Version
    if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Version must be a three-part numeric version.' }
    $commit = (& git rev-parse HEAD)
    if ($LASTEXITCODE -ne 0) { throw 'Release assembly requires a committed Git checkout.' }
    $workingChanges = & git status --porcelain
    if ($LASTEXITCODE -ne 0 -or $workingChanges) { throw 'Commit or preserve all working-tree changes before assembling a release.' }
    # Whatever carries a date carries the commit's, never the clock's, so that building one commit twice can give the same files.
    $commitTime = [DateTimeOffset]::Parse((& git log -1 --format=%cI $commit), [Globalization.CultureInfo]::InvariantCulture).ToUniversalTime()
    $artifactRoot = [IO.Path]::GetFullPath((Join-Path $root 'artifacts')) + [IO.Path]::DirectorySeparatorChar
    if ((Test-Path -LiteralPath $artifactRoot) -and ((Get-Item -LiteralPath $artifactRoot -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Release assembly refuses a redirected artifacts directory.' }
    $buildId = [Guid]::NewGuid().ToString('N')
    $publish = Join-Path $root "artifacts/publish-$version-$buildId"
    $finalPublish = Join-Path $root 'artifacts/publish'
    $buildInfoFile = Join-Path $publish 'BUILDINFO.json'
    if ($Stage -ne 'Package') {
        if (-not $SkipBuild) { & "$PSScriptRoot/Build.ps1" }
        New-Item -ItemType Directory -Force $publish | Out-Null
        # Build.ps1 restored the packages in locked mode. Restoring again here could only loosen that, and would rewrite a lock file.
        dotnet publish src/Diga.App/Diga.App.csproj -c Release -r win-x64 -p:Platform=x64 --self-contained true --no-restore -o $publish --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Self-contained publish failed. With -SkipBuild, scripts/Build.ps1 must have run in this checkout first.' }
        foreach ($needed in @('Diga.exe','App.xbf','Diga.pri','Assets/BrandMark.png','Assets/Diga.ico','tools/MediaInfo.dll','tools/licenses/MediaInfo-LICENSE.html',
                'licenses/dotnet-LICENSE.txt','licenses/dotnet-THIRD-PARTY-NOTICES.txt','licenses/WindowsAppSDK-license.txt','licenses/WindowsAppSDK-NOTICE.txt')) {
            if (-not (Test-Path -LiteralPath (Join-Path $publish $needed))) { throw "Missing release file: $needed" }
        }
        # The tools folder is copied from tools/bin, which is not tracked and which a build PC can hold more in (the tests' FFmpeg,
        # programs of earlier versions). Exactly these files ship; FFmpeg is downloaded by the installer or the application instead.
        $toolsFolder = Join-Path $publish 'tools'
        $shippedTools = @(Get-ChildItem -LiteralPath $toolsFolder -Recurse -File | ForEach-Object { [IO.Path]::GetRelativePath($toolsFolder, $_.FullName).Replace('\', '/') } | Sort-Object)
        $expectedTools = @('MediaInfo.dll', 'dependencies.json', 'licenses/MediaInfo-LICENSE.html', 'licenses/MediaInfo-README.txt') | Sort-Object
        if (Compare-Object $shippedTools $expectedTools) { throw "The payload's tools folder must hold exactly: $($expectedTools -join ', '). It holds: $($shippedTools -join ', ')" }
        $mediaInfoSignature = Get-AuthenticodeSignature -LiteralPath (Join-Path $toolsFolder 'MediaInfo.dll')
        if ($mediaInfoSignature.Status -ne 'Valid' -or $mediaInfoSignature.SignerCertificate.Subject -notmatch 'MediaArea') { throw "MediaInfo.dll does not carry MediaArea's valid signature ($($mediaInfoSignature.Status))." }
        # Documents written for the user ship, each one named here and each one tracked by Git. The whole docs folder used to be
        # copied, which also shipped the maintainer's notes and screenshots, and any untracked file that happened to lie there.
        # The first three must exist. A guide ships in every language it has ("*" stands for ".pl" and the like, within the
        # docs folder only), and one that is not in this commit yet is left out.
        $requiredDocuments = @('LICENSE', 'THIRD-PARTY-NOTICES.md', 'README.md')
        $userDocuments = @('docs/USER-GUIDE*.md', 'docs/CLOUD-SETUP*.md', 'docs/RECORDER-SETUP*.md', 'docs/PRIVACY.md')
        $documents = @(& git ls-files -- @($requiredDocuments + $userDocuments | ForEach-Object { ":(glob)$_" }))
        if ($LASTEXITCODE -ne 0) { throw 'Could not ask Git which documents are tracked.' }
        foreach ($document in $requiredDocuments) { if ($documents -cnotcontains $document) { throw "Missing release document: $document" } }
        foreach ($pattern in $userDocuments) { if (-not ($documents -clike $pattern)) { Write-Host "Not in this commit, so not shipped: $pattern" } }
        foreach ($document in $documents) {
            $destination = Join-Path $publish $document
            New-Item -ItemType Directory -Force (Split-Path $destination -Parent) | Out-Null
            Copy-Item -LiteralPath $document -Destination $destination
        }
        # The same exact-list check as for the tools folder: nothing else may have found its way into the payload's docs folder.
        $docsFolder = Join-Path $publish 'docs'
        $shippedDocs = @(if (Test-Path -LiteralPath $docsFolder) { Get-ChildItem -LiteralPath $docsFolder -Recurse -File -Force | ForEach-Object { 'docs/' + [IO.Path]::GetRelativePath($docsFolder, $_.FullName).Replace('\', '/') } | Sort-Object })
        $expectedDocs = @($documents | Where-Object { $_ -like 'docs/*' } | Sort-Object)
        if (($shippedDocs -join ', ') -cne ($expectedDocs -join ', ')) { throw "The payload's docs folder must hold exactly: $($expectedDocs -join ', '). It holds: $($shippedDocs -join ', ')" }
        foreach ($document in $requiredDocuments) { if (-not (Test-Path -LiteralPath (Join-Path $publish $document) -PathType Leaf)) { throw "Missing release file: $document" } }
        New-Item -ItemType Directory -Path (Join-Path $publish 'diagnostics') | Out-Null
        Copy-Item scripts/Test-DlnaRecorder.ps1,scripts/Run-DlnaDiagnostics.cmd -Destination (Join-Path $publish 'diagnostics')
        Copy-Item docs/DLNA-DIAGNOSTICS.md -Destination (Join-Path $publish 'diagnostics/README.md')
        # What the application was built with. The .NET SDK may be a later patch than global.json names, and the runtime that
        # ships inside the application comes with the SDK; neither is written down anywhere else.
        $sdkVersion = (& dotnet --version)
        if ($LASTEXITCODE -ne 0 -or "$sdkVersion" -notmatch '^\d+\.\d+\.\d+') { throw 'Could not read the .NET SDK version.' }
        $runtimeConfig = Get-Content -LiteralPath (Join-Path $publish 'Diga.runtimeconfig.json') -Raw | ConvertFrom-Json
        $runtimeVersion = @($runtimeConfig.runtimeOptions.includedFrameworks | Where-Object name -eq 'Microsoft.NETCore.App' | ForEach-Object version)
        if ($runtimeVersion.Count -ne 1) { throw 'Diga.runtimeconfig.json does not name the .NET runtime that was published with the application.' }
        $packages = Get-Content -LiteralPath 'src/Diga.App/packages.lock.json' -Raw | ConvertFrom-Json
        $windowsAppSdk = @($packages.dependencies.PSObject.Properties.Value | ForEach-Object { $_.'Microsoft.WindowsAppSDK'.resolved } | Where-Object { $_ } | Select-Object -Unique)
        if ($windowsAppSdk.Count -ne 1) { throw 'src/Diga.App/packages.lock.json does not name one Windows App SDK version.' }
        # GitHub's runners name their image in these two variables; elsewhere the entry stays empty.
        $image = "$env:ImageOS $env:ImageVersion".Trim()
        [ordered]@{ Version=$version; Commit=$commit; CommitUtc=$commitTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"); Architecture='x64'
            DotnetSdk="$sdkVersion"; DotnetRuntime=$runtimeVersion[0]; WindowsAppSdk=$windowsAppSdk[0]; BuildImage=$image } |
            ConvertTo-Json | Set-Content $buildInfoFile -Encoding utf8
        # The complete payload must not name the build account or checkout; nothing is packaged if it does.
        & "$PSScriptRoot/Test-BuildPathLeak.ps1" -PublishDirectory $publish -BuildRoot $root
        if ($Stage -eq 'Publish') {
            $workingChanges = & git status --porcelain
            if ($LASTEXITCODE -ne 0 -or $workingChanges) { throw 'Source files changed while the application was being published; nothing was promoted.' }
            Move-ToFinal $publish $finalPublish
            Write-Host "Application folder, not yet packaged: $finalPublish"
            return
        }
    }
    else {
        # The folder may come from another job of the release workflow. It is taken only if it says it was published from this very commit.
        $source = Join-Path $finalPublish 'BUILDINFO.json'
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw 'artifacts/publish holds no published application. Run scripts/New-Release.ps1 -Stage Publish first.' }
        $published = [Text.Json.Nodes.JsonNode]::Parse([IO.File]::ReadAllText($source))
        if ("$($published['Version'])" -cne $version -or "$($published['Commit'])" -cne $commit) { throw "artifacts/publish was published from version $($published['Version']), commit $($published['Commit']); this checkout is version $version, commit $commit." }
        if ($null -ne $published['Signed']) { throw 'artifacts/publish has been packaged already. Run scripts/New-Release.ps1 -Stage Publish again.' }
        Copy-Item -LiteralPath $finalPublish -Destination $publish -Recurse
    }
    $inno = if ($IsccPath) { [pscustomobject]@{ Version = (& "$PSScriptRoot/Get-InnoSetup.ps1" -PinOnly).Version; Compiler = $IsccPath } }
        else { & "$PSScriptRoot/Get-InnoSetup.ps1" | Select-Object -Last 1 }
    $release = Join-Path $root "artifacts/release-$version-$buildId"
    New-Item -ItemType Directory -Force $release | Out-Null
    $ffmpeg =Get-Content -LiteralPath 'src/Diga.Core/Media/FfmpegPackage.json' -Raw | ConvertFrom-Json
    if (@($ffmpeg.urls).Count -ne 2 -or @($ffmpeg.urls | Where-Object { $_ -notmatch '^https://[A-Za-z0-9./_-]+$' })) { throw 'FfmpegPackage.json must list two plain https addresses.' }
    foreach ($value in @($ffmpeg.sha256, $ffmpeg.ffmpegSha256, $ffmpeg.ffprobeSha256)) { if ($value -notmatch '^[0-9A-Fa-f]{64}$') { throw 'FfmpegPackage.json holds a malformed SHA-256.' } }
    if ($ffmpeg.version -notmatch '^[0-9.]+$' -or $ffmpeg.root -notmatch '^[A-Za-z0-9._-]+$') { throw 'FfmpegPackage.json holds an unexpected version or root folder.' }
    $ffmpegDefines = @(
        "/DFfmpegVersion=$($ffmpeg.version)", "/DFfmpegUrl=$($ffmpeg.urls[0])", "/DFfmpegMirrorUrl=$($ffmpeg.urls[1])", "/DFfmpegSha256=$($ffmpeg.sha256)",
        "/DFfmpegRoot=$($ffmpeg.root)", "/DFfmpegExeSha256=$($ffmpeg.ffmpegSha256)", "/DFfprobeExeSha256=$($ffmpeg.ffprobeSha256)",
        ('/DFfmpegSizeText={0:0} MB' -f ($ffmpeg.sizeBytes / 1MB)))
    # The signing identity is named in the environment (scripts/Sign-Files.ps1 says how); without one the release is unsigned.
    $signed = [bool]("$env:DIGA_SIGN_ENDPOINT$env:DIGA_SIGN_ACCOUNT$env:DIGA_SIGN_PROFILE$env:DIGA_SIGN_THUMBPRINT".Trim())
    $publisher = "$env:DIGA_SIGN_PUBLISHER".Trim()
    # Without the expected name a signature from any certificate the identity can reach would pass as the project's own.
    if ($signed -and -not $publisher) { throw 'A signing identity is configured, but DIGA_SIGN_PUBLISHER is empty. Set it to the name the certificate is issued to.' }
    $buildInfo = [Text.Json.Nodes.JsonNode]::Parse([IO.File]::ReadAllText($buildInfoFile))
    $buildInfo['InnoSetup'] = $inno.Version
    $buildInfo['Signed'] = $signed
    [IO.File]::WriteAllText($buildInfoFile, $buildInfo.ToJsonString([Text.Json.JsonSerializerOptions]@{ WriteIndented = $true }) + "`n", [Text.UTF8Encoding]::new($false))
    # The project's own files. The .NET, Windows App SDK and MediaInfo files carry their publishers' signatures already.
    $diagnostics = Join-Path $publish 'diagnostics'
    $ownFiles = @((Join-Path $publish 'Diga.exe'), (Join-Path $publish 'Diga.dll'), (Join-Path $publish 'Diga.Core.dll'), (Join-Path $diagnostics 'Test-DlnaRecorder.ps1'))
    $signingArguments = @()
    if ($signed) {
        # A signed diagnostics script must not come with a launcher that switches PowerShell's signature check off. The signed
        # release's launcher asks for the check instead; an unsigned release keeps the launcher exactly as it is in scripts/.
        $launcher = Join-Path $diagnostics 'Run-DlnaDiagnostics.cmd'
        $launcherText = [IO.File]::ReadAllText($launcher)
        $unsignedPolicy = 'set "DiagnosticPolicy=Bypass"'
        if ([regex]::Matches($launcherText, [regex]::Escape($unsignedPolicy)).Count -ne 1) { throw 'Run-DlnaDiagnostics.cmd must set DiagnosticPolicy exactly once.' }
        [IO.File]::WriteAllText($launcher, $launcherText.Replace($unsignedPolicy, 'set "DiagnosticPolicy=AllSigned"'), [Text.UTF8Encoding]::new($false))
        # Nor with instructions that do: where the kit's README shows how to start the script by hand, it names the same policy.
        $instructions = Join-Path $diagnostics 'README.md'
        [IO.File]::WriteAllText($instructions, ([IO.File]::ReadAllText($instructions) -replace '(?i)(-ExecutionPolicy\s+)Bypass\b', '${1}AllSigned'), [Text.UTF8Encoding]::new($false))
        & "$PSScriptRoot/Sign-Files.ps1" -Require -Path $ownFiles
        # The compiler runs this for the uninstaller and for the finished installer. In its notation $q is a quote and $f the quoted file.
        $signingArguments = @('/DSignToolName=digasign',
            ('/Sdigasign=pwsh.exe -NoProfile -NonInteractive -File $q' + (Join-Path $PSScriptRoot 'Sign-Files.ps1') + '$q -Require -Path $f'))
    }
    else { Write-Warning 'No signing identity is configured: the application and the installer are not signed.' }
    # The installer records each file's time, and so would the portable package; signing has just rewritten some of them.
    foreach ($file in Get-ChildItem -LiteralPath $publish -Recurse -File -Force) { $file.LastWriteTimeUtc = $commitTime.UtcDateTime }
    & $inno.Compiler "/DAppVersion=$version" "/DPublishDir=$publish" @ffmpegDefines @signingArguments "/O$release" installer/Diga.iss | Tee-Object -Variable compilerOutput
    if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
    # installer/Diga.iss is written for and tested with one Inno Setup version; any other needs a reviewed change of the pin.
    $compilerVersion = @($compilerOutput | Where-Object { $_ -match '^Compiler engine version: ' }) | Select-Object -First 1
    if ("$compilerVersion" -cne "Compiler engine version: Inno Setup $($inno.Version)") { throw "The installer must be compiled with Inno Setup $($inno.Version). Found: $compilerVersion" }
    Write-Host $compilerVersion
    if ($signed) { & "$PSScriptRoot/Test-Signature.ps1" -Path (@(Join-Path $release "DIGA-$version-win-x64-setup.exe") + $ownFiles) -ExpectedPublisher $publisher }
    & "$PSScriptRoot/Write-ZipArchive.ps1" -Directory $publish -Destination "$release/DIGA-$version-win-x64-portable.zip" -EntryTime $commitTime
    # The kit holds the same three files as the payload's diagnostics folder: in a signed release, the signed script with the
    # launcher and the instructions that go with it.
    & "$PSScriptRoot/New-DlnaDiagnostics.ps1" -OutputDirectory $release -SourceDirectory $diagnostics
    # The exact source of this build, written by Git from the commit itself, so that anyone can compare it with the tag.
    # Line endings follow .gitattributes alone, not this PC's Git settings, and the entries carry the commit's time read in
    # UTC, not in this PC's time zone. Git stores the commit ID as the ZIP's comment.
    $sourceArchive = Join-Path $release "DIGA-$version-source.zip"
    $timeZone = $env:TZ
    $env:TZ = 'UTC'
    try { & git -c core.autocrlf=false -c core.eol=lf archive --format=zip "--prefix=DIGA-$version/" -o $sourceArchive $commit }
    finally { $env:TZ = $timeZone }
    if ($LASTEXITCODE -ne 0) { throw 'Could not write the source archive.' }
    $sourceZip = [IO.Compression.ZipFile]::OpenRead($sourceArchive)
    try { if ($sourceZip.Comment -cne $commit) { throw "The source archive does not name commit $commit." } } finally { $sourceZip.Dispose() }
    $assets = Get-ChildItem $release -File | Where-Object Name -ne 'SHA256SUMS.txt' | Sort-Object Name
    $assets | ForEach-Object { '{0}  {1}' -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name } |
        Set-Content "$release/SHA256SUMS.txt" -Encoding utf8
    foreach ($expectedAsset in @("DIGA-$version-win-x64-setup.exe", "DIGA-$version-win-x64-portable.zip", "DIGA-$version-source.zip", "DIGA-$version-dlna-diagnostics.zip", 'SHA256SUMS.txt')) {
        if (-not (Test-Path -LiteralPath (Join-Path $release $expectedAsset) -PathType Leaf)) { throw "Release asset missing: $expectedAsset" }
    }
    $workingChanges = & git status --porcelain
    if ($LASTEXITCODE -ne 0 -or $workingChanges) { throw 'Source files changed while the release was being assembled; no outputs were promoted.' }
    Move-ToFinal $publish $finalPublish
    Move-ToFinal $release (Join-Path $root 'artifacts/release')
    Write-Host "Release artifacts: $(Join-Path $root 'artifacts/release')"
}
finally { Pop-Location }
