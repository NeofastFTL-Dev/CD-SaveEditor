; Inno Setup script for CD-SaveEditor.
; Built by CI with: iscc /DMyAppVersion=1.2.0 installer\windows\CD-SaveEditor.iss
; The publish folder is expected at publish\win-x64 (relative to the repository root).

#ifndef MyAppVersion
  #define MyAppVersion GetEnv('CD_VERSION')
#endif

#define MyAppName "CD-SaveEditor"
#define MyAppPublisher "NeofastFTL-Dev"
#define MyAppUrl "https://github.com/NeofastFTL-Dev/CD-SaveEditor"
#define MyAppExeName "CD-SaveEditor.exe"

[Setup]
AppId={{7C4E1B2A-5D3F-4A6B-9C8E-2F1D0A9B7C33}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppUrl}
AppSupportURL={#MyAppUrl}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=..\..\dist
OutputBaseFilename=CD-SaveEditor-{#MyAppVersion}-win-x64-setup
SetupIconFile=..\..\assets\icon.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequiredOverridesAllowed=dialog
MinVersion=10.0

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\..\publish\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent
