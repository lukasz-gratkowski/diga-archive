[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$downloads = Join-Path $root 'tools/downloads'
$bin = Join-Path $root 'tools/bin'
New-Item -ItemType Directory -Force $downloads, $bin, "$bin/licenses" | Out-Null
$dependencies = @(
    @{ Name='ffmpeg'; Version='9.0.2'; Url='https://www.gyan.dev/ffmpeg/builds/packages/ffmpeg-9.0.2-essentials_build.zip'; SHA256='60F467265B1E312373DBCD92200C2618A74850F98D3D078E94296BB3FA2047BA'; Source='https://github.com/FFmpeg/FFmpeg/commit/946fcce07b' },
    @{ Name='mediainfo'; Version='26.05'; Url='https://mediaarea.net/download/binary/libmediainfo0/26.05/MediaInfo_DLL_26.05_Windows_x64_WithoutInstaller.zip'; SHA256='F8C81699550A3A9425E9BDD1D6621587C463C51C568848AB8D3E36FE5EFC222C'; Source='https://github.com/MediaArea/MediaInfoLib/tree/v26.05' }
)
foreach ($dependency in $dependencies) {
    $archive = Join-Path $downloads ($dependency.Name + '.zip')
    if (-not (Test-Path -LiteralPath $archive)) {
        Write-Host "Downloading $($dependency.Name) $($dependency.Version)..."
        Invoke-WebRequest $dependency.Url -OutFile $archive -TimeoutSec 600
    }
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $dependency.SHA256) {
        throw "Checksum mismatch for $archive. Dependency pins require a reviewed update; do not execute this archive."
    }
    $expanded = Join-Path $downloads $dependency.Name
    Expand-Archive -LiteralPath $archive -DestinationPath $expanded -Force
}
$ffroot = Join-Path $downloads 'ffmpeg/ffmpeg-9.0.2-essentials_build'
Copy-Item -LiteralPath "$ffroot/bin/ffmpeg.exe", "$ffroot/bin/ffprobe.exe" -Destination $bin -Force
Copy-Item -LiteralPath "$downloads/mediainfo/MediaInfo.dll" -Destination $bin -Force
Copy-Item -LiteralPath "$ffroot/LICENSE" -Destination "$bin/licenses/FFmpeg-LICENSE.txt" -Force
Copy-Item -LiteralPath "$ffroot/README.txt" -Destination "$bin/licenses/FFmpeg-README.txt" -Force
Copy-Item -LiteralPath "$downloads/mediainfo/Developers/License.html" -Destination "$bin/licenses/MediaInfo-LICENSE.html" -Force
Copy-Item -LiteralPath "$downloads/mediainfo/ReadMe.txt" -Destination "$bin/licenses/MediaInfo-README.txt" -Force
$dependencies | ConvertTo-Json -Depth 5 | Set-Content "$bin/dependencies.json" -Encoding utf8
$ffmpegVersion = & "$bin/ffmpeg.exe" -version
if ($LASTEXITCODE -ne 0) { throw 'Downloaded FFmpeg cannot start.' }
Write-Host ($ffmpegVersion | Select-Object -First 1)
$ffprobeVersion = & "$bin/ffprobe.exe" -version
if ($LASTEXITCODE -ne 0) { throw 'Downloaded ffprobe cannot start.' }
Write-Host ($ffprobeVersion | Select-Object -First 1)
