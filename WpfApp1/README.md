Overseer - Hardware Monitor (WPF)
=================================

Overseer is a Windows desktop hardware monitoring application built with WPF and .NET 8. It reads system sensors (CPU/GPU temps, usage, disk health, etc.) and provides export and tray features.

Overseer is focused on delivering real-time system information through a clean, immersive cyberpunk interface.
Rather than overwhelming users with endless tables of sensors, Overseer aims to make monitoring your PC both informative and enjoyable, but most of all, straightforward. The core emphasis is both usability and performance.
I recondition and resell laptops. This app was born because I got tired of downloading and installing different apps each time I had to diagnose PCs. 
It was a waste of time to install, run and then dive into the apps for different readings when I already knew what I wanted to see and also knew this could all be seen in a single view.
I hope it's of help for you too. Yes this is heavily ai assisted coding and I'm so grateful I can do things like this now. I do know basic coding. It works.
There's still much to do but I decided not to be a perfectionist and just iterate along the way!

-----

✨ Features
📊 Real-time CPU, GPU, RAM and disk monitoring
🌡️ Temperature, utilization and power sensors
✈️ Exportss
📈 Interactive performance graphs
😎 cyberpunk-inspired interface
⚡ Lightweight and responsive
🖥️ Built with WPF and .NET
🔌 Extensible architecture for future plugins
🌙 Customizable themes (planned)

-----

🤝 Contributing

Contributions are welcome!

Whether you'd like to:

fix bugs
improve performance
add hardware support
improve the UI
translate the application
improve documentation

feel free to open an Issue or submit a Pull Request. Keep changes small and include a brief description and a simple test if possible.

Please discuss major changes before beginning implementation.
-----

🎨 Branding

The Overseer name, TechPvnk branding, logos, artwork, icons, mascot, and other visual assets are not covered by the MPL 2.0 unless explicitly stated.

These assets remain Copyright © 2026 Alfredo Capella. All rights reserved.

-----

❤️ Support the Project

If you enjoy Overseer and would like to support its development, consider:

⭐ Starring this repository
🐛 Reporting bugs
💡 Suggesting new features
🤝 Contributing code
☕ Supporting development via Ko-fi https://ko-fi.com/techpvnk

Every contribution—large or small—helps make Overseer better.

🌐 About

Overseer is developed by TechPvnk, a project dedicated to giving technology a second life through restoration, repair, open-source software, and a passion for PC hardware.
If you enjoy restoring, tinkering, Linux, hardware modding, and building unique tools, you're in the right place.

https://www.youtube.com/@TechPvnk
https://www.instagram.com/techpvnk_/
https://www.tiktok.com/@techpvnk_
https://x.com/TechPvnk 

Status
------
- Project targets: .NET 8 (net8.0-windows)
- Uses the LibreHardwareMonitor NuGet package for sensor readings

Requirements
------------
- Windows 10/11
- .NET 8 runtime

Building
--------
- Open the solution in Visual Studio and build.
- Or from the repository root using the CLI:
  - dotnet build "Overseer.slnx"

Notes
-----
- The project uses the LibreHardwareMonitor NuGet package. NuGet restore will download the package during build.
- User-specific files (*.csproj.user) are ignored and should not be committed.

Release Package
---------------
- Version: 1.0.0
- Target: Windows x64 with the .NET 8 Desktop Runtime installed.
- Publish from the project directory:
  - dotnet publish Overseer.csproj -p:PublishProfile=win-x64
- The publish output is written to:
  - bin\Release\net8.0-windows\win-x64\publish
- The published folder includes the PawnIO helper, the separately distributed Smartmontools executable and notices, the project license, third-party notices, and English/Spanish resources.

Release Smoke Test
------------------
- Start Overseer and verify that the Temps, Disk Health, and System Info tabs load.
- Verify CPU/GPU/RAM values are plausible and RAM used plus available approximately matches installed memory.
- Open Disk Health and confirm SMART data either loads or shows an explicit unavailable state.
- Use Tools > Reset Statistics and confirm CPU, GPU, RAM, and every drive chart/minimum/maximum reset.
- Use Tools > Refresh System Information after changing a display connection.
- Verify File export, Edit copy commands, English/Spanish switching, About, and Tools > Open Log.

License
-------
This project is licensed under the MPL 2.0 License. See LICENSE for details.

Author
------
Alfredo Capella (TechPvnk)
Venezuelan in Panama

Contact
-------
techpvnk@proton.me

V 0.1:
- Some menu functions missing:
	-Language (English for now)
	-Help links (only about works)
	-Open Log
- Menu still uses windows so colors need fix

Coming Soon:
- All of that plus:
	-Sidebar
	-Themes
	-An actual website I guess
