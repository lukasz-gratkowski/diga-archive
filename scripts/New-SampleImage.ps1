# Creates only generated test-pattern media, never reads a physical disk.
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$directory = Join-Path $root 'artifacts/ui-smoke'
New-Item -ItemType Directory -Force $directory | Out-Null
$video = Join-Path $directory 'generated-test-pattern.mpg'
if (-not (Test-Path -LiteralPath $video)) {
    & "$root/tools/bin/ffmpeg.exe" -hide_banner -nostdin -v error -f lavfi -i 'testsrc2=size=640x360:rate=25' -f lavfi -i 'sine=frequency=880:sample_rate=48000' -t 8 -c:v mpeg2video -b:v 1500k -c:a mp2 -f mpeg $video
    if ($LASTEXITCODE -ne 0) { throw 'Test video generation failed.' }
}
$image = Join-Path $directory 'synthetic-panasonic.img'
if (-not (Test-Path -LiteralPath $image)) {
    dotnet run --project "$root/tools/Diga.FixtureGen" -- $video $image
    if ($LASTEXITCODE -ne 0) { throw 'Fixture generation failed.' }
}
Write-Host "Synthetic image for UI testing: $image"
