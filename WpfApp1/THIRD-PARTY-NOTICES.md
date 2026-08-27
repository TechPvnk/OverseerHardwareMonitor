# Third-Party Notices

Overseer includes or invokes the following third-party components:

- LibreHardwareMonitorLib 0.9.6, used through its NuGet package for hardware telemetry.
- Smartmontools / smartctl, distributed as a separate executable under ThirdParty\Smartmontools.
  Smartmontools is GPL-2.0-or-later. Overseer does not link against Smartmontools code;
  it invokes smartctl.exe and consumes JSON output. The accompanying Smartmontools license
  and documentation are shipped in that directory.
- PresentMon 2.5.1, distributed as a separate x64 console executable under
  ThirdParty\PresentMon. PresentMon is licensed under the MIT License. Overseer does not
  link PresentMon source; it starts the executable as a child process and consumes its
  documented CSV output. The accompanying license is shipped in that directory.
- PawnIO setup helper, distributed under helpers. Its installer and verification metadata
  remain with the helper distribution.

The Overseer name, TechPvnk name, logos, artwork, icons, mascot, and branding are not
third-party components and are not covered by the project source license.
