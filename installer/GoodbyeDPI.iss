; GoodbyeDPI setup: installs the engine (with built-in DNS over HTTPS), WinDivert and
; the panel, adds shortcuts and an entry in Settings > Apps.
; Build with build.ps1 (needs Inno Setup 6: winget install JRSoftware.InnoSetup).

#define AppName "GoodbyeDPI"
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#define Dist "..\dist"

[Setup]
AppId={{DABD3821-F7DF-4D18-986D-333347D63041}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
VersionInfoVersion={#AppVersion}
VersionInfoDescription={#AppName} kurulumu
DefaultDirName={autopf}\GoodbyeDPI
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableReadyPage=yes
DisableWelcomePage=no
PrivilegesRequired=admin
; WinDivert's driver exists only for x64 (no ARM64, no 32 bit build here)
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0
OutputDir={#Dist}
OutputBaseFilename=GoodbyeDPI-Kurulum
SetupIconFile=..\src\icon.ico
UninstallDisplayIcon={app}\GoodbyeDPI-Panel.exe
UninstallDisplayName={#AppName}
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
CloseApplications=force
RestartApplications=no

[Languages]
Name: "tr"; MessagesFile: "compiler:Languages\Turkish.isl"

[Tasks]
Name: "desktopicon"; Description: "Masaüstüne kısayol koy"; Flags: unchecked

[Files]
Source: "{#Dist}\GoodbyeDPI-Panel.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#Dist}\x86_64\goodbyedpi.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#Dist}\x86_64\WinDivert.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#Dist}\x86_64\WinDivert64.sys"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE-GoodbyeDPI.txt"
Source: "..\deps\WinDivert-2.2.0-D\LICENSE"; DestDir: "{app}"; DestName: "LICENSE-WinDivert.txt"

[Icons]
Name: "{autoprograms}\GoodbyeDPI"; Filename: "{app}\GoodbyeDPI-Panel.exe"
Name: "{autodesktop}\GoodbyeDPI"; Filename: "{app}\GoodbyeDPI-Panel.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\GoodbyeDPI-Panel.exe"; Description: "GoodbyeDPI'yi aç"; Flags: postinstall nowait skipifsilent

[UninstallRun]
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /TN ""GoodbyeDPI Panel"" /F"; Flags: runhidden waituntilterminated; RunOnceId: "DeleteTask"
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM GoodbyeDPI-Panel.exe"; Flags: runhidden waituntilterminated; RunOnceId: "ClosePanel"
Filename: "{sys}\sc.exe"; Parameters: "stop GoodbyeDPI"; Flags: runhidden waituntilterminated; RunOnceId: "StopService"
Filename: "{sys}\timeout.exe"; Parameters: "/t 2 /nobreak"; Flags: runhidden waituntilterminated; RunOnceId: "WaitService"
Filename: "{sys}\sc.exe"; Parameters: "delete GoodbyeDPI"; Flags: runhidden waituntilterminated; RunOnceId: "DeleteService"
Filename: "{sys}\sc.exe"; Parameters: "stop WinDivert"; Flags: runhidden waituntilterminated; RunOnceId: "StopDriver"
Filename: "{sys}\ipconfig.exe"; Parameters: "/flushdns"; Flags: runhidden waituntilterminated; RunOnceId: "FlushDns"

[UninstallDelete]
Type: files; Name: "{app}\goodbyedpi.log"
Type: dirifempty; Name: "{app}"

[Registry]
Root: HKLM; Subkey: "SOFTWARE\GoodbyeDPI Panel"; Flags: uninsdeletekey dontcreatekey

[Code]
// An update replaces files that are in use: stop the engine and unload the driver first.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Code: Integer;
begin
  Exec(ExpandConstant('{sys}\sc.exe'), 'stop GoodbyeDPI', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Sleep(1500);
  Exec(ExpandConstant('{sys}\sc.exe'), 'stop WinDivert', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Result := '';
end;
