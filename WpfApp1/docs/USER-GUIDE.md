# Overseer User Guide

## Main Views

### Temps

The live-monitoring view shows CPU, GPU, RAM, and drive temperatures. CPU and GPU cards include temperature, utilization, power where available, session minimum/maximum values, and one-second history charts. RAM shows installed, used, and available memory with utilization history. Drive cards show each available drive temperature and history.

Temperature states are shared by the monitoring and presentation layers:

- **Normal**: operating within the normal threshold.
- **High**: elevated temperature.
- **Critical**: temperature has crossed the critical threshold.
- **Unavailable**: no sensor value is available; this is neutral, not a warning.

### Disk Health

Each drive begins as a compact summary. Use its expand button for detailed health data. When `smartctl` is available and the drive supports it, Overseer presents identity metadata and ATA or NVMe SMART attributes in a compact table. Unsupported drives, USB bridges, denied access, and unavailable smartctl data keep the basic drive summary rather than reporting a false failure.

### System Info

System Info combines static Windows data with hardware metadata. It includes CPU, installed memory, GPU, motherboard, BIOS, operating system, battery, graphics/display, audio, and active network details.

Network addresses are masked by default. Hover over an IPv4 address, IPv6 address, or MAC address to reveal a selectable value that can be copied. Use **Tools > Refresh System Information** after connecting or removing displays, adapters, or other hardware.

## Menus and Shortcuts

| Command | Result |
| --- | --- |
| `Ctrl+S` | Export monitoring data as TXT or CSV. |
| `Ctrl+Shift+S` | Export a screenshot. |
| `Ctrl+Shift+C` | Copy the current tab. |
| `Ctrl+Alt+C` | Copy all tabs. |
| `F5` | Reset live statistics and chart history. |
| **Tools > Refresh System Information** | Re-read static system, display, and network information. |
| **Tools > Open Log** | Open the Overseer application log. |
| **View > Always On Top** | Keep the main window over other windows. |
| **View > Minimize to tray** | Keep Overseer running from the notification area when minimized. |

Temperature units and language are selected from **View**. English is the default language and the chosen language persists.

## Sidebar Mode

Open Sidebar Mode from **View > Sidebar Mode**. Opening either entry control reuses one shared sidebar window.

The sidebar can display FPS, CPU, GPU, RAM, drives, and network activity. Use the header `...` menu to show or hide modules and show or hide range rows. The drive card `...` menu selects the drive used by the card. Settings persist across launches.

Use the footer controls to:

- Adjust background opacity from 40% to 100%.
- Dock to the top, bottom, left, or right edge of the current monitor.
- Toggle click-through mode. The overlay body forwards pointer input to the underlying application while the header controls remain usable.

Top and bottom docking use a horizontal compact layout. Left and right docking use a vertical layout. Sidebar placement is stored per monitor and falls back safely when a monitor is no longer connected.

### FPS

FPS uses the bundled PresentMon executable only when the FPS module is visible. It automatically follows an active 3D application, retains a short valid sample during brief frame-data gaps, and resets with `F5`. If no compatible application is active, the card displays `—` and a clear unavailable message.

## Alerts

Alert sound is available from the Tools menu and is enabled by default. Overseer uses cooldowns and re-arm thresholds to avoid repeated chimes from short temperature bursts. Critical defaults are CPU 95 C, GPU 90 C, and storage 70 C; drive health events can also trigger alerts.
