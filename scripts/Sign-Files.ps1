<#
.SYNOPSIS
Signs files (Authenticode, SHA-256, RFC 3161 timestamp) with the identity named in the environment, and verifies the result.

.DESCRIPTION
Nothing here holds a key or a secret. The identity comes from the environment:

  Azure Artifact Signing (the release workflow after azure/login, or a developer after `az login`):
    DIGA_SIGN_ENDPOINT    the account's regional endpoint, for example https://plc.codesigning.azure.net
    DIGA_SIGN_ACCOUNT     the Artifact Signing account name
    DIGA_SIGN_PROFILE     the certificate profile name

  A certificate in the Windows certificate store (a hardware token, or a test certificate):
    DIGA_SIGN_THUMBPRINT  SHA-1 thumbprint of the certificate in Cert:\CurrentUser\My
    DIGA_SIGN_TIMESTAMP   its RFC 3161 timestamp server

  With either, and required by scripts/New-Release.ps1:
    DIGA_SIGN_PUBLISHER   the name the certificate is issued to; scripts/Test-Signature.ps1 accepts no other signer

Without an identity the files are left as they are; -Require turns that into an error. The Inno Setup compiler calls this
script for the uninstaller and for the finished installer, so it must not depend on the current directory.

Programs and PowerShell scripts are signed alike; signtool picks the format from the file.

-ToolsOnly downloads and checks the signing tools and signs nothing. The release workflow runs it before it signs in to
Azure, so that nothing is downloaded while the signing identity is live and the sign-in is not spent on waiting.
#>
[CmdletBinding()]
param([string[]]$Path = @(), [switch]$Require, [switch]$ToolsOnly)
$ErrorActionPreference = 'Stop'
if (-not $ToolsOnly -and -not $Path) { throw 'Name the files to sign with -Path.' }
$root = Split-Path $PSScriptRoot -Parent
$endpoint = "$env:DIGA_SIGN_ENDPOINT".Trim()
$account = "$env:DIGA_SIGN_ACCOUNT".Trim()
$certificateProfile = "$env:DIGA_SIGN_PROFILE".Trim()
$thumbprint = "$env:DIGA_SIGN_THUMBPRINT".Trim()
$useArtifactSigning = [bool]($endpoint -or $account -or $certificateProfile)
if (-not $useArtifactSigning -and -not $thumbprint) {
    if ($Require) { throw 'No signing identity is configured (DIGA_SIGN_ENDPOINT, DIGA_SIGN_ACCOUNT and DIGA_SIGN_PROFILE, or DIGA_SIGN_THUMBPRINT).' }
    Write-Host 'No signing identity is configured; the files are left unsigned.'
    return
}
if ($useArtifactSigning -and $thumbprint) { throw 'Configure Azure Artifact Signing or a certificate thumbprint, not both.' }
$files = @(foreach ($item in $Path) {
    $full = [IO.Path]::GetFullPath($item)
    if (-not (Test-Path -LiteralPath $full -PathType Leaf)) { throw "File to sign not found: $full" }
    $full
})

# The same pair of packages Microsoft's own signing action uses; each is accepted only with its recorded SHA-512.
$packages = @{
    SignTool = @{ Id = 'microsoft.windows.sdk.buildtools'; Version = '10.0.26100.4188'; Sha512 = 'OkiRhdDr0ngD6+wm0Ezdu1mgV8ZuknXdHwjzeHVZjYSXi9bCkYkq9Ns/X/ab9BoHb+Tq/5/RmXbdNWtC0qHipQ=='; File = 'bin/10.0.26100.0/x64/signtool.exe' }
    Dlib     = @{ Id = 'microsoft.artifactsigning.client'; Version = '1.0.128'; Sha512 = 'mPBqaR9Pwvoi8Z3PhVZzPphgf775GjEsRTubB5jMkIja4KyzbjibVSoRtNIyAyR4W4VBwrUQkackwFvF31y/lQ=='; File = 'bin/x64/Azure.CodeSigning.Dlib.dll' }
}
function Get-SigningTool([hashtable]$Package) {
    $store = Join-Path $root 'tools/signing'
    $folder = Join-Path $store "$($Package.Id).$($Package.Version)"
    $tool = Join-Path $folder $Package.File
    if (Test-Path -LiteralPath (Join-Path $folder '.verified')) { return $tool }
    New-Item -ItemType Directory -Force $store | Out-Null
    $archive = Join-Path $store "$($Package.Id).$($Package.Version).nupkg"
    if (-not (Test-Path -LiteralPath $archive)) {
        $url = "https://api.nuget.org/v3-flatcontainer/$($Package.Id)/$($Package.Version)/$($Package.Id).$($Package.Version).nupkg"
        Write-Host "Downloading $url"
        Invoke-WebRequest $url -OutFile $archive -TimeoutSec 300
    }
    $stream = [IO.File]::OpenRead($archive)
    try { $hash = [Convert]::ToBase64String([Security.Cryptography.SHA512]::HashData($stream)) } finally { $stream.Dispose() }
    if ($hash -cne $Package.Sha512) {
        [IO.File]::Delete($archive)
        throw "Checksum mismatch for $($Package.Id) $($Package.Version). The package was discarded; a new version needs a reviewed update of scripts/Sign-Files.ps1."
    }
    # The dlib loads the files beside it, so the package is unpacked whole and used in place.
    Expand-Archive -LiteralPath $archive -DestinationPath $folder -Force
    if (-not (Test-Path -LiteralPath $tool -PathType Leaf)) { throw "$($Package.File) is missing from $($Package.Id) $($Package.Version)." }
    Set-Content -LiteralPath (Join-Path $folder '.verified') -Value $Package.Sha512 -Encoding ascii
    $tool
}

