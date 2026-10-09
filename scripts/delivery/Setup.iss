#ifndef PayloadRoot
  #error PayloadRoot is required
#endif
#ifndef OutputRoot
  #error OutputRoot is required
#endif
#ifndef AppVersion
  #define AppVersion "0.1.0-dev.13"
#endif

[Setup]
AppId=RatoObservatorTabajara-{#AppVersion}
AppName=Rato Observator Tabajara
AppVersion={#AppVersion}
AppPublisher=Paulo Costa Carvalho
AppPublisherURL=https://github.com/pcarvalho75/RatoObservatorTabajara
AppSupportURL=https://github.com/pcarvalho75/RatoObservatorTabajara/issues
DefaultDirName={localappdata}\RatoObservatorTabajara\versions\{#AppVersion}-setup
DefaultGroupName=RatoObservatorTabajara
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir={#OutputRoot}
OutputBaseFilename=RatoObservatorTabajara-Setup-{#AppVersion}
Compression=lzma2/fast
SolidCompression=yes
WizardStyle=modern
DisableProgramGroupPage=yes
UsePreviousAppDir=no
Uninstallable=no
CloseApplications=no
RestartApplications=no
LicenseFile={#PayloadRoot}\licenses\APPLICATION-GPL-3.0.txt
SetupLogging=yes

[Files]
Source: "{#PayloadRoot}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{userprograms}\RatoObservatorTabajara\Rato Observator Tabajara"; Filename: "{app}\desktop\MouseTrackerTabajara.exe"; WorkingDir: "{app}\desktop"

[Run]
Filename: "{app}\desktop\MouseTrackerTabajara.exe"; Description: "Open Mouse Tracker Tabajara"; Flags: nowait postinstall skipifsilent

[Code]
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if DirExists(ExpandConstant('{app}')) then
    Result := 'This version folder already exists. Choose a new folder to preserve previous installations and projects.';
end;
