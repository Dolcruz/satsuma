; Satsuma installer — per-user, no admin rights required.
; Build: ISCC.exe /DAppVersion=1.0.0 /DSourceDir=..\publish installer\Satsuma.iss

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish"
#endif

[Setup]
AppId={{6E2F3B8A-5C1D-4E7B-9A51-5A75A0E1C0DE}
AppName=Satsuma
AppVersion={#AppVersion}
AppVerName=Satsuma {#AppVersion}
AppPublisher=Satsuma contributors
AppPublisherURL=https://github.com/Dolcruz/satsuma
AppSupportURL=https://github.com/Dolcruz/satsuma/issues
AppUpdatesURL=https://github.com/Dolcruz/satsuma/releases
DefaultDirName={localappdata}\Programs\Satsuma
DefaultGroupName=Satsuma
DisableProgramGroupPage=yes
DisableDirPage=yes
DisableReadyPage=yes
PrivilegesRequired=lowest
OutputDir=..\dist
OutputBaseFilename=Satsuma-Setup-{#AppVersion}
SetupIconFile=..\src\Satsuma\Assets\Satsuma.ico
UninstallDisplayIcon={app}\Satsuma.exe
UninstallDisplayName=Satsuma
Compression=lzma2/ultra64
SolidCompression=yes
LZMAUseSeparateProcess=yes
WizardStyle=modern
WizardSizePercent=100
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
AppMutex=SatsumaAppMutex
CloseApplications=force
RestartApplications=no

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "de"; MessagesFile: "compiler:Languages\German.isl"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{userprograms}\Satsuma"; Filename: "{app}\Satsuma.exe"; Comment: "Drag a file, tap Shift, drop it on a format."

[Run]
Filename: "{app}\Satsuma.exe"; Description: "{cm:LaunchProgram,Satsuma}"; Flags: nowait postinstall

[UninstallRun]
Filename: "{cmd}"; Parameters: "/C taskkill /IM Satsuma.exe /F"; Flags: runhidden; RunOnceId: "KillSatsuma"
Filename: "{app}\Satsuma.exe"; Parameters: "--cleanup"; Flags: runhidden waituntilterminated; RunOnceId: "CleanupSatsuma"

[UninstallDelete]
Type: filesandordirs; Name: "{localappdata}\Satsuma"