$signtool = Get-SigningTool $packages.SignTool
if ($ToolsOnly) {
    if ($useArtifactSigning) { $null = Get-SigningTool $packages.Dlib }
    Write-Host 'The signing tools are in place and match their recorded checksums.'
    return
}
$metadata = $null
if ($useArtifactSigning) {
    if ($endpoint -notmatch '^https://[a-z0-9]+\.codesigning\.azure\.net/?$') { throw 'DIGA_SIGN_ENDPOINT must be an Azure Artifact Signing endpoint such as https://plc.codesigning.azure.net.' }
    if ($account -notmatch '^[A-Za-z0-9]{3,24}$') { throw 'DIGA_SIGN_ACCOUNT must be the Artifact Signing account name (3 to 24 letters and digits).' }
    if ($certificateProfile -notmatch '^[A-Za-z0-9_-]{5,100}$') { throw 'DIGA_SIGN_PROFILE must be the certificate profile name.' }
    $dlib = Get-SigningTool $packages.Dlib
    # Only the Azure CLI sign-in is used: azure/login in the workflow, `az login` on a developer's PC.
    $excluded = @('EnvironmentCredential', 'WorkloadIdentityCredential', 'ManagedIdentityCredential', 'SharedTokenCacheCredential', 'VisualStudioCredential',
        'VisualStudioCodeCredential', 'AzurePowerShellCredential', 'AzureDeveloperCliCredential', 'InteractiveBrowserCredential')
    $metadata = Join-Path ([IO.Path]::GetTempPath()) ('diga-signing-' + [Guid]::NewGuid().ToString('N') + '.json')
    @{ Endpoint = $endpoint.TrimEnd('/'); CodeSigningAccountName = $account; CertificateProfileName = $certificateProfile; ExcludeCredentials = $excluded } |
        ConvertTo-Json | Set-Content -LiteralPath $metadata -Encoding utf8
    $identity = @('/tr', 'http://timestamp.acs.microsoft.com', '/td', 'SHA256', '/dlib', $dlib, '/dmdf', $metadata)
}
else {
    if ($thumbprint -notmatch '^[0-9A-Fa-f]{40}$') { throw 'DIGA_SIGN_THUMBPRINT must be the 40-character SHA-1 thumbprint of the signing certificate.' }
    $timestamp = "$env:DIGA_SIGN_TIMESTAMP".Trim()
    if ($timestamp -notmatch '^https?://[A-Za-z0-9./_-]+$') { throw 'DIGA_SIGN_TIMESTAMP must be the address of an RFC 3161 timestamp server.' }
    $identity = @('/tr', $timestamp, '/td', 'SHA256', '/sha1', $thumbprint)
}
function Invoke-SignTool([string[]]$Arguments) {
    # The Inno Setup compiler waits for this script without a limit, so a signing service that never answers must not hang the build.
    $start = [Diagnostics.ProcessStartInfo]::new($signtool)
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true; $start.RedirectStandardInput = $true
    $process = [Diagnostics.Process]::Start($start)
    try {
        $process.StandardInput.Close()
        $stdout = $process.StandardOutput.ReadToEndAsync(); $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(300000)) { $process.Kill($true); throw 'signtool did not finish within five minutes.' }
        $process.WaitForExit()
        [pscustomobject]@{ ExitCode = $process.ExitCode; Output = ($stdout.Result + $stderr.Result) }
    }
    finally { $process.Dispose() }
}
try {
    for ($attempt = 1; ; $attempt++) {
        $result = Invoke-SignTool (@('sign', '/v', '/fd', 'SHA256') + $identity + $files)
        # Exit code 2 means "completed with warnings", which is how signtool reports a signature without a timestamp.
        if ($result.ExitCode -eq 0) { break }
        Write-Host $result.Output
        if ($attempt -ge 3) { throw "signtool failed with exit code $($result.ExitCode) after $attempt attempts." }
        Write-Host "signtool exit code $($result.ExitCode); trying again in $(10 * $attempt) seconds."
        Start-Sleep -Seconds (10 * $attempt)
    }
}
finally { if ($metadata -and (Test-Path -LiteralPath $metadata)) { [IO.File]::Delete($metadata) } }
& "$PSScriptRoot/Test-Signature.ps1" -Path $files
