[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$downloads = Join-Path $root 'tools/downloads'
$bin = Join-Path $root 'tools/bin'
New-Item -ItemType Directory -Force $downloads, $bin, "$bin/licenses" | Out-Null
# FFmpeg is needed here by the tests only; the application does not ship it. The installer and the application download the
# same package, described by this file, which is the one place its version, addresses and checksums are recorded.
$ffmpeg = Get-Content -LiteralPath (Join-Path $root 'src/Diga.Core/Media/FfmpegPackage.json') -Raw | ConvertFrom-Json
# Ordered, because this description is written to a file that ships: a plain table would list its entries in a different order on every run.
$mediainfo = [ordered]@{ Name='mediainfo'; Version='26.05'; Url='https://mediaarea.net/download/binary/libmediainfo0/26.05/MediaInfo_DLL_26.05_Windows_x64_WithoutInstaller.zip'; SHA256='F8C81699550A3A9425E9BDD1D6621587C463C51C568848AB8D3E36FE5EFC222C'; Source='https://github.com/MediaArea/MediaInfoLib/tree/v26.05' }
$dependencies = @(
    @{ Name='ffmpeg'; Version=$ffmpeg.version; Urls=@($ffmpeg.urls); SHA256=$ffmpeg.sha256 },
    @{ Name='mediainfo'; Version=$mediainfo.Version; Urls=@($mediainfo.Url); SHA256=$mediainfo.SHA256 }
)
foreach ($dependency in $dependencies) {
    $archive = Join-Path $downloads ($dependency.Name + '.zip')
    if ((Test-Path -LiteralPath $archive) -and (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $dependency.SHA256) {
        # An archive kept from an earlier pin; it is replaced, never unpacked.
        [IO.File]::Delete($archive)
    }
    if (-not (Test-Path -LiteralPath $archive)) {
        $failure = $null
        foreach ($url in $dependency.Urls) {
            Write-Host "Downloading $($dependency.Name) $($dependency.Version) from $url..."
            try { Invoke-WebRequest $url -OutFile $archive -TimeoutSec 600; $failure = $null; break }
            catch { $failure = $_ }
        }
        if ($failure) { throw $failure }
    }
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $dependency.SHA256) {
        throw "Checksum mismatch for $archive. Dependency pins require a reviewed update; do not execute this archive."
    }
    $expanded = Join-Path $downloads $dependency.Name
    Expand-Archive -LiteralPath $archive -DestinationPath $expanded -Force
}
$ffroot = Join-Path $downloads ('ffmpeg/' + $ffmpeg.root)
Copy-Item -LiteralPath "$ffroot/bin/ffmpeg.exe", "$ffroot/bin/ffprobe.exe" -Destination $bin -Force
Copy-Item -LiteralPath "$downloads/mediainfo/MediaInfo.dll" -Destination $bin -Force
Copy-Item -LiteralPath "$ffroot/LICENSE" -Destination "$bin/licenses/FFmpeg-LICENSE.txt" -Force
Copy-Item -LiteralPath "$ffroot/README.txt" -Destination "$bin/licenses/FFmpeg-README.txt" -Force
Copy-Item -LiteralPath "$downloads/mediainfo/Developers/License.html" -Destination "$bin/licenses/MediaInfo-LICENSE.html" -Force
Copy-Item -LiteralPath "$downloads/mediainfo/ReadMe.txt" -Destination "$bin/licenses/MediaInfo-README.txt" -Force
foreach ($program in @(@{ File='ffmpeg.exe'; Hash=$ffmpeg.ffmpegSha256 }, @{ File='ffprobe.exe'; Hash=$ffmpeg.ffprobeSha256 })) {
    if ((Get-FileHash -LiteralPath (Join-Path $bin $program.File) -Algorithm SHA256).Hash -ne $program.Hash) { throw "$($program.File) does not match the checksum recorded in FfmpegPackage.json." }
}
# This file ships beside MediaInfo.dll, so it describes only what ships.
@($mediainfo) | ConvertTo-Json -Depth 5 -AsArray | Set-Content "$bin/dependencies.json" -Encoding utf8
$ffmpegVersion = & "$bin/ffmpeg.exe" -version
if ($LASTEXITCODE -ne 0) { throw 'Downloaded FFmpeg cannot start.' }
Write-Host ($ffmpegVersion | Select-Object -First 1)
$ffprobeVersion = & "$bin/ffprobe.exe" -version
if ($LASTEXITCODE -ne 0) { throw 'Downloaded ffprobe cannot start.' }
Write-Host ($ffprobeVersion | Select-Object -First 1)
