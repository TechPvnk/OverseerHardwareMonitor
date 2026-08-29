# Troubleshooting

## Readings Show N/A, Unknown, or —

These values indicate that a provider did not expose a reading, not that Overseer measured zero. Common examples include CPU package power on unsupported platforms, integrated-GPU thermal sensors, DIMM temperatures, battery fields on desktops, and SMART data behind some USB bridges.

Run Overseer as administrator first. If the reading remains unavailable, confirm it is exposed by the device firmware and driver. Avoid comparing static device specifications with live sensor values; they are different kinds of data.

## Disk Health Details Are Missing

Detailed SMART data needs `ThirdParty\Smartmontools\bin\smartctl.exe` in the application folder and enough permission to query the drive. Overseer uses smartctl JSON, caches detailed reports, and does not run it in the one-second telemetry loop.

Reasons detailed data may be unavailable include USB-to-SATA/NVMe bridge limitations, RAID/controller abstraction, permission denial, an unsupported device, a timeout, or malformed tool output. The Disk Health summary remains the fallback. See [Smartmontools integration notes](../ThirdParty/Smartmontools/README.md).

## FPS Does Not Appear

FPS requires the bundled `ThirdParty\PresentMon\PresentMon.exe` and a compatible active 3D application. The FPS module starts PresentMon only when it is visible, and stops it when hidden or when Sidebar Mode closes. Brief `No new frames` messages can occur when a game stops presenting frames; the last valid sample is retained briefly before Overseer marks it unavailable.

## Network or Display Information Is Stale

Use **Tools > Refresh System Information** after plugging or unplugging a monitor, switching adapters, or changing a network connection. Network details use the currently active adapter; Windows may omit a distinct maximum-link-speed capability, in which case Overseer displays the active negotiated link speed as the available fallback.

## App Log and Diagnostics

Use **Tools > Open Log** to open the current application log. The log records startup failures, WMI/provider errors, smartctl availability problems, and PresentMon failures. Enable **Debug > Log WMI queries** when diagnosing a machine-specific Windows Management Instrumentation issue, reproduce the problem, then check the log.

When reporting a problem, include:

- Overseer version and Windows version.
- Hardware model, CPU, GPU, and storage connection type where relevant.
- Whether Overseer was run as administrator.
- The affected tab or Sidebar module.
- Relevant log lines, with personal information such as serial numbers, MAC addresses, and IP addresses removed.

