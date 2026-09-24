#define MyAppName "Overseer Hardware Monitor"
#define MyAppVersion "1.1.2"
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
UsePreviousAppDir=yes
UsePreviousGroup=yes
UsePreviousLanguage=yes
UsePreviousTasks=yes
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
CloseApplications=yes
CloseApplicationsFilter={#MyAppExeName}
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
english.UpgradeTitle=Updating Overseer Hardware Monitor
english.UpgradeText=An earlier version of Overseer was found. Setup will update it while keeping your settings, language, and shortcuts.%n%nClick Next to continue.
english.DowngradeMessage=A newer version of Overseer (%1) is already installed. Setup cannot replace it with version %2.
spanish.WelcomeTitle=Gracias por usar Overseer Hardware Monitor
spanish.WelcomeText=Espero que te sea tan útil como lo ha sido para mí. Es el monitor todo en uno perfecto. Al menos para mí.%n%nHaz clic en Siguiente para continuar.
spanish.UpgradeTitle=Actualizando Overseer Hardware Monitor
spanish.UpgradeText=Se encontró una versión anterior de Overseer. El instalador la actualizará y conservará tu configuración, idioma y accesos directos.%n%nHaz clic en Siguiente para continuar.
spanish.DowngradeMessage=Ya está instalada una versión más reciente de Overseer (%1). El instalador no puede reemplazarla con la versión %2.

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
const
  UninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{F3E5A4EA-9FDC-4E82-9F2F-839DE75AF0E5}_is1';

var
  UpgradeDetected: Boolean;
  InstalledVersion: String;

function NextVersionPart(const Version: String; var Position: Integer): Integer;
var
  EndPosition: Integer;
  Part: String;
begin
  EndPosition := Position;
  while (EndPosition <= Length(Version)) and (Version[EndPosition] <> '.') do
    EndPosition := EndPosition + 1;

  Part := Copy(Version, Position, EndPosition - Position);
  Result := StrToIntDef(Part, 0);
  Position := EndPosition + 1;
end;

function CompareVersions(const Left, Right: String): Integer;
var
  Index: Integer;
  LeftPart: Integer;
  LeftPosition: Integer;
  RightPart: Integer;
  RightPosition: Integer;
begin
  Result := 0;
  LeftPosition := 1;
  RightPosition := 1;

  for Index := 1 to 4 do
  begin
    LeftPart := NextVersionPart(Left, LeftPosition);
    RightPart := NextVersionPart(Right, RightPosition);

    if LeftPart < RightPart then
    begin
      Result := -1;
      Exit;
    end;

    if LeftPart > RightPart then
    begin
      Result := 1;
      Exit;
    end;
  end;
end;

function GetInstalledVersion(var Version: String): Boolean;
begin
  Result := RegQueryStringValue(HKLM64, UninstallKey, 'DisplayVersion', Version);
  if not Result then
    Result := RegQueryStringValue(HKCU, UninstallKey, 'DisplayVersion', Version);
end;

function InitializeSetup(): Boolean;
begin
  Result := True;
  if not IsWin64 then
  begin
    MsgBox('Overseer requires a 64-bit version of Windows.', mbError, MB_OK);
    Result := False;
    Exit;
  end;

  UpgradeDetected := GetInstalledVersion(InstalledVersion) and
    (CompareVersions(InstalledVersion, '{#MyAppVersion}') < 0);

  if GetInstalledVersion(InstalledVersion) and
    (CompareVersions(InstalledVersion, '{#MyAppVersion}') > 0) then
  begin
    MsgBox(Format(ExpandConstant('{cm:DowngradeMessage}'), [InstalledVersion, '{#MyAppVersion}']), mbError, MB_OK);
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
  if UpgradeDetected then
  begin
    WizardForm.WelcomeLabel1.Caption := ExpandConstant('{cm:UpgradeTitle}');
    WizardForm.WelcomeLabel2.Caption := ExpandConstant('{cm:UpgradeText}');
  end
  else
  begin
    WizardForm.WelcomeLabel1.Caption := ExpandConstant('{cm:WelcomeTitle}');
    WizardForm.WelcomeLabel2.Caption := ExpandConstant('{cm:WelcomeText}');
  end;

  WizardForm.WelcomeLabel1.Font.Color := $000000C8;
  WizardForm.WelcomeLabel1.Font.Style := [fsBold];
  WizardForm.WelcomeLabel2.Font.Color := $00202020;
end;
