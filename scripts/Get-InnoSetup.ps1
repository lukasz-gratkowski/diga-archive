<#
.SYNOPSIS
Fetches the one Inno Setup compiler this project's installer is built with; returns its version and the path of ISCC.exe.

.DESCRIPTION
The compiler writes the setup program, the uninstaller and the standard message texts, and the project then signs the
result. It is therefore pinned like every other downloaded input: one exact version from Inno Setup's own GitHub release,
used only when the file has the recorded SHA-256 and carries its publisher's valid signature.

Inno Setup's installer is run in its portable mode (/PORTABLE=1). That copies the compiler into tools/innosetup inside this
checkout and registers nothing in Windows: no uninstall entry, no shortcuts, no .iss file association. Git ignores the folder.

A new version is a reviewed change of the four values below, with the SHA-256 taken from a download you checked yourself,
followed by the installer tests (scripts/Test-Package.ps1 -TestInstaller).

-PinOnly returns the pinned version without downloading or running anything.
#>
[CmdletBinding()]
param([switch]$PinOnly)
$ErrorActionPreference = 'Stop'
$pin = @{
    Version = '6.7.3'
    Url     = 'https://github.com/jrsoftware/issrc/releases/download/is-6_7_3/innosetup-6.7.3.exe'
    Sha256  = '9C73C3BAE7ED48D44112A0F48E66742C00090BDB5BEF71D9D3C056C66E97B732'
    # The name on the signature of the official installer and of the compiler it installs.
    Signer  = 'Pyrsys B.V.'
}
if ($PinOnly) { return [pscustomobject]@{ Version = $pin.Version; Compiler = $null } }
$root = Split-Path $PSScriptRoot -Parent
$folder = [IO.Path]::GetFullPath((Join-Path $root 'tools/innosetup'))
$compiler = Join-Path $folder 'ISCC.exe'
$marker = Join-Path $folder '.verified'
function Assert-Publisher([string]$File) {
    $signature = Get-AuthenticodeSignature -LiteralPath $File
    $signer = if ($signature.SignerCertificate) { $signature.SignerCertificate.GetNameInfo('SimpleName', $false) } else { '' }
    if ($signature.Status -ne 'Valid' -or $signer -cne $pin.Signer) { throw "$(Split-Path $File -Leaf) does not carry the valid signature of '$($pin.Signer)' ($($signature.Status), '$signer'). Do not run it." }
}
# The marker names the installer this folder was unpacked from, so a changed pin replaces the folder.
if ((Test-Path -LiteralPath $compiler -PathType Leaf) -and (Test-Path -LiteralPath $marker -PathType Leaf) -and (Get-Content -LiteralPath $marker -Raw).Trim() -ceq $pin.Sha256) {
    Assert-Publisher $compiler
    return [pscustomobject]@{ Version = $pin.Version; Compiler = $compiler }
}
$downloads = Join-Path $root 'tools/downloads'
New-Item -ItemType Directory -Force $downloads | Out-Null
$installer = Join-Path $downloads "innosetup-$($pin.Version).exe"
if ((Test-Path -LiteralPath $installer) -and (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash -ne $pin.Sha256) {
    # An interrupted download, or a file kept from an earlier pin; it is replaced, never run.
    [IO.File]::Delete($installer)
}
if (-not (Test-Path -LiteralPath $installer)) {
    Write-Host "Downloading Inno Setup $($pin.Version) from $($pin.Url)..."
    Invoke-WebRequest $pin.Url -OutFile $installer -TimeoutSec 300
}
if ((Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash -ne $pin.Sha256) {
    throw "Checksum mismatch for $installer. The Inno Setup pin requires a reviewed update of scripts/Get-InnoSetup.ps1; do not run this file."
}
Assert-Publisher $installer
if (Test-Path -LiteralPath $folder) {
    if ((Get-Item -LiteralPath $folder -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Refusing to replace a redirected tools/innosetup folder.' }
    [IO.Directory]::Delete($folder, $true)
}
$log = Join-Path $downloads "innosetup-$($pin.Version)-install.log"
# /CURRENTUSER keeps Windows from asking for administrator rights; /PORTABLE=1 is what keeps the registry and the Start menu untouched.
$arguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/CURRENTUSER', '/NOICONS', '/PORTABLE=1', ('/DIR="' + $folder + '"'), ('/LOG="' + $log + '"'))
$process = Start-Process -FilePath $installer -ArgumentList $arguments -WindowStyle Hidden -PassThru
if (-not $process.WaitForExit(300000)) { $process.Kill($true); throw 'The Inno Setup installer did not finish within five minutes.' }
if ($process.ExitCode -ne 0) { throw "The Inno Setup installer failed with exit code $($process.ExitCode). See $log" }
if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) { throw "The Inno Setup installer did not create $compiler. See $log" }
Assert-Publisher $compiler
Set-Content -LiteralPath $marker -Value $pin.Sha256 -Encoding ascii
Write-Host "Inno Setup $($pin.Version): $compiler"
[pscustomobject]@{ Version = $pin.Version; Compiler = $compiler }
