# Building the Overseer Installer

The installer uses Inno Setup 6 and packages the self-contained x64 publish output.

## Prerequisites

- Inno Setup 6: https://jrsoftware.org/isinfo.php
- .NET 8 SDK to publish Overseer

## Build

From the project directory, create the publish folder:

```powershell
dotnet publish Overseer.csproj -p:PublishProfile=win-x64
```

Then compile `installer\Overseer.iss` with the Inno Setup Compiler (`ISCC.exe`). Winget commonly installs it here:

```powershell
& "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe" installer\Overseer.iss
```

Some installations instead use `C:\Program Files (x86)\Inno Setup 6\ISCC.exe`.

The installer is written to `dist\Overseer-Setup-1.0.0-x64.exe`.

## Installer Behavior

- Installs the self-contained x64 application under Program Files by default.
- Creates Start Menu entries and offers an optional desktop shortcut.
- Registers a standard Windows uninstaller.
- Installs the bundled PawnIO helper silently when the PawnIO service is not already installed; this enables the widest available CPU sensor coverage on first launch.
- Preserves the required external Smartmontools and PresentMon executables as files beside the application.
- Runs with administrator privileges, matching Overseer's hardware-access requirements.
- Is not code-signed by this build process. Sign the generated setup executable with a trusted code-signing certificate before public distribution to avoid Windows SmartScreen reputation warnings.
