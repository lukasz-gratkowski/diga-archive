#ifndef AppVersion
  #define AppVersion "0.6.4"
#endif
#ifndef PublishDir
  #define PublishDir "..\artifacts\publish"
#endif
; The FFmpeg package is described once, in src\Diga.Core\Media\FfmpegPackage.json. scripts\New-Release.ps1 passes it in.
#ifndef FfmpegSha256
  #error Compile this script with scripts\New-Release.ps1, which supplies the FFmpeg package description.
#endif

[Setup]
AppId={{B9726F3D-42CA-4270-9870-529922EAC106}
AppName=AMG DIGA Archive
AppVersion={#AppVersion}
AppPublisher=DIGA contributors
AppPublisherURL=https://github.com/lukasz-gratkowski/diga-archive
DefaultDirName={localappdata}\Programs\DIGA
DefaultGroupName=DIGA
SetupIconFile=..\src\Diga.App\Assets\Diga.ico
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.22000
OutputDir=..\artifacts\release
OutputBaseFilename=DIGA-{#AppVersion}-win-x64-setup
Compression=lzma2/fast
SolidCompression=yes
WizardStyle=modern
LanguageDetectionMethod=uilanguage
ShowLanguageDialog=yes
UsePreviousLanguage=no
LicenseFile=..\LICENSE
UninstallDisplayIcon={app}\Diga.exe
CloseApplications=yes
RestartApplications=no
DisableProgramGroupPage=yes
VersionInfoVersion={#AppVersion}
; scripts\New-Release.ps1 defines the sign tool when a signing identity is configured; the installer and its uninstaller are
; then signed by scripts\Sign-Files.ps1, which makes its own retries.
#ifdef SignToolName
SignTool={#SignToolName}
SignedUninstaller=yes
SignToolRetryCount=0
#endif

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "pl"; MessagesFile: "compiler:Languages\Polish.isl"

[CustomMessages]
en.CreateDesktopShortcut=Create a desktop shortcut
pl.CreateDesktopShortcut=Utwórz skrót na pulpicie
en.OpenApplication=Open AMG DIGA Archive
pl.OpenApplication=Otwórz AMG DIGA Archive
en.FfmpegGroup=FFmpeg is not included in this installer:
pl.FfmpegGroup=Ten instalator nie zawiera FFmpeg:
en.FfmpegTask=Download FFmpeg {#FfmpegVersion} ({#FfmpegSizeText}), needed to save recordings as MKV, MP4 or MPEG and to preview them. Optional: the application can download it later.
pl.FfmpegTask=Pobierz FFmpeg {#FfmpegVersion} ({#FfmpegSizeText}), potrzebny do zapisywania nagrań jako MKV, MP4 lub MPEG i do podglądu. Opcjonalnie: aplikacja może pobrać go później.
en.FfmpegDownloading=Downloading FFmpeg {#FfmpegVersion}
pl.FfmpegDownloading=Pobieranie FFmpeg {#FfmpegVersion}
en.FfmpegDownloadingHelp=FFmpeg comes from the GitHub releases of its distributor, Gyan Doshi (github.com/GyanD/codexffmpeg), or from gyan.dev if that fails. It is checked against the SHA-256 recorded in this installer and is licensed under the GNU GPL version 3.
pl.FfmpegDownloadingHelp=FFmpeg pochodzi z wydań jego dystrybutora, Gyana Doshiego, w serwisie GitHub (github.com/GyanD/codexffmpeg), a jeśli to się nie uda — z gyan.dev. Jest sprawdzany z sumą SHA-256 zapisaną w tym instalatorze i objęty licencją GNU GPL w wersji 3.
en.FfmpegFailed=FFmpeg could not be downloaded or unpacked:%n%n%1%n%nThe installation continues without it. An exact copy of a recording does not need FFmpeg, and you can download it later in the application: Settings, Media tools.
pl.FfmpegFailed=Nie udało się pobrać lub rozpakować FFmpeg:%n%n%1%n%nInstalacja jest kontynuowana bez niego. Zapisanie dokładnej kopii nagrania nie wymaga FFmpeg, a można go pobrać później w aplikacji: Ustawienia, Narzędzia multimedialne.
en.FfmpegCancelled=The FFmpeg download was cancelled.%n%nInstall without FFmpeg? An exact copy of a recording does not need it, and you can download it later in the application: Settings, Media tools.%n%nYes: install without FFmpeg.%nNo: go back, so that you can start the download again.
pl.FfmpegCancelled=Pobieranie FFmpeg zostało anulowane.%n%nZainstalować bez FFmpeg? Zapisanie dokładnej kopii nagrania go nie wymaga, a można go pobrać później w aplikacji: Ustawienia, Narzędzia multimedialne.%n%nTak: zainstaluj bez FFmpeg.%nNie: wróć, aby rozpocząć pobieranie ponownie.
en.FfmpegChecksum=An unpacked file does not have the expected SHA-256.
pl.FfmpegChecksum=Rozpakowany plik nie ma oczekiwanej sumy SHA-256.
en.FfmpegUnpack=The package could not be unpacked (tar.exe, code %1).
pl.FfmpegUnpack=Nie udało się rozpakować pakietu (tar.exe, kod %1).
en.FfmpegCopy=A file could not be written to %1. Check that the drive has free space: installing FFmpeg needs about 600 MB, of which 210 MB stay in use.
pl.FfmpegCopy=Nie udało się zapisać pliku w folderze %1. Sprawdź, czy na dysku jest wolne miejsce: instalacja FFmpeg wymaga około 600 MB, z czego 210 MB pozostaje zajęte.

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopShortcut}"; Flags: unchecked
; Unattended installation without FFmpeg: /MERGETASKS="!ffmpeg"
Name: "ffmpeg"; Description: "{cm:FfmpegTask}"; GroupDescription: "{cm:FfmpegGroup}"

[InstallDelete]
; Everything here is what an earlier version left behind, so it is removed only when an earlier version is installed
; (IsUpgrade). On a first installation a folder called docs in the chosen folder, or a shortcut called DIGA on the desktop,
; is the user's own.
Type: files; Name: "{group}\DIGA.lnk"; Check: IsUpgrade
Type: files; Name: "{autodesktop}\DIGA.lnk"; Check: IsUpgrade
Type: files; Name: "{group}\AmgDigaArchive.lnk"; Check: IsUpgrade
Type: files; Name: "{autodesktop}\AmgDigaArchive.lnk"; Check: IsUpgrade
; Versions up to 0.5.2 installed the disk-reader programs and their documents; an upgrade must not leave them behind.
Type: filesandordirs; Name: "{app}\tools\native"; Check: IsUpgrade
; The documents that ship change from version to version; an upgrade starts with none of the earlier ones.
Type: filesandordirs; Name: "{app}\docs"; Check: IsUpgrade

[UninstallDelete]
; FFmpeg is written by the code below, or by the application into the user's own folder, so the uninstall log does not know it.
Type: files; Name: "{app}\tools\ffmpeg.exe"
Type: files; Name: "{app}\tools\ffprobe.exe"
Type: files; Name: "{app}\tools\licenses\FFmpeg-LICENSE.txt"
Type: files; Name: "{app}\tools\licenses\FFmpeg-README.txt"
Type: dirifempty; Name: "{app}\tools\licenses"
Type: dirifempty; Name: "{app}\tools"
Type: files; Name: "{localappdata}\Diga\tools\ffmpeg.exe"
Type: files; Name: "{localappdata}\Diga\tools\ffprobe.exe"
Type: files; Name: "{localappdata}\Diga\tools\FFmpeg-LICENSE.txt"
Type: files; Name: "{localappdata}\Diga\tools\FFmpeg-README.txt"
Type: dirifempty; Name: "{localappdata}\Diga\tools"
Type: files; Name: "{localappdata}\Diga\tools\.*.partial"
Type: dirifempty; Name: "{localappdata}\Diga\tools"
; Saved cloud sign-ins (encrypted for this Windows account) must not outlive the application. Neither must what it kept for
; itself: recordings downloaded for a preview that a crash left in the default folder for temporary files, and the error log
; (versions up to 0.5.2 kept it in a folder of its own). The settings file and the saved recordings stay.
Type: filesandordirs; Name: "{localappdata}\Diga\Accounts"
Type: filesandordirs; Name: "{localappdata}\Diga\Cache"
Type: filesandordirs; Name: "{localappdata}\Diga\logs"
Type: filesandordirs; Name: "{localappdata}\DigaArchive"
Type: dirifempty; Name: "{localappdata}\Diga"

[Icons]
Name: "{group}\AMG DIGA Archive"; Filename: "{app}\Diga.exe"
Name: "{autodesktop}\AMG DIGA Archive"; Filename: "{app}\Diga.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Diga.exe"; Description: "{cm:OpenApplication}"; Flags: nowait postinstall skipifsilent

[Code]
var
  FfmpegPage: TDownloadWizardPage;
  { Set once the package is in the temporary folder and has matched its SHA-256. }
  FfmpegArchive: String;
  FfmpegError: String;

{ True when a version of this application is installed already: Windows then has its uninstall entry. }
function IsUpgrade: Boolean;
begin
  Result := RegKeyExists(HKA, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{B9726F3D-42CA-4270-9870-529922EAC106}_is1');
end;

procedure InitializeWizard;
begin
  { The page shows the address in full, so that it is plain which server is contacted. }
  FfmpegPage := CreateDownloadPage(CustomMessage('FfmpegDownloading'), CustomMessage('FfmpegDownloadingHelp'), nil);
end;

function HasSha256(const FileName, Expected: String): Boolean;
begin
  Result := False;
  if FileExists(FileName) then
    try
      Result := CompareText(GetSHA256OfFile(FileName), Expected) = 0;
    except
    end;
end;

function FfmpegIn(const Folder: String): Boolean;
begin
  Result := HasSha256(Folder + '\ffmpeg.exe', '{#FfmpegExeSha256}') and HasSha256(Folder + '\ffprobe.exe', '{#FfprobeExeSha256}');
end;

{ Nothing to download when this same FFmpeg build is already here: left by an earlier installation, or downloaded by the
  application itself into the user's own folder, which is where the application looks first. }
function FfmpegPresent: Boolean;
begin
  Result := FfmpegIn(ExpandConstant('{app}\tools')) or FfmpegIn(ExpandConstant('{localappdata}\Diga\tools'));
end;

function DownloadFfmpeg(const Url: String): Boolean;
begin
  Result := False;
  FfmpegPage.Clear;
  { The download is refused unless its SHA-256 is the recorded one. }
  FfmpegPage.Add(Url, 'ffmpeg.zip', '{#FfmpegSha256}');
  try
    FfmpegPage.Download;
    Result := True;
  except
    FfmpegError := GetExceptionMessage;
    Log('FFmpeg download from ' + Url + ': ' + FfmpegError);
  end;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (CurPageID <> wpReady) or not WizardIsTaskSelected('ffmpeg') or FfmpegPresent then
    Exit;
  FfmpegArchive := '';
  FfmpegPage.Show;
  try
    if DownloadFfmpeg('{#FfmpegUrl}') or (not FfmpegPage.AbortedByUser and DownloadFfmpeg('{#FfmpegMirrorUrl}')) then
      FfmpegArchive := ExpandConstant('{tmp}\ffmpeg.zip')
    else if FfmpegPage.AbortedByUser then
    begin
      { Cancelled: either go on without FFmpeg, or stay on the Ready page, where Install starts the download again. }
      Result := SuppressibleMsgBox(CustomMessage('FfmpegCancelled'), mbConfirmation, MB_YESNO, IDYES) = IDYES;
      if Result then
        WizardSelectTasks('!ffmpeg');
    end
    else
      SuppressibleMsgBox(FmtMessage(CustomMessage('FfmpegFailed'), [FfmpegError]), mbError, MB_OK, IDOK);
  finally
    FfmpegPage.Hide;
  end;
end;

function InstallFfmpegFile(const Source, Folder, Name: String): Boolean;
begin
  Result := ForceDirectories(Folder) and CopyFile(Source, Folder + '\' + Name, False);
  if not Result then
    FfmpegError := FmtMessage(CustomMessage('FfmpegCopy'), [Folder]);
end;

{ Only the two programs and their licence texts leave the package, under fixed names. }
function InstallFfmpeg: Boolean;
var
  Stage, Root, Tools, Entries: String;
  ExitCode: Integer;
begin
  Result := False;
  Stage := ExpandConstant('{tmp}\ffmpeg');
  Root := Stage + '\{#FfmpegRoot}';
  Tools := ExpandConstant('{app}\tools');
  Entries := '"{#FfmpegRoot}/bin/ffmpeg.exe" "{#FfmpegRoot}/bin/ffprobe.exe" "{#FfmpegRoot}/LICENSE" "{#FfmpegRoot}/README.txt"';
  if not ForceDirectories(Stage) then
  begin
    FfmpegError := FmtMessage(CustomMessage('FfmpegCopy'), [Stage]);
    Exit;
  end;
  { tar.exe is part of Windows 11 and reads zip archives. It is given names relative to the temporary folder, which is made its
    working folder: a folder name with characters outside the system code page (a user name such as Łukasz) on the command
    line makes it fail to open the archive. }
  if not Exec(ExpandConstant('{sys}\tar.exe'), '-xf ffmpeg.zip -C ffmpeg ' + Entries, ExpandConstant('{tmp}'), SW_HIDE, ewWaitUntilTerminated, ExitCode) then
    ExitCode := -1;
  if ExitCode <> 0 then
  begin
    FfmpegError := FmtMessage(CustomMessage('FfmpegUnpack'), [IntToStr(ExitCode)]);
    Exit;
  end;
  if not (HasSha256(Root + '\bin\ffmpeg.exe', '{#FfmpegExeSha256}') and HasSha256(Root + '\bin\ffprobe.exe', '{#FfprobeExeSha256}')) then
  begin
    FfmpegError := CustomMessage('FfmpegChecksum');
    Exit;
  end;
  Result := InstallFfmpegFile(Root + '\bin\ffmpeg.exe', Tools, 'ffmpeg.exe')
    and InstallFfmpegFile(Root + '\bin\ffprobe.exe', Tools, 'ffprobe.exe')
    and InstallFfmpegFile(Root + '\LICENSE', Tools + '\licenses', 'FFmpeg-LICENSE.txt')
    and InstallFfmpegFile(Root + '\README.txt', Tools + '\licenses', 'FFmpeg-README.txt');
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep <> ssPostInstall) or (FfmpegArchive = '') then
    Exit;
  if not InstallFfmpeg then
  begin
    Log('FFmpeg installation: ' + FfmpegError);
    SuppressibleMsgBox(FmtMessage(CustomMessage('FfmpegFailed'), [FfmpegError]), mbError, MB_OK, IDOK);
  end;
end;
