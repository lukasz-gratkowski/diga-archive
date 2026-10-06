[CmdletBinding()]
param([string]$PublishDirectory = '', [string]$BuildRoot = '')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (-not $PublishDirectory) { $PublishDirectory = Join-Path $root 'artifacts/publish' }
$PublishDirectory = [IO.Path]::GetFullPath($PublishDirectory)
if (-not (Test-Path -LiteralPath $PublishDirectory -PathType Container)) { throw "Publish directory was not found: $PublishDirectory" }
$latin1 = [Text.Encoding]::Latin1
# A path is embedded as plain or UTF-16 text, with either separator, or JSON-escaped
# (the Source Link map inside a PDB). Bytes are compared as Latin-1 text, ignoring case.
function New-Needle([string[]]$Path) {
    foreach ($form in $Path | ForEach-Object { $_; $_.Replace('\','/'); $_.Replace('\','\\') } | Select-Object -Unique) {
        @{ Label=$form; Text=$form }
        @{ Label="$form (UTF-16)"; Text=$latin1.GetString([Text.Encoding]::Unicode.GetBytes($form)) }
    }
}
$profileNeedles = @(New-Needle (@('C:\Users\', $env:USERPROFILE) | Where-Object { $_ }))
# Markdown may name a checkout location in prose (docs/TEST-REPORT.md does); only a
# user-profile path is refused there. Everything else must not name the build root either.
$rootNeedles = @(if ($BuildRoot) { New-Needle ([IO.Path]::GetFullPath($BuildRoot).TrimEnd('\','/')) })
$overlap = ($profileNeedles + $rootNeedles | ForEach-Object { $_.Text.Length } | Measure-Object -Maximum).Maximum
# A portable PDB stores each document name as separate path segments, which a byte scan cannot see.
function Get-PdbDocumentName([string]$Path) {
    $stream = [IO.File]::OpenRead($Path)
    try {
        $provider = [Reflection.Metadata.MetadataReaderProvider]::FromPortablePdbStream($stream)
        try {
            $reader = $provider.GetMetadataReader()
            foreach ($handle in $reader.Documents) { $reader.GetString($reader.GetDocument($handle).Name) }
        }
        finally { $provider.Dispose() }
    }
    catch [BadImageFormatException] { }
    finally { $stream.Dispose() }
}
$buffer = [byte[]]::new(16MB)
$files = @(Get-ChildItem -LiteralPath $PublishDirectory -Recurse -File)
$leaks = foreach ($file in $files) {
    $needles = if ($file.Extension -eq '.md') { $profileNeedles } else { $profileNeedles + $rootNeedles }
    $found = [Collections.Generic.SortedSet[string]]::new()
    $stream = [IO.File]::OpenRead($file.FullName)
    try {
        $carry = ''
        while (($read = $stream.Read($buffer, 0, $buffer.Length)) -gt 0) {
            $text = $carry + $latin1.GetString($buffer, 0, $read)
            foreach ($needle in $needles) {
                if ($text.IndexOf($needle.Text, [StringComparison]::OrdinalIgnoreCase) -ge 0) { [void]$found.Add($needle.Label) }
            }
            $carry = $text.Substring([Math]::Max(0, $text.Length - $overlap))
        }
    }
    finally { $stream.Dispose() }
    if ($file.Extension -eq '.pdb') {
        $documents = (Get-PdbDocumentName $file.FullName) -join "`n"
        foreach ($needle in $needles) {
            if ($documents.IndexOf($needle.Text, [StringComparison]::OrdinalIgnoreCase) -ge 0) { [void]$found.Add("$($needle.Label) (PDB document)") }
        }
    }
    foreach ($label in $found) { '{0}: {1}' -f [IO.Path]::GetRelativePath($PublishDirectory, $file.FullName), $label }
}
if ($leaks) {
    $leaks | Write-Host
    throw "Local build paths are embedded in $(@($leaks).Count) place(s) under $PublishDirectory. Build Release with ContinuousIntegrationBuild (Directory.Build.props) from a Git checkout."
}
Write-Host "No local build paths in $($files.Count) files: $PublishDirectory"
