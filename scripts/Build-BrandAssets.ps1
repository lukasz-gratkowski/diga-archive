[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$assets = Join-Path $root 'src/Diga.App/Assets'
$masterPath = Join-Path $assets 'BrandMaster.png'
if (-not (Test-Path -LiteralPath $masterPath)) { throw "Missing brand master: $masterPath" }
Add-Type -AssemblyName System.Drawing
# The mark that the window shows stands on the window's own background, which is another colour in each theme, so the
# flat background of the master is taken out of it. Pixels are blue, green, red and alpha bytes, row after row.
Add-Type -TypeDefinition @'
using System;

public static class BrandCutout
{
    const int Corner = 40;      // side of the four squares in which the background colour is measured
    const double Flat = 8;      // a pixel no farther than this from the background colour is background
    const double Solid = 90;    // a pixel at least this far from it is artwork in full
    const int Reach = 4;        // how far a pixel looks for background, and for the artwork it is the edge of

    public static byte[] Cut(byte[] source, int width, int height)
    {
        if (width < 2 * Corner || height < 2 * Corner) throw new ArgumentException("The image is too small to have its background measured.");
        var background = new double[3];
        var corners = new[] { new[] { 0, 0 }, new[] { width - Corner, 0 }, new[] { 0, height - Corner }, new[] { width - Corner, height - Corner } };
        foreach (var corner in corners)
            for (var y = corner[1]; y < corner[1] + Corner; y++)
                for (var x = corner[0]; x < corner[0] + Corner; x++)
                    for (var channel = 0; channel < 3; channel++) background[channel] += source[(y * width + x) * 4 + channel];
        for (var channel = 0; channel < 3; channel++) background[channel] = Math.Round(background[channel] / (4 * Corner * Corner));

        var distance = new double[width * height];
        for (var index = 0; index < distance.Length; index++)
        {
            double sum = 0;
            for (var channel = 0; channel < 3; channel++) { var difference = source[index * 4 + channel] - background[channel]; sum += difference * difference; }
            distance[index] = Math.Sqrt(sum);
        }
        // Without one flat colour in all four corners there is no background that could be told from the artwork.
        foreach (var corner in corners)
            for (var y = corner[1]; y < corner[1] + Corner; y++)
                for (var x = corner[0]; x < corner[0] + Corner; x++)
                    if (distance[y * width + x] > Flat) throw new InvalidOperationException("The corners of the brand master are not one flat colour, so its background cannot be removed.");

        var result = new byte[source.Length];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var index = y * width + x;
                var own = distance[index];
                if (own <= Flat) continue;
                // Is background within reach, and which pixel within reach is most surely artwork?
                var edge = false;
                var farthest = own;
                var artwork = index;
                for (var nearY = Math.Max(0, y - Reach); nearY <= Math.Min(height - 1, y + Reach); nearY++)
                    for (var nearX = Math.Max(0, x - Reach); nearX <= Math.Min(width - 1, x + Reach); nearX++)
                    {
                        var near = nearY * width + nearX;
                        if (distance[near] <= Flat) edge = true;
                        else if (distance[near] > farthest) { farthest = distance[near]; artwork = near; }
                    }
                double alpha;
                var colour = new double[3];
                if (edge && farthest >= Solid)
                {
                    // The smoothed edge of solid artwork: the artwork's colour, as much of it as the pixel holds.
                    double along = 0, length = 0;
                    for (var channel = 0; channel < 3; channel++)
                    {
                        var direction = source[artwork * 4 + channel] - background[channel];
                        along += (source[index * 4 + channel] - background[channel]) * direction;
                        length += direction * direction;
                        colour[channel] = source[artwork * 4 + channel];
                    }
                    alpha = Math.Max(0, Math.Min(1, along / length));
                }
                else if (own >= Solid)
                {
                    alpha = 1;
                    for (var channel = 0; channel < 3; channel++) colour[channel] = source[index * 4 + channel];
                }
                else
                {
                    // A shadow that fades into the background: it keeps its look on that background and fades into any other.
                    alpha = (own - Flat) / (Solid - Flat);
                    for (var channel = 0; channel < 3; channel++)
                        colour[channel] = Math.Max(0, Math.Min(255, background[channel] + (source[index * 4 + channel] - background[channel]) / alpha));
                }
                for (var channel = 0; channel < 3; channel++) result[index * 4 + channel] = (byte)Math.Round(colour[channel]);
                result[index * 4 + 3] = (byte)Math.Round(alpha * 255);
            }
        return result;
    }

    // Each new pixel is the average of the area it covers, weighted by alpha, so that the colour of a transparent pixel
    // cannot show at an edge.
    public static byte[] Resize(byte[] source, int width, int height, int size)
    {
        var result = new byte[size * size * 4];
        double stepX = (double)width / size, stepY = (double)height / size;
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                double left = x * stepX, right = (x + 1) * stepX, top = y * stepY, bottom = (y + 1) * stepY;
                double alpha = 0, area = 0;
                var colour = new double[3];
                for (var sourceY = (int)Math.Floor(top); sourceY < Math.Ceiling(bottom) && sourceY < height; sourceY++)
                {
                    var tall = Math.Min(bottom, sourceY + 1) - Math.Max(top, sourceY);
                    if (tall <= 0) continue;
                    for (var sourceX = (int)Math.Floor(left); sourceX < Math.Ceiling(right) && sourceX < width; sourceX++)
                    {
                        var wide = Math.Min(right, sourceX + 1) - Math.Max(left, sourceX);
                        if (wide <= 0) continue;
                        var index = (sourceY * width + sourceX) * 4;
                        var weight = wide * tall;
                        var covered = source[index + 3] / 255.0 * weight;
                        for (var channel = 0; channel < 3; channel++) colour[channel] += source[index + channel] * covered;
                        alpha += covered;
                        area += weight;
                    }
                }
                if (alpha <= 0) continue;
                var target = (y * size + x) * 4;
                for (var channel = 0; channel < 3; channel++) result[target + channel] = (byte)Math.Round(Math.Min(255, colour[channel] / alpha));
                result[target + 3] = (byte)Math.Round(255 * alpha / area);
            }
        return result;
    }
}
'@
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
    # The mark inside the window: the master without its background, at 512 pixels.
    $whole = [Drawing.Rectangle]::new(0, 0, $master.Width, $master.Height)
    $masterData = $master.LockBits($whole, [Drawing.Imaging.ImageLockMode]::ReadOnly, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try
    {
        $masterPixels = [byte[]]::new($masterData.Stride * $masterData.Height)
        [Runtime.InteropServices.Marshal]::Copy($masterData.Scan0, $masterPixels, 0, $masterPixels.Length)
    }
    finally { $master.UnlockBits($masterData) }
    $markSize = 512
    $markPixels = [BrandCutout]::Resize([BrandCutout]::Cut($masterPixels, $master.Width, $master.Height), $master.Width, $master.Height, $markSize)
    $markBitmap = [Drawing.Bitmap]::new($markSize, $markSize, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $markStream = [IO.MemoryStream]::new()
    try
    {
        $markData = $markBitmap.LockBits([Drawing.Rectangle]::new(0, 0, $markSize, $markSize), [Drawing.Imaging.ImageLockMode]::WriteOnly, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
        try { [Runtime.InteropServices.Marshal]::Copy($markPixels, 0, $markData.Scan0, $markPixels.Length) }
        finally { $markBitmap.UnlockBits($markData) }
        $markBitmap.Save($markStream, [Drawing.Imaging.ImageFormat]::Png)
        [IO.File]::WriteAllBytes((Join-Path $assets 'BrandMark.png'), $markStream.ToArray())
    }
    finally { $markStream.Dispose(); $markBitmap.Dispose() }
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
