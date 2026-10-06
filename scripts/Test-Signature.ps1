<#
.SYNOPSIS
Fails unless every file carries a valid, timestamped Authenticode signature from one and the same publisher.

.DESCRIPTION
Windows itself judges the signature (Get-AuthenticodeSignature); programs and PowerShell scripts are checked alike. A
signature that lacks a timestamp is rejected, because the signing certificates used for this project are valid for a few
days only.

-ExpectedPublisher is the name the certificate must be issued to (its "simple name", which Windows shows as the
publisher). The comparison is for equality: a name that merely contains the expected one is refused. When the parameter is
not given, the environment variable DIGA_SIGN_PUBLISHER is used; with neither, the files only have to agree with each other.

DIGA_SIGN_TEST_THUMBPRINT is for trying the signing pipeline with a self-made test certificate that Windows does not
trust: the named certificate is then accepted although its chain is not. The release workflow never sets it.
#>
[CmdletBinding()]
param([Parameter(Mandatory)][string[]]$Path, [string]$ExpectedPublisher = "$env:DIGA_SIGN_PUBLISHER")
$ErrorActionPreference = 'Stop'
$ExpectedPublisher = $ExpectedPublisher.Trim()
$testThumbprint = "$env:DIGA_SIGN_TEST_THUMBPRINT".Trim()
$publishers = @{}
foreach ($item in $Path) {
    $file = [IO.Path]::GetFullPath($item)
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "File not found: $file" }
    $signature = Get-AuthenticodeSignature -LiteralPath $file
    $name = Split-Path $file -Leaf
    if (-not $signature.SignerCertificate) { throw "$name is not signed." }
    if (-not $signature.TimeStamperCertificate) { throw "$name is signed without a timestamp." }
    $trusted = $signature.Status -eq 'Valid'
    if (-not $trusted -and $testThumbprint -and $signature.SignerCertificate.Thumbprint -ieq $testThumbprint -and $signature.Status -eq 'UnknownError') {
        Write-Warning "$name is signed with the test certificate $testThumbprint, which Windows does not trust."
        $trusted = $true
    }
    if (-not $trusted) { throw "The signature of $name is not valid: $($signature.Status). $($signature.StatusMessage)" }
    $subject = $signature.SignerCertificate.Subject
    $publisher = $signature.SignerCertificate.GetNameInfo('SimpleName', $false)
    if ($ExpectedPublisher -and $publisher -cne $ExpectedPublisher) { throw "$name is signed by '$publisher' ($subject), not by '$ExpectedPublisher'." }
    $publishers[$subject] = $publisher
    Write-Host "Signed: $name"
}
if ($publishers.Count -ne 1) { throw "The files are signed by different publishers: $($publishers.Keys -join ' | ')" }
Write-Host "Publisher: $($publishers.Values | Select-Object -First 1) ($($publishers.Keys | Select-Object -First 1))"
