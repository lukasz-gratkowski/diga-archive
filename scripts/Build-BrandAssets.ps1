[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$assets = Join-Path $root 'src/Diga.App/Assets'
$masterPath = Join-Path $assets 'BrandMaster.png'
if (-not (Test-Path -LiteralPath $masterPath)) { throw "Missing brand master: $masterPath" }
Add-Type -AssemblyName System.Drawing
$master = [Drawing.Image]::FromFile($masterPath)
try {
    if ($master.Width -ne $master.Height) { throw 'The brand master must be square.' }
    function Convert-BrandPng([int]$Size) {
        # Format/size conversion only: the generated artwork is kept intact.
        $bitmap = [Drawing.Bitmap]::new($Size, $Size, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        $stream = [IO.MemoryStream]::new()
        try {
            $graphics.CompositingMode = [Drawing.Drawing2D.CompositingMode]::SourceCopy
            $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $graphics.DrawImage($master, [Drawing.Rectangle]::new(0, 0, $Size, $Size))
            $bitmap.Save($stream, [Drawing.Imaging.ImageFormat]::Png)
            return ,$stream.ToArray()
        }
        finally { $stream.Dispose(); $graphics.Dispose(); $bitmap.Dispose() }
    }
    [IO.File]::WriteAllBytes((Join-Path $assets 'BrandMark.png'), (Convert-BrandPng 512))
    [IO.File]::WriteAllBytes((Join-Path $assets 'Diga.png'), (Convert-BrandPng 256))
    # Logo for the Microsoft Entra app registration (Branding & properties). Microsoft requires exactly 215x215 px, PNG,
    # at most 100 KB, and asks for a solid background without transparency:
    # https://learn.microsoft.com/entra/identity/enterprise-apps/application-properties#logo
    # It is written without an alpha channel, so no pixel can be transparent.
    $entraBitmap = [Drawing.Bitmap]::new(215, 215, [Drawing.Imaging.PixelFormat]::Format24bppRgb)
    $entraGraphics = [Drawing.Graphics]::FromImage($entraBitmap)
    $entraStream = [IO.MemoryStream]::new()
    try {
        $entraGraphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $entraGraphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $entraGraphics.DrawImage($master, [Drawing.Rectangle]::new(0, 0, 215, 215))
        $entraBitmap.Save($entraStream, [Drawing.Imaging.ImageFormat]::Png)
        if ($entraStream.Length -gt 100KB) { throw "The Entra logo is $($entraStream.Length) bytes; Microsoft accepts at most 100 KB." }
        $branding = Join-Path $root 'docs/branding'
        New-Item -ItemType Directory -Force $branding | Out-Null
        [IO.File]::WriteAllBytes((Join-Path $branding 'entra-app-logo-215.png'), $entraStream.ToArray())
    }
    finally { $entraStream.Dispose(); $entraGraphics.Dispose(); $entraBitmap.Dispose() }
    $sizes = @(16,24,32,48,64,128,256)
    $images = @($sizes | ForEach-Object { ,(Convert-BrandPng $_) })
    $iconStream = [IO.MemoryStream]::new()
    $writer = [IO.BinaryWriter]::new($iconStream)
    try {
        $writer.Write([uint16]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]$sizes.Count)
        $offset = 6 + 16 * $sizes.Count
        for ($index = 0; $index -lt $sizes.Count; $index++) {
            $dimension = if ($sizes[$index] -eq 256) { 0 } else { $sizes[$index] }
            $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
            $writer.Write([byte]0); $writer.Write([byte]0)
            $writer.Write([uint16]1); $writer.Write([uint16]32)
            $writer.Write([uint32]$images[$index].Length)
            $writer.Write([uint32]$offset)
            $offset += $images[$index].Length
        }
        foreach ($png in $images) { $writer.Write([byte[]]$png) }
        $writer.Flush()
        [IO.File]::WriteAllBytes((Join-Path $assets 'Diga.ico'), $iconStream.ToArray())
    }
    finally { $writer.Dispose(); $iconStream.Dispose() }
    Write-Host "Exported BrandMark.png, Diga.png, and Diga.ico ($($sizes -join ', ') px)."
}
finally { $master.Dispose() }
