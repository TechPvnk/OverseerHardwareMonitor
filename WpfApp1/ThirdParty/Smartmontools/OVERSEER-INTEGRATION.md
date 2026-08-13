# Overseer Smartmontools Integration

This directory contains the unmodified official Smartmontools 7.5 Windows distribution obtained from the Smartmontools SourceForge release.

Overseer invokes `bin\smartctl.exe` as an external process and parses JSON produced by `--scan-open --json=o` and `-a --json=o`. It does not link Smartmontools libraries or source code into Overseer.

Smartmontools is licensed under GPL-2.0-or-later. Its accompanying `doc\COPYING.txt`, documentation, and support files are included in this third-party directory. Source and release information: https://www.smartmontools.org/.
