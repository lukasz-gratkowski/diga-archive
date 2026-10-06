#ifndef AppVersion
  #define AppVersion "0.5.2"
#endif
#ifndef PublishDir
  #define PublishDir "..\artifacts\publish"
#endif

[Setup]
AppId={{B9726F3D-42CA-4270-9870-529922EAC106}
AppName=AMG DIGA Archive
AppVersion={#AppVersion}
AppPublisher=DIGA contributors
AppPublisherURL=https://github.com/lukasz-gratkowski/AmgDigaArchive
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

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "pl"; MessagesFile: "compiler:Languages\Polish.isl"

[CustomMessages]
en.CreateDesktopShortcut=Create a desktop shortcut
pl.CreateDesktopShortcut=Utwórz skrót na pulpicie
en.OpenApplication=Open AMG DIGA Archive
pl.OpenApplication=Otwórz AMG DIGA Archive

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopShortcut}"; Flags: unchecked

[InstallDelete]
Type: files; Name: "{group}\DIGA.lnk"
Type: files; Name: "{autodesktop}\DIGA.lnk"
Type: files; Name: "{group}\AmgDigaArchive.lnk"
Type: files; Name: "{autodesktop}\AmgDigaArchive.lnk"

[Icons]
Name: "{group}\AMG DIGA Archive"; Filename: "{app}\Diga.exe"
Name: "{autodesktop}\AMG DIGA Archive"; Filename: "{app}\Diga.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Diga.exe"; Description: "{cm:OpenApplication}"; Flags: nowait postinstall skipifsilent
