[CmdletBinding()]
param([string]$PublishDirectory = '', [string]$InstallerPath = '', [switch]$TestInstaller,
    [ValidateSet('', 'en', 'pl')][string]$InstallerLanguage = '',
    # Also lets the installer download FFmpeg (about 110 MB from its distributor) and checks what it installed.
    [switch]$WithFfmpegDownload,
    # Fails when the files are not signed. The release workflow passes it once signing is switched on.
    [switch]$RequireSignature)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (-not $PublishDirectory) { $PublishDirectory = Join-Path $root 'artifacts/publish' }
$PublishDirectory = [IO.Path]::GetFullPath($PublishDirectory)
# A signed release signs the project's own programs and the diagnostics script, and its diagnostics launcher asks PowerShell
# to check that signature. An unsigned release keeps the launcher that switches the check off; the two must never be mixed.
# The launcher, its instructions and the script lie together: in the application's diagnostics folder and in the diagnostics kit.
function Test-DiagnosticsFolder([string]$Folder, [bool]$Signed) {
    foreach ($needed in @('Run-DlnaDiagnostics.cmd', 'README.md', 'Test-DlnaRecorder.ps1')) {
        if (-not (Test-Path -LiteralPath (Join-Path $Folder $needed) -PathType Leaf)) { throw "Missing diagnostics file: $needed in $Folder" }
    }
    $launcher = Get-Content -LiteralPath (Join-Path $Folder 'Run-DlnaDiagnostics.cmd') -Raw
    $policy = if ($Signed) { 'AllSigned' } else { 'Bypass' }
    $found = @([regex]::Matches($launcher, 'DiagnosticPolicy=(\w+)"') | ForEach-Object { $_.Groups[1].Value })
    if (($found -join ',') -cne $policy) { throw "The diagnostics launcher in $Folder must run its script with the execution policy $policy; it names '$($found -join ',')'." }
    if ($Signed -and (Select-String -LiteralPath (Join-Path $Folder 'README.md') -Pattern '-ExecutionPolicy\s+Bypass' -Quiet)) { throw "The diagnostics instructions in $Folder tell the reader to switch the execution policy off, although the script is signed." }
}
function Test-OwnFiles([string]$Directory, [string[]]$Installer = @()) {
    # The installer says whether the release is signed; a portable folder has only the application to ask.
    $decisive = if ($Installer) { $Installer[0] } else { Join-Path $Directory 'Diga.exe' }
    $signed = [bool](Get-AuthenticodeSignature -LiteralPath $decisive).SignerCertificate
    if ($RequireSignature -and -not $signed) { throw "$(Split-Path $decisive -Leaf) is not signed." }
    Test-DiagnosticsFolder (Join-Path $Directory 'diagnostics') $signed
    if ($signed) { & "$PSScriptRoot/Test-Signature.ps1" -Path ($Installer + @('Diga.exe', 'Diga.dll', 'Diga.Core.dll', 'diagnostics/Test-DlnaRecorder.ps1' | ForEach-Object { Join-Path $Directory $_ })) }
}
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
            $appLog = Join-Path $env:LOCALAPPDATA 'Diga/logs/errors.log'
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
Test-OwnFiles $PublishDirectory
foreach ($excluded in @('tools/ffmpeg.exe','tools/ffprobe.exe')) {
    if (Test-Path -LiteralPath (Join-Path $PublishDirectory $excluded)) { throw "The portable payload contains $excluded. FFmpeg must not be packaged." }
}
if ($TestInstaller) {
    $registration = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{B9726F3D-42CA-4270-9870-529922EAC106}_is1'
    if (Test-Path $registration) {
        # A run of this test that was cut off after installing leaves its copy registered. Removing that copy needs care, because
        # its uninstaller deletes folders of the Windows account that are, or soon are again, the developer's own.
        $left = "$((Get-ItemProperty -LiteralPath $registration).'Inno Setup: App Path')"
        if ($left -like '*install-smoke-*') {
            throw ("An earlier run of this test was cut off and left its copy installed in $left. Its uninstaller also deletes the folders Accounts, logs, Cache and a downloaded FFmpeg in $(Join-Path $env:LOCALAPPDATA 'Diga'), and $(Join-Path $env:LOCALAPPDATA 'DigaArchive'). " +
                "Leave your own folders set aside if their names end in .smoke-..., or move them away yourself; then run `"$(Join-Path $left 'unins000.exe')`" /VERYSILENT and give the folders their names back.")
        }
        throw 'DIGA is already installed for this user. Run installer smoke testing in a clean user account.'
    }
    if (-not $InstallerPath) {
        [xml]$properties = Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props')
        $version = [string]$properties.Project.PropertyGroup.Version
        if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Version must be a three-part numeric version.' }
        $InstallerPath = Join-Path $root "artifacts/release/DIGA-$version-win-x64-setup.exe"
    }
    if (-not (Test-Path -LiteralPath $InstallerPath)) { throw "Expected installer was not found: $InstallerPath" }
    $InstallerPath = (Get-Item -LiteralPath $InstallerPath).FullName
    # The diagnostics kit is the one file of a release that users are asked to download and run as a script, so the package
    # itself is opened and held to the same rule as the application's diagnostics folder. A signed release whose kit still
    # held the unsigned script and the launcher that switches the check off would otherwise pass every check here.
    $kits = @(Get-ChildItem -LiteralPath (Split-Path $InstallerPath -Parent) -Filter 'DIGA-*-dlna-diagnostics.zip' -File)
    if ($kits.Count -ne 1) { throw "Expected exactly one diagnostics kit (DIGA-...-dlna-diagnostics.zip) beside the installer; found $($kits.Count)." }
    $kit = Join-Path $root ('artifacts/kit-smoke-' + [Guid]::NewGuid().ToString('N'))
    try {
        Expand-Archive -LiteralPath $kits[0].FullName -DestinationPath $kit
        $kitSigned = [bool](Get-AuthenticodeSignature -LiteralPath $InstallerPath).SignerCertificate
        Test-DiagnosticsFolder $kit $kitSigned
        # Together with the installer, so that the kit's script must come from the same publisher.
        if ($kitSigned) { & "$PSScriptRoot/Test-Signature.ps1" -Path @($InstallerPath, (Join-Path $kit 'Test-DlnaRecorder.ps1')) }
        Write-Host "Diagnostics kit checked: $($kits[0].Name)"
    }
    finally { if (Test-Path -LiteralPath $kit) { [IO.Directory]::Delete($kit, $true) } }
    $stage = Join-Path $root ('artifacts/install-smoke-' + [Guid]::NewGuid().ToString('N'))
    $tasks = if ($WithFfmpegDownload) { '/TASKS=ffmpeg' } else { '/TASKS=' }
    $installerArguments = @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/NOICONS',$tasks,('/DIR="' + $stage + '"'),('/LOG="' + $stage + '-install.log"'))
    if ($InstallerLanguage) { $installerArguments += "/LANG=$InstallerLanguage" }
    # Room for the installed application and, with FFmpeg, for the package, what is unpacked from it and the two programs once
    # more. The check stops the test before a download that cannot be stored.
    $room = if ($WithFfmpegDownload) { 1GB } else { 0.5GB }
    foreach ($place in @($root, [IO.Path]::GetTempPath())) {
        try { $free = [IO.DriveInfo]::new([IO.Path]::GetPathRoot($place)).AvailableFreeSpace } catch { continue }
        if ($free -lt $room) { throw ('The installer test needs {0:0.#} GB of free space on the drive of {1}; {2:0.##} GB are free.' -f ($room / 1GB), $place, ($free / 1GB)) }
    }
    # The application keeps a downloaded FFmpeg, saved cloud sign-ins, its error log (versions up to 0.5.2: in a folder of its
    # own) and, by default, its temporary files in the user's profile, and the uninstaller removes them all. On a developer's PC
    # those folders belong to the developer's own copy, so each is set aside and put back at the end. All of them are set aside
    # before the installer starts: a folder that Windows will not rename then stops the test while nothing is installed. Were
    # that found out only before the uninstall, the test's copy would stay installed, and the one program that removes it would
    # delete the developer's folders with it. The FFmpeg folder has to be out of the way that early in any case, because the
    # installer downloads no FFmpeg when that folder already holds the same build. Only the settings file stays where it is:
    # the uninstaller must not touch it.
    if (-not $env:LOCALAPPDATA) { throw 'LOCALAPPDATA is not set.' }
    $userData = Join-Path $env:LOCALAPPDATA 'Diga'
    $ownCache = Join-Path $userData 'Cache'
    $ownFolders = @('tools', 'Accounts', 'logs' | ForEach-Object { Join-Path $userData $_ }) + $ownCache + (Join-Path $env:LOCALAPPDATA 'DigaArchive')
    $settings = Join-Path $userData 'settings.json'
    $hadSettings = Test-Path -LiteralPath $settings -PathType Leaf
    $stranded = @(foreach ($own in $ownFolders) {
        $parent = Split-Path $own -Parent
        if (Test-Path -LiteralPath $parent) { [IO.Directory]::GetDirectories($parent, (Split-Path $own -Leaf) + '.smoke-*') }
    })
    if ($stranded) { throw "An earlier run of this test was interrupted and left folders set aside. Give each its own name back (the part before .smoke-) first:`n$($stranded -join "`n")" }
    $setAside = [Collections.Generic.List[object]]::new()
    $standIns = @()
    $cacheInUse = $false
    function Move-Aside([string]$Own) {
        if (-not (Test-Path -LiteralPath $Own)) { return }
        $aside = "$Own.smoke-" + [Guid]::NewGuid().ToString('N')
        # A plain rename, which happens entirely or not at all. Move-Item would answer a refusal by copying the folder. Windows
        # refuses while a file in the folder is open; a program that has just ended, or a virus scanner, lets go within a moment.
        for ($attempt = 1; ; $attempt++) {
            try { [IO.Directory]::Move($Own, $aside); break }
            catch { if ($attempt -ge 3) { throw }; Start-Sleep -Milliseconds 500 }
        }
        $setAside.Add([pscustomobject]@{ Own = $Own; Aside = $aside })
    }
    try {
        foreach ($own in $ownFolders) {
            try { Move-Aside $own }
            catch {
                $reason = $_.Exception.GetBaseException().Message
                # A running copy of the application keeps a lock file open in its folder for temporary files. That folder stays where
                # it is and is left out of the check. Any other folder that is in use ends the test here, before the installer.
                if ($own -ne $ownCache) { throw "Could not set aside $own ($reason). A file in that folder is probably open in another program. The installer was not started and nothing is installed; what was set aside is put back. Close that program and run the test again." }
                $cacheInUse = $true
                Write-Warning "$own is in use, probably by a running copy of the application ($reason). It is not set aside: the uninstaller removes from it what it can, and whether it removes the folder is not checked."
            }
        }
        $process = Start-Process -FilePath $InstallerPath -ArgumentList $installerArguments -WindowStyle Hidden -PassThru
        if (-not $process.WaitForExit($(if ($WithFfmpegDownload) { 900000 } else { 120000 }))) { $process.Kill($true); throw 'Installer timed out.' }
        if ($process.ExitCode -ne 0) { throw "Installer failed: $($process.ExitCode)" }
        try {
            Test-AppLaunch $stage
            if (-not (Test-Path $registration)) { throw 'Uninstall registration missing.' }
            if ($InstallerLanguage -and (Get-ItemPropertyValue -LiteralPath $registration -Name 'Inno Setup: Language') -ne $InstallerLanguage) {
                throw 'Installer did not use the requested language.'
            }
            foreach ($required in @('tools/MediaInfo.dll','LICENSE','THIRD-PARTY-NOTICES.md')) {
                if (-not (Test-Path -LiteralPath (Join-Path $stage $required))) { throw "Installed payload is missing $required" }
            }
            # A signed installer must install signed programs and a signed uninstaller, which exists only in an installed copy.
            Test-OwnFiles $stage @($InstallerPath, (Join-Path $stage 'unins000.exe'))
            if (Test-Path -LiteralPath (Join-Path $stage 'tools/native')) { throw 'Installed payload still contains tools/native.' }
            $ffmpeg = Get-Content -LiteralPath (Join-Path $root 'src/Diga.Core/Media/FfmpegPackage.json') -Raw | ConvertFrom-Json
            $programs = @(@{ File='tools/ffmpeg.exe'; Hash=$ffmpeg.ffmpegSha256 }, @{ File='tools/ffprobe.exe'; Hash=$ffmpeg.ffprobeSha256 })
            if ($WithFfmpegDownload) {
                foreach ($program in $programs) {
                    $path = Join-Path $stage $program.File
                    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "The installer did not install $($program.File). See $stage-install.log" }
                    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $program.Hash) { throw "$($program.File) does not match the recorded checksum." }
                    $null = & $path -version
                    if ($LASTEXITCODE -ne 0) { throw "$($program.File) does not start." }
                }
                if (-not (Test-Path -LiteralPath (Join-Path $stage 'tools/licenses/FFmpeg-LICENSE.txt') -PathType Leaf)) { throw 'The FFmpeg licence text was not installed.' }
                Write-Host 'The installer downloaded, verified and installed FFmpeg.'
            }
            else {
                # Without the FFmpeg option the installer must install none of it: the payload does not contain FFmpeg.
                foreach ($program in $programs) { if (Test-Path -LiteralPath (Join-Path $stage $program.File)) { throw "$($program.File) was installed although FFmpeg was not selected." } }
            }
        }
        finally {
            $uninstaller = Join-Path $stage 'unins000.exe'
            if (Test-Path -LiteralPath $uninstaller) {
                # Stand-ins for what the application leaves in a user's profile, to see that uninstalling removes each of them: a saved
                # sign-in, the error log, the temporary files of a session that ended in a crash and the log folder of an old version.
                # The developer's own folders are out of these places, so a folder that is there now was made by the copy started above
                # and has to go as well. The exception is a folder for temporary files that is in use: it is the developer's still.
                $session = 'session-' + [Guid]::NewGuid().ToString('N')
                foreach ($standIn in @(
                        @{ Folder = (Join-Path $userData 'Accounts'); File = 'OneDrive-SMOKE.bin'; What = 'saved cloud sign-ins' },
                        @{ Folder = (Join-Path $userData 'logs'); File = 'errors.log'; What = 'the error log' },
                        @{ Folder = $ownCache; File = "$session/.lock"; What = 'temporary files' },
                        @{ Folder = (Join-Path $env:LOCALAPPDATA 'DigaArchive'); File = 'logs/errors.log'; What = 'the error log of versions up to 0.5.2' })) {
                    if ($cacheInUse -and $standIn.Folder -eq $ownCache) { continue }
                    $standIns += [pscustomobject]$standIn
                    $file = Join-Path $standIn.Folder $standIn.File
                    $null = New-Item -ItemType Directory -Path (Split-Path $file -Parent) -Force
                    Set-Content -LiteralPath $file -Value 'smoke' -Encoding ascii
                }
                $uninstall = Start-Process -FilePath $uninstaller -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART') -WindowStyle Hidden -PassThru
                if (-not $uninstall.WaitForExit(120000)) { $uninstall.Kill($true); throw 'Uninstaller timed out.' }
                if ($uninstall.ExitCode -ne 0) { throw "Uninstaller failed: $($uninstall.ExitCode)" }
                foreach ($standIn in $standIns) { if (Test-Path -LiteralPath $standIn.Folder) { throw "Uninstaller left $($standIn.What) behind: $($standIn.Folder)" } }
                # The settings are the one thing the uninstaller keeps, so that a later installation starts as the user left it.
                if ($hadSettings -and -not (Test-Path -LiteralPath $settings -PathType Leaf)) { throw 'Uninstaller removed settings.json.' }
                Write-Host "Uninstalling removed: $(($standIns | ForEach-Object What) -join ', ').$(if ($hadSettings) { ' settings.json is still there.' })"
            }
        }
    }
    finally {
        # Every step is tried, whatever happens to another: what was set aside is the developer's own.
        foreach ($standIn in $standIns) {
            try { if (Test-Path -LiteralPath $standIn.Folder) { [IO.Directory]::Delete($standIn.Folder, $true) } }
            catch { Write-Warning "Could not remove the stand-in $($standIn.Folder): $_" }
        }
        # Since a folder was set aside, its place has belonged to the test. What lies there now, after an uninstall that failed or
        # never ran, was written by the copy the test installed and started; it goes, so that the developer's folder gets its
        # name back. The rename itself never overwrites or merges.
        $notPutBack = @(foreach ($item in $setAside) {
            try {
                if (Test-Path -LiteralPath $item.Own) { [IO.Directory]::Delete($item.Own, $true) }
                [IO.Directory]::Move($item.Aside, $item.Own)
            }
            catch { "$($item.Aside) -> $($item.Own): $($_.Exception.GetBaseException().Message)" }
        })
        if ($notPutBack) { throw "Could not put back what was set aside for the test. Move it back by hand:`n$($notPutBack -join "`n")" }
    }
    if (Test-Path $registration) { throw 'Uninstall registration was not removed.' }
    if (Test-Path (Join-Path $stage 'Diga.exe')) { throw 'Uninstaller left the executable behind.' }
    if (Test-Path (Join-Path $stage 'tools/ffmpeg.exe')) { throw 'Uninstaller left FFmpeg behind.' }
    Write-Host 'Installer, installed launch, and uninstall smoke checks passed.'
}
