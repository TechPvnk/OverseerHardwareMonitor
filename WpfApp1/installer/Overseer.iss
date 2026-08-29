#define MyAppName "Overseer Hardware Monitor"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "TechPvnk"
#define MyAppURL "https://github.com/TechPvnk/OverseerHardwareMonitor"
#define MyAppExeName "Overseer.exe"
#define MyPublishDir "..\bin\Release\net8.0-windows\win-x64\publish"

[Setup]
AppId={{F3E5A4EA-9FDC-4E82-9F2F-839DE75AF0E5}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
AppUpdatesURL={#MyAppURL}/releases
DefaultDirName={autopf}\Overseer
DefaultGroupName=Overseer
DisableProgramGroupPage=yes
LicenseFile=..\LICENSE
OutputDir=..\dist
OutputBaseFilename=Overseer-Setup-{#MyAppVersion}-x64
SetupIconFile=..\Themes\favicon.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
WizardImageFile=assets\installer-vertical.png
WizardSmallImageFile=assets\installer-small.png
WizardImageStretch=yes
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
VersionInfoVersion={#MyAppVersion}
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription=Overseer Hardware Monitor Setup
VersionInfoProductName={#MyAppName}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"

[CustomMessages]
english.WelcomeTitle=Thanks for using Overseer Hardware Monitor
english.WelcomeText=I hope it is just as useful to you as it has been for me. It is the perfect all-in-one monitor. At least for me.%n%nClick Next to continue.
spanish.WelcomeTitle=Gracias por usar Overseer Hardware Monitor
spanish.WelcomeText=Espero que te sea tan útil como lo ha sido para mí. Es el monitor todo en uno perfecto. Al menos para mí.%n%nHaz clic en Siguiente para continuar.

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "{#MyPublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Overseer"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"
Name: "{group}\Uninstall Overseer"; Filename: "{uninstallexe}"
Name: "{autodesktop}\Overseer"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\helpers\PawnIO_setup.exe"; Parameters: "-install -silent"; StatusMsg: "Installing PawnIO hardware helper..."; Flags: waituntilterminated skipifdoesntexist; Check: not PawnIoIsInstalled
Filename: "{app}\{#MyAppExeName}"; Description: "Launch Overseer"; Flags: nowait postinstall skipifsilent runascurrentuser

[Code]
function InitializeSetup(): Boolean;
begin
  Result := True;
  if not IsWin64 then
  begin
    MsgBox('Overseer requires a 64-bit version of Windows.', mbError, MB_OK);
    Result := False;
  end;
end;

function PawnIoIsInstalled(): Boolean;
begin
  Result := RegKeyExists(HKLM64, 'SYSTEM\CurrentControlSet\Services\PawnIO') or
    FileExists(ExpandConstant('{sys}\drivers\PawnIO.sys'));
end;

procedure InitializeWizard();
begin
  WizardForm.WelcomeLabel1.Caption := ExpandConstant('{cm:WelcomeTitle}');
  WizardForm.WelcomeLabel1.Font.Color := $000000C8;
  WizardForm.WelcomeLabel1.Font.Style := [fsBold];
  WizardForm.WelcomeLabel2.Caption := ExpandConstant('{cm:WelcomeText}');
  WizardForm.WelcomeLabel2.Font.Color := $00202020;
end;
