<#
.SYNOPSIS
Writes the files of a folder to a new ZIP whose bytes depend on those files only.

.DESCRIPTION
Compress-Archive stores each file's own modification time and takes the files in whatever order the file system lists
them, so two builds of one commit never gave the same archive. Here the entries are sorted by name and all carry the time
the caller supplies (the release scripts pass the commit's). An existing destination is an error; nothing is overwritten.
Folders without files are not stored.
#>
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Directory, [Parameter(Mandatory)][string]$Destination, [Parameter(Mandatory)][DateTimeOffset]$EntryTime)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$Directory = [IO.Path]::GetFullPath($Directory)
[string[]]$names = @(Get-ChildItem -LiteralPath $Directory -Recurse -File -Force | ForEach-Object { [IO.Path]::GetRelativePath($Directory, $_.FullName).Replace('\', '/') })
if (-not $names) { throw "Nothing to archive in $Directory" }
[Array]::Sort($names, [StringComparer]::Ordinal)
$archive = [IO.Compression.ZipFile]::Open([IO.Path]::GetFullPath($Destination), [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($name in $names) {
        $entry = $archive.CreateEntry($name, [IO.Compression.CompressionLevel]::Optimal)
        # A ZIP stores a clock reading without a time zone; this stores the reading in UTC on every PC.
        $entry.LastWriteTime = $EntryTime.ToUniversalTime()
        $target = $entry.Open()
        try {
            $source = [IO.File]::OpenRead((Join-Path $Directory $name))
            try { $source.CopyTo($target) } finally { $source.Dispose() }
        }
        finally { $target.Dispose() }
    }
}
finally { $archive.Dispose() }
