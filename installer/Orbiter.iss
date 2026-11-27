; Installeur Orbiter (Inno Setup 6)
#define AppName "Orbiter"
; La version est passée par build.ps1 (/DAppVersion=...) depuis le fichier VERSION
#ifndef AppVersion
  #define AppVersion "0.0.0-dev"
#endif
#define AppExe "Orbiter.exe"

[Setup]
AppId={{B3E1A7C4-58D2-4F6B-9A0E-7C21D4F9E513}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=Orbiter
VersionInfoVersion={#AppVersion}
AppVerName=Orbiter {#AppVersion}
AppComments=Client SSH / SFTP pour Windows
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\dist
OutputBaseFilename=Orbiter-Setup-{#AppVersion}
SetupIconFile=..\Orbiter\Assets\orbiter.ico
UninstallDisplayIcon={app}\{#AppExe}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
MinVersion=10.0.17763

[Languages]
Name: "french"; MessagesFile: "compiler:Languages\French.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "..\publish\Orbiter.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\publish\*.dll"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent
; mise à jour lancée depuis Orbiter (installation silencieuse) : relancer l'application
Filename: "{app}\{#AppExe}"; Flags: nowait; Check: WizardSilent

[UninstallDelete]
Type: filesandordirs; Name: "{localappdata}\Orbiter"
