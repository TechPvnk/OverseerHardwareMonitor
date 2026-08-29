# Overseer Hardware Monitor

Overseer is a Windows hardware-monitoring application built with WPF and .NET 8. It brings live telemetry, storage health, and essential system details into a compact TechPvnk interface, including a dockable game-friendly sidebar.

## Features

- Live CPU, GPU, RAM, drive-temperature, and network monitoring with one-second history charts.
- Temperature availability and Normal, High, and Critical status handling for CPU, GPU, and storage.
- Disk Health with expandable SMART data for ATA and NVMe devices, backed by `smartctl` JSON when available.
- System Info for CPU, RAM, graphics, audio, battery, displays, active network adapter details, and GPU driver version.
- A compact Sidebar Mode with FPS, CPU, GPU, RAM, drive, and network modules.
- Sidebar docking on the top, bottom, left, or right edge of any connected monitor; module visibility, transparency, click-through, selected drive, and position persist between launches.
- Optional PresentMon-based FPS monitoring. It runs only while the FPS sidebar module is visible.
- English and Spanish UI localization, with the selected language persisted.
- Celsius/Fahrenheit switching, alert-sound controls, copy/export commands, screenshots, an application log, and minimize-to-tray support.

## Quick Start

1. Run `Overseer.exe` as administrator when prompted. Some sensors and drive-health information require elevated access.
2. Review live values in **Temps**, detailed storage data in **Disk Health**, and static machine information in **System Info**.
3. Open **View > Sidebar Mode** for the compact overlay. Its `...` menu controls visible modules and range rows. The footer controls transparency and dock edge.
4. Use **Tools > Reset Statistics** or `F5` to reset session minimum/maximum values and history charts. Use **Tools > Refresh System Information** after hardware, display, or network changes.

See [docs/USER-GUIDE.md](docs/USER-GUIDE.md) for feature details and [docs/TROUBLESHOOTING.md](docs/TROUBLESHOOTING.md) for availability and diagnostics guidance.

## Data Providers and Availability

Overseer combines several providers rather than treating a missing sensor as a failure:

- **LibreHardwareMonitorLib** supplies real-time telemetry where hardware and drivers expose it.
- **Windows APIs and WMI** supply static system, display, battery, adapter, and driver data.
- **Smartmontools / smartctl** is invoked as a separate executable for detailed SMART reports. LibreHardwareMonitor drive summaries remain available as a fallback.
- **PresentMon** is invoked as a separate executable for optional FPS monitoring.

Hardware vendors do not expose every field on every system. `N/A`, `Unknown`, or `—` indicate unavailable, unsupported, or still-initializing data; they never mean a measured zero. For example, integrated graphics can have incomplete thermal/power sensors, USB storage bridges may not forward SMART data, and devices without a battery are reported as not present.

## Requirements

- Windows 10 or Windows 11
- .NET 8 Desktop Runtime for framework-dependent builds
- Windows x64 for the provided publish profile
- Administrator rights recommended for the broadest hardware and SMART access

## Build

Open the solution in Visual Studio, or run:

```powershell
dotnet build "Overseer.slnx"
```

## Publish

Publish the x64 framework-dependent build from the project directory:

```powershell
dotnet publish Overseer.csproj -p:PublishProfile=win-x64
```

Output:

```text
bin\Release\net8.0-windows\win-x64\publish
```

The published folder includes the PawnIO helper, bundled Smartmontools files, PresentMon, third-party notices, and localized resources. Keep the publish folder intact; the external helper executables are loaded from it at runtime.

## Release Smoke Test

- Verify that Temps, Disk Health, and System Info load without errors.
- Verify CPU/GPU/RAM readings are plausible and RAM used plus available approximately matches installed memory.
- Confirm Disk Health either provides SMART details or communicates an explicit unavailable state.
- Reset statistics and verify CPU, GPU, RAM, and each drive history/ranges reset.
- Refresh System Info after changing a display or network connection.
- Verify file exports, copy commands, language switching, About, Open Log, tray behavior, and Sidebar Mode.
- In Sidebar Mode, verify module visibility, docking, click-through, transparency, drive selection, and FPS behavior when PresentMon is available.

## Contributing

Contributions are welcome. Please keep changes focused, include a concise description, and add or update verification where practical. Discuss major architecture or provider changes before starting.

## Licensing and Third-Party Components

Overseer source is licensed under MPL 2.0; see [LICENSE](LICENSE). Smartmontools is a separate GPL-2.0-or-later executable and PresentMon is a separate MIT-licensed executable. Overseer does not link either component's source code. See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) and the bundled component documentation for details.

The Overseer name, TechPvnk branding, logos, artwork, icons, and other visual assets are not covered by the MPL 2.0 unless explicitly stated. Copyright © 2026 Alfredo Capella. All rights reserved.

## Support and Contact

- Report bugs and ideas through the GitHub repository.
- Support development: https://ko-fi.com/techpvnk
- Email: techpvnk@proton.me
- TechPvnk: https://www.youtube.com/@TechPvnk
