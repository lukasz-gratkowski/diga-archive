[CmdletBinding()]
param([string]$PublishDirectory = '', [string]$InstallerPath = '', [switch]$TestInstaller,
    [ValidateSet('', 'en', 'pl')][string]$InstallerLanguage = '')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (-not $PublishDirectory) { $PublishDirectory = Join-Path $root 'artifacts/publish' }
$PublishDirectory = [IO.Path]::GetFullPath($PublishDirectory)
function Test-AppLaunch([string]$Directory) {
    $exe = Join-Path $Directory 'Diga.exe'
    foreach ($required in @('Diga.exe','App.xbf','Diga.pri','Assets/BrandMark.png','Assets/Diga.ico')) {
        if (-not (Test-Path -LiteralPath (Join-Path $Directory $required) -PathType Leaf)) { throw "Missing application payload: $required in $Directory" }
    }
    $started = Get-Date
    $process = $null
    try {
        $process = Start-Process -FilePath $exe -WorkingDirectory $Directory -WindowStyle Hidden -PassThru
        if ($process.WaitForExit(10000)) { throw "DIGA exited during startup with code $($process.ExitCode)." }
        $process.Refresh()
        if ($process.MainWindowHandle -eq 0) { throw 'DIGA did not create a top-level window.' }
        Write-Host "App window created: $exe"
    }
    catch {
        # Retain useful evidence on hosted runners, whose desktop/media components
        # differ from the supported Windows 11 client environment.
        $failure = $_
        try {
            $diagnosticPath = Join-Path $root 'artifacts/package-diagnostics.txt'
            $report = @("Startup failure: $failure", "Executable: $exe", "OS: $([Environment]::OSVersion)", "Started: $($started.ToUniversalTime().ToString('o'))")
            New-Item -ItemType Directory -Path (Split-Path $diagnosticPath -Parent) -Force | Out-Null
            $report | Set-Content -LiteralPath $diagnosticPath -Encoding utf8
            $appLog = Join-Path $env:LOCALAPPDATA 'DigaArchive/logs/errors.log'
            if ((Test-Path -LiteralPath $appLog) -and (Get-Item -LiteralPath $appLog).LastWriteTime -ge $started) {
                $report += Get-Content -LiteralPath $appLog -Tail 80
            }
            $events = Get-WinEvent -FilterHashtable @{ LogName='Application'; StartTime=$started } -ErrorAction SilentlyContinue |
                Where-Object { $_.Message -match 'Diga\.(exe|dll)' } | Select-Object -First 8
            $report += $events | ForEach-Object { "$($_.TimeCreated) $($_.ProviderName): $($_.Message)" }
            $report += Get-ChildItem -LiteralPath $Directory -File | ForEach-Object { "Payload: $($_.Name) $($_.Length) bytes" }
            $report | Set-Content -LiteralPath $diagnosticPath -Encoding utf8
            $report | Write-Host
        }
        catch { Write-Warning "Could not collect complete startup diagnostics: $_" }
        throw $failure
    }
    finally {
        if ($process -and -not $process.HasExited) {
            $null = $process.CloseMainWindow()
            if (-not $process.WaitForExit(5000)) { $process.Kill($true); $process.WaitForExit() }
        }
        if ($process) { $process.Dispose() }
    }
}
Test-AppLaunch $PublishDirectory
if ($TestInstaller) {
    $registration = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{B9726F3D-42CA-4270-9870-529922EAC106}_is1'
    if (Test-Path $registration) { throw 'DIGA is already installed for this user. Run installer smoke testing in a clean user account.' }
    if (-not $InstallerPath) {
        [xml]$properties = Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props')
        $version = [string]$properties.Project.PropertyGroup.Version
        if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Version must be a three-part numeric version.' }
        $InstallerPath = Join-Path $root "artifacts/release/DIGA-$version-win-x64-setup.exe"
    }
    if (-not (Test-Path -LiteralPath $InstallerPath)) { throw "Expected installer was not found: $InstallerPath" }
    $stage = Join-Path $root ('artifacts/install-smoke-' + [Guid]::NewGuid().ToString('N'))
    $installerArguments = @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/NOICONS','/TASKS=',('/DIR="' + $stage + '"'))
    if ($InstallerLanguage) { $installerArguments += "/LANG=$InstallerLanguage" }
    $process = Start-Process -FilePath $InstallerPath -ArgumentList $installerArguments -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit(120000)) { $process.Kill($true); throw 'Installer timed out.' }
    if ($process.ExitCode -ne 0) { throw "Installer failed: $($process.ExitCode)" }
    try {
        Test-AppLaunch $stage
        if (-not (Test-Path $registration)) { throw 'Uninstall registration missing.' }
        if ($InstallerLanguage -and (Get-ItemPropertyValue -LiteralPath $registration -Name 'Inno Setup: Language') -ne $InstallerLanguage) {
            throw 'Installer did not use the requested language.'
        }
        foreach ($required in @('tools/ffmpeg.exe','tools/MediaInfo.dll','tools/native/udf_dump.exe','LICENSE')) {
            if (-not (Test-Path -LiteralPath (Join-Path $stage $required))) { throw "Installed payload is missing $required" }
        }
    }
    finally {
        $uninstaller = Join-Path $stage 'unins000.exe'
        if (Test-Path -LiteralPath $uninstaller) {
            $uninstall = Start-Process -FilePath $uninstaller -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART') -WindowStyle Hidden -PassThru
            if (-not $uninstall.WaitForExit(120000)) { $uninstall.Kill($true); throw 'Uninstaller timed out.' }
            if ($uninstall.ExitCode -ne 0) { throw "Uninstaller failed: $($uninstall.ExitCode)" }
        }
    }
    if (Test-Path $registration) { throw 'Uninstall registration was not removed.' }
    if (Test-Path (Join-Path $stage 'Diga.exe')) { throw 'Uninstaller left the executable behind.' }
    Write-Host 'Installer, installed launch, and uninstall smoke checks passed.'
}
