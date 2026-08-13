# Smartmontools External Component

Overseer invokes `smartctl.exe` as a separate external process and consumes only its JSON output. It does not link against, embed, or modify Smartmontools source code.

Install the official Windows Smartmontools release files into this folder so this path exists:

`ThirdParty\Smartmontools\bin\smartctl.exe`

Keep the Smartmontools license and accompanying files distributed with the official package in this directory. Smartmontools is a GPL-2.0-or-later third-party component. Its source and release information are available at https://www.smartmontools.org/.

Overseer uses `smartctl --scan-open --json=o` and `smartctl -a --json=o` only. The integration tolerates unsupported USB bridges, unavailable permissions, command failures, timeouts, and malformed JSON by preserving LibreHardwareMonitor summary values.
