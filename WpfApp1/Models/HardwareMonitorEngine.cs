using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using LibreHardwareMonitor.Hardware;
using System.Management;
using System.IO;
using Overseer.Helpers;

namespace Overseer.Models;

public sealed class HardwareMonitorEngine : IDisposable
{
    private readonly Computer _computer;
    private readonly UpdateVisitor _updateVisitor = new();
    private readonly WindowsSystemInfo _windowsSystemInfo = new();
    private SystemInfoSnapshot? _systemInfo;
    private bool _disposed;
    private bool _isOpen;
    private bool _isInstallingPawnIo; // Guard flag to prevent duplicate installer triggers

    public HardwareMonitorEngine()
    {
        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMemoryEnabled = true,
            IsMotherboardEnabled = true,
            IsControllerEnabled = true,
            IsStorageEnabled = true
        };

        // Initialize immediately so ring0 drivers have time to bind before the first snapshot
        Initialize();
    }

    public void Initialize()
    {
        if (_isOpen)
        {
            return;
        }

        TryRead(() =>
        {
            _computer.Open();
            _isOpen = true;

            try
            {
                WriteDebugSensorSnapshot();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to write debug sensor snapshot: {ex}");
            }

            try
            {
                // Guard clause: ensures PawnIO installer is only launched ONCE per session
                if (!_isInstallingPawnIo && !LibreHardwareMonitorInstaller.IsPawnIoInstalled())
                {
                    _isInstallingPawnIo = true; // Lock immediately
                    bool installerLaunched = LibreHardwareMonitorInstaller.TryInstallHelper();
                    if (installerLaunched)
                    {
                        try { _computer.Close(); } catch { }
                        System.Threading.Thread.Sleep(1500);
                        try { _computer.Open(); } catch (Exception ex) { Debug.WriteLine($"Failed to re-open computer after helper install: {ex}"); }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Installer attempt failed: {ex}");
            }
        }, "Unable to initialize hardware monitor.");
    }

    public HardwareSnapshot ReadSnapshot()
    {
        return TryRead(() =>
        {
            Initialize();

            IHardware? cpu = null;
            IHardware? gpu = null;
            float? cpuTemp = null;
            float? cpuUsage = null;
            float? cpuPower = null;
            float? cpuClock = null;

            // Warm-up loop: SMU and Super I/O sensors often return null on the first tick.
            for (int i = 0; i < 3; i++)
            {
                UpdateHardware();
                _systemInfo ??= _windowsSystemInfo.ReadSystemInfo();

                cpu = _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Cpu);
                gpu = _computer.Hardware.FirstOrDefault(IsGpu);

                cpuTemp = FindCpuTemperature(cpu);

                cpuUsage = FindSensorValue(cpu, SensorType.Load, "Total")
                    ?? FindSensorValue(cpu, SensorType.Load, "CPU Total")
                    ?? FindFirstValue(cpu, SensorType.Load);

                cpuPower = FindSensorValue(cpu, SensorType.Power, "Package")
                    ?? FindSensorValue(cpu, SensorType.Power, "CPU Package")
                    ?? FindFirstPositiveValue(cpu, SensorType.Power)
                    ?? FindCpuPowerFromAllHardware(cpu);

                cpuClock = FindSensorValue(cpu, SensorType.Clock, "Core")
                    ?? FindFirstValue(cpu, SensorType.Clock);

                if (cpuTemp.HasValue && cpuTemp.Value > 0 && cpuPower.HasValue)
                {
                    break;
                }

                System.Threading.Thread.Sleep(50);
            }

            // Fallback to WMI if driver fails
            if (!cpuTemp.HasValue || cpuTemp.Value == 0)
            {
                cpuTemp = GetWmiCpuTemperature();
            }

            float? gpuTemp = FindSensorValue(gpu, SensorType.Temperature, "Core")
                ?? FindFirstPositiveValue(gpu, SensorType.Temperature);

            // Prefer LibreHardwareMonitor GPU sensors for memory totals/usage when available
            string gpuRamFromSensors = "Unknown";
            string gpuMemoryTotalStr = "Unknown";
            string gpuMemoryUsedStr = "Unknown";
            string gpuMemoryFreeStr = "Unknown";
            try
            {
                var sensors = GetSensors(gpu).ToArray();
                // use name-based heuristics: look for "memory" + ("total"|"used"|"free")
                float? totalMb = sensors
                    .Where(s => s.SensorType == SensorType.SmallData && s.Name.IndexOf("memory", StringComparison.OrdinalIgnoreCase) >= 0 && s.Name.IndexOf("total", StringComparison.OrdinalIgnoreCase) >= 0)
                    .Select(s => s.Value)
                    .FirstOrDefault(v => v.HasValue);

                float? usedMb = sensors
                    .Where(s => s.SensorType == SensorType.SmallData && s.Name.IndexOf("memory", StringComparison.OrdinalIgnoreCase) >= 0 && s.Name.IndexOf("used", StringComparison.OrdinalIgnoreCase) >= 0)
                    .Select(s => s.Value)
                    .FirstOrDefault(v => v.HasValue);

                float? freeMb = sensors
                    .Where(s => s.SensorType == SensorType.SmallData && s.Name.IndexOf("memory", StringComparison.OrdinalIgnoreCase) >= 0 && (s.Name.IndexOf("free", StringComparison.OrdinalIgnoreCase) >= 0 || s.Name.IndexOf("available", StringComparison.OrdinalIgnoreCase) >= 0))
                    .Select(s => s.Value)
                    .FirstOrDefault(v => v.HasValue);

                // fallback sensors that include D3D names
                if (!totalMb.HasValue)
                {
                    totalMb = sensors.Where(s => s.SensorType == SensorType.SmallData && s.Name.IndexOf("d3d dedicated memory total", StringComparison.OrdinalIgnoreCase) >= 0)
                        .Select(s => s.Value).FirstOrDefault(v => v.HasValue);
                }
                if (!usedMb.HasValue)
                {
                    usedMb = sensors.Where(s => s.SensorType == SensorType.SmallData && s.Name.IndexOf("d3d dedicated memory used", StringComparison.OrdinalIgnoreCase) >= 0)
                        .Select(s => s.Value).FirstOrDefault(v => v.HasValue);
                }
                if (!freeMb.HasValue)
                {
                    freeMb = sensors.Where(s => s.SensorType == SensorType.SmallData && s.Name.IndexOf("d3d dedicated memory free", StringComparison.OrdinalIgnoreCase) >= 0)
                        .Select(s => s.Value).FirstOrDefault(v => v.HasValue);
                }

                if (totalMb.HasValue && totalMb.Value > 0)
                {
                    double totalGb = Math.Round(totalMb.Value / 1024d, 2);
                    gpuMemoryTotalStr = $"{totalGb:0.##} GB";
                    gpuRamFromSensors = gpuMemoryTotalStr;
                }

                if (usedMb.HasValue && usedMb.Value > 0)
                {
                    double usedGb = Math.Round(usedMb.Value / 1024d, 2);
                    gpuMemoryUsedStr = $"{usedGb:0.##} GB";
                }

                if (freeMb.HasValue && freeMb.Value > 0)
                {
                    double freeGb = Math.Round(freeMb.Value / 1024d, 2);
                    gpuMemoryFreeStr = $"{freeGb:0.##} GB";
                }
            }
            catch
            {
                // ignore sensor parsing errors
            }
            float? gpuUsage = FindSensorValue(gpu, SensorType.Load, "Core")
                ?? FindSensorValue(gpu, SensorType.Load, "GPU Core")
                ?? FindFirstValue(gpu, SensorType.Load);
            float? gpuPower = FindSensorValue(gpu, SensorType.Power, "Package")
                ?? FindSensorValue(gpu, SensorType.Power, "GPU")
                ?? FindFirstPositiveValue(gpu, SensorType.Power);

            // GPU clock (best-effort)
            float? gpuClock = FindSensorValue(gpu, SensorType.Clock, "Core")
                ?? FindFirstValue(gpu, SensorType.Clock);

            // CPU TDP fallback: if WMI TDP is unknown, show current package power as a runtime approximation
            string cpuTdpDisplay = _systemInfo?.CpuTdp ?? "Unknown";
            if (string.IsNullOrWhiteSpace(cpuTdpDisplay) || cpuTdpDisplay == "Unknown")
            {
                if (cpuPower.HasValue)
                {
                    cpuTdpDisplay = FormatWatts(cpuPower) + " (current)";
                }
            }

            StorageHealthSnapshot[] storage = _computer.Hardware
                .Where(h => h.HardwareType == HardwareType.Storage)
                .Select(CreateStorageSnapshot)
                .ToArray();

            // For GPU RAM/Bus prefer LibreHardwareMonitor sensors first, then per-GPU DXGI/WMI lookup
            string gpuRam = "Unknown";
            string gpuBus = "Unknown";
            // try LHM sensors
            try
            {
                if (!string.IsNullOrWhiteSpace(gpu?.Name))
                {
                    // gpuRamFromSensors computed above
                    gpuRam = string.IsNullOrWhiteSpace(gpuRamFromSensors) ? "Unknown" : gpuRamFromSensors;
                }
            }
            catch { }

            // If sensors didn't provide a value, try per-GPU DXGI/WMI lookup
            if (gpuRam == "Unknown")
            {
                try { gpuRam = _windowsSystemInfo.ReadGpuRamFor(FormatName(gpu?.Name, string.Empty)); } catch { }
            }

            try { gpuBus = _windowsSystemInfo.ReadGpuBusFor(FormatName(gpu?.Name, string.Empty)); } catch { }
            // Prefer RAM module info from LibreHardwareMonitor if available (shows manufacturer + part)
            var ramModulesFinal = _systemInfo?.RamModules?.ToList() ?? new System.Collections.Generic.List<string>();
            try
            {
                var lhmMemory = _computer.Hardware.Where(h => h.HardwareType == HardwareType.Memory).ToArray();
                foreach (var mem in lhmMemory)
                {
                    string name = FormatName(mem.Name, "Unknown Module");
                    // find capacity sensor (Data / Capacity) reported by LHM
                    var capSensor = GetSensors(mem).FirstOrDefault(s => s.SensorType == SensorType.Data && s.Name.IndexOf("Capacity", StringComparison.OrdinalIgnoreCase) >= 0);
                    string capStr = string.Empty;
                    if (capSensor?.Value is float v && v > 0)
                    {
                        // LHM often reports capacity in GB for these sensors
                        capStr = $": {Math.Round(v):0} GB";
                    }

                    // find configured clock if exposed on the memory hardware
                    var speedSensor = GetSensors(mem).FirstOrDefault(s => s.SensorType == SensorType.Clock && s.Name.IndexOf("Memory", StringComparison.OrdinalIgnoreCase) >= 0);
                    string speedStr = string.Empty;
                    if (speedSensor?.Value is float sv && sv > 0)
                    {
                        speedStr = $" @ {Math.Round(sv):0} MHz";
                    }

                    var entry = (name + capStr + speedStr).Trim();
                    if (!string.IsNullOrWhiteSpace(entry) && !ramModulesFinal.Contains(entry))
                    {
                        ramModulesFinal.Add(entry);
                    }
                }
            }
            catch
            {
                // ignore LHM read errors
            }

            // Filter out generic or unknown entries (e.g., "Virtual Memory", "Total Memory", or entries starting with "Unknown")
            try
            {
                ramModulesFinal = ramModulesFinal
                    .Where(e => !string.IsNullOrWhiteSpace(e))
                    .Where(e => !e.StartsWith("Unknown", StringComparison.OrdinalIgnoreCase))
                    .Where(e => !e.Equals("Virtual Memory", StringComparison.OrdinalIgnoreCase))
                    .Where(e => !e.Equals("Total Memory", StringComparison.OrdinalIgnoreCase))
                    .Select(e => e.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch
            {
                // ignore filtering errors
            }

            // Group identical modules and preserve multiplicity; prefer non-Unknown names
            try
            {
                var groups = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

                foreach (var entry in ramModulesFinal)
                {
                    string left = entry.Split(':')[0].Trim();
                    // remove parentheses suffix like "(#0)"
                    left = System.Text.RegularExpressions.Regex.Replace(left, "\\s*\\(#[0-9]+\\)", string.Empty);

                    // extract manufacturer/part and capacity key similar to before
                    string man = left;
                    string part = string.Empty;
                    if (left.Contains('-'))
                    {
                        var parts = left.Split('-', 2);
                        man = parts[0].Trim();
                        part = parts.Length > 1 ? parts[1].Trim() : string.Empty;
                    }
                    else
                    {
                        var toks = left.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                        man = toks.Length > 0 ? toks[0].Trim() : left;
                        part = toks.Length > 1 ? toks[1].Trim() : string.Empty;
                    }

                    var capMatch = System.Text.RegularExpressions.Regex.Match(entry, "(\\d+)\\s*GB", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    string cap = capMatch.Success ? capMatch.Groups[1].Value : "0";

                    string key = $"{man.ToLowerInvariant()}|{part.ToLowerInvariant()}|{cap}";

                    if (!groups.TryGetValue(key, out var list))
                    {
                        list = new List<string>();
                        groups[key] = list;
                    }
                    list.Add(entry);
                }

                var finalList = new List<string>();
                foreach (var kv in groups)
                {
                    var list = kv.Value;
                    if (list.Count == 1)
                    {
                        finalList.Add(list[0]);
                    }
                    else
                    {
                        // emit each with index suffix to preserve multiplicity
                        for (int i = 0; i < list.Count; i++)
                        {
                            // append (#{i}) if not already present
                            string baseEntry = list[i];
                            if (!System.Text.RegularExpressions.Regex.IsMatch(baseEntry, "\\(#[0-9]+\\)"))
                            {
                                // insert suffix before any trailing parts (preserve ": 16 GB @ 3600 MHz")
                                var namePart = baseEntry.Split(':')[0].Trim();
                                var rest = baseEntry.Substring(namePart.Length);
                                finalList.Add($"{namePart} (#{i}){rest}");
                            }
                            else
                            {
                                finalList.Add(baseEntry);
                            }
                        }
                    }
                }

                ramModulesFinal = finalList;
            }
            catch
            {
                // ignore dedupe errors
            }

            // Motherboard sub-hardware (from LibreHardwareMonitor)
            IReadOnlyList<string> motherboardSubs = Array.Empty<string>();
            try
            {
                var mb = _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Motherboard);
                if (mb != null)
                {
                    motherboardSubs = mb.SubHardware
                        .Select(sh => $"{sh.Name} | Type: {sh.HardwareType}")
                        .Where(s => !string.IsNullOrWhiteSpace(s))
                        .ToArray();
                }
            }
            catch
            {
                motherboardSubs = Array.Empty<string>();
            }

            string batteryInfo = _systemInfo?.BatteryInfo ?? "Not present";
            return new HardwareSnapshot(
                        FormatName(cpu?.Name, "Unknown CPU"),
                        FormatName(gpu?.Name, "Unknown GPU"),
                        _systemInfo?.RamInfo ?? "Unknown",
                        _systemInfo?.RamTotal ?? "Unknown",
                        _systemInfo?.RamType ?? "Unknown",
                        _systemInfo?.RamClock ?? "Unknown",
                        ramModulesFinal ?? (_systemInfo?.RamModules ?? Array.Empty<string>()),
                        _systemInfo?.Motherboard ?? "Unknown",
                        _systemInfo?.Bios ?? "Unknown",
                        _systemInfo?.OsVersion ?? "Unknown",
                        motherboardSubs,
                        batteryInfo,
                        FormatTemperature(cpuTemp),
                        cpuTemp,
                        FormatPercent(cpuUsage),
                        cpuUsage,
                        FormatWatts(cpuPower),
                        cpuPower,
                        FormatClock(cpuClock, _systemInfo?.CpuClock ?? "Unknown"),
                        _systemInfo?.CpuCoresThreads ?? "Unknown",
                        _systemInfo?.CpuCaches ?? "Unknown",
                        cpuTdpDisplay,
                        FormatClock(gpuClock, "Unknown"),
                        gpuRam,
                        gpuBus,
                        gpuMemoryTotalStr,
                        gpuMemoryUsedStr,
                        gpuMemoryFreeStr,
                        FormatTemperature(gpuTemp),
                        gpuTemp,
                        FormatPercent(gpuUsage),
                        gpuUsage,
                        FormatWatts(gpuPower),
                        gpuPower,
                        storage);
        }, HardwareSnapshot.Empty);
    }

    public void WriteDebugSensorSnapshot()
    {
        Debug.WriteLine("\n=================== MINIMAL HARDWARE MONITOR SENSOR DUMP ===================");

        foreach (IHardware hardware in _computer.Hardware)
        {
            Debug.WriteLine($"\n[HARDWARE NODE] Name: '{hardware.Name}' | Type: {hardware.HardwareType}");

            DumpSensors(hardware, 1);

            foreach (IHardware subHardware in hardware.SubHardware)
            {
                Debug.WriteLine($"  [SUB-HARDWARE] Name: '{subHardware.Name}' | Type: {subHardware.HardwareType}");
                DumpSensors(subHardware, 2);
            }
        }

        // DXGI adapter dump (best-effort)
        try
        {
            var adapters = DxgiNative.EnumerateAdapters();
            if (adapters != null && adapters.Count > 0)
            {
                Debug.WriteLine("\n=================== DXGI ADAPTER DUMP ===================");
                int idx = 0;
                foreach (var a in adapters)
                {
                    Debug.WriteLine($"[DXGI Adapter #{idx}] Description: {a.Description}");
                    Debug.WriteLine($"  VendorId=0x{a.VendorId:X4} DeviceId=0x{a.DeviceId:X4} SubSysId=0x{a.SubSysId:X4} Revision={a.Revision}");
                    Debug.WriteLine($"  DedicatedVideoMemory={a.DedicatedVideoMemory} bytes DedicatedSystemMemory={a.DedicatedSystemMemory} bytes SharedSystemMemory={a.SharedSystemMemory} bytes AdapterLuid={a.AdapterLuid}");
                    if (a.LocalMemory is not null)
                    {
                        Debug.WriteLine($"  [Local VRAM] Budget={a.LocalMemory.Budget} CurrentUsage={a.LocalMemory.CurrentUsage} AvailableForReservation={a.LocalMemory.AvailableForReservation} CurrentReservation={a.LocalMemory.CurrentReservation}");
                    }
                    if (a.NonLocalMemory is not null)
                    {
                        Debug.WriteLine($"  [NonLocal System] Budget={a.NonLocalMemory.Budget} CurrentUsage={a.NonLocalMemory.CurrentUsage}");
                    }
                    idx++;
                }
                Debug.WriteLine("=========================================================");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"DXGI adapter dump failed: {ex}");
        }

        Debug.WriteLine("============================================================================\n");
    }

    private static float? GetWmiCpuTemperature()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT * FROM MSAcpi_ThermalZoneTemperature");
            foreach (ManagementBaseObject obj in searcher.Get())
            {
                uint temp = Convert.ToUInt32(obj["CurrentTemperature"], CultureInfo.InvariantCulture);
                float celsius = (temp / 10f) - 273.15f;
                if (celsius is > 0f and < 125f)
                {
                    return celsius;
                }
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    private static void DumpSensors(IHardware hardware, int indentLevel)
    {
        string indent = new(' ', indentLevel * 2);
        foreach (ISensor sensor in hardware.Sensors)
        {
            Debug.WriteLine($"{indent}--> Sensor: '{sensor.Name}' | Type: {sensor.SensorType,-12} | Value: {sensor.Value}");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        TryRead(() => _computer.Close(), "Unable to close hardware monitor.");
        _disposed = true;
    }

    private StorageHealthSnapshot CreateStorageSnapshot(IHardware storage)
    {
        float? temperature = FindFirstPositiveValue(storage, SensorType.Temperature);
        float? lifeRemaining = FindSensorValue(storage, SensorType.Level, "Remaining Life")
            ?? FindSensorValue(storage, SensorType.Level, "Life")
            ?? FindSensorValue(storage, SensorType.Level, "Health");

        bool hasWarnings = HasWarningSensors(storage);
        bool hasCriticalFailure = HasExplicitCriticalFailure(storage);

        return new StorageHealthSnapshot(
            FormatName(storage.Name, "Unknown Drive"),
            DetermineStorageHealth(lifeRemaining, hasWarnings, hasCriticalFailure),
            FormatPercent(lifeRemaining),
            FormatTemperature(temperature),
            temperature,
            FindStorageDataCounter(storage, true),
            FindStorageDataCounter(storage, false),
            FindStorageCounter(storage, "Power On Count"),
            FindStorageCounter(storage, "Power On Hours"),
            GetStorageInterfaceType(storage),
            hasWarnings || hasCriticalFailure ? "Warnings Detected" : "No Errors");
    }

    private void UpdateHardware()
    {
        _computer.Accept(_updateVisitor);
        _systemInfo ??= _windowsSystemInfo.ReadSystemInfo();
    }

    private static bool IsGpu(IHardware hardware)
    {
        return hardware.HardwareType is HardwareType.GpuAmd or HardwareType.GpuIntel or HardwareType.GpuNvidia;
    }

    private float? FindCpuTemperature(IHardware? cpu)
    {
        return GetSensorValue(cpu, SensorType.Temperature, "Core (Tctl/Tdie)")
            ?? GetSensorValue(cpu, SensorType.Temperature, "Core (Tdie)")
            ?? GetSensorValue(cpu, SensorType.Temperature, "Core (Tctl)")
            ?? GetSensorValue(cpu, SensorType.Temperature, "CPU Package")
            ?? GetSensorValue(cpu, SensorType.Temperature, "CPU Core")
            ?? GetSensorValue(cpu, SensorType.Temperature, "Core Max")
            ?? GetSensorValue(cpu, SensorType.Temperature)
            ?? FindCpuTemperatureFromAllHardware(cpu);
    }

    private static float? GetSensorValue(IHardware? hardware, SensorType sensorType, string? sensorName = null)
    {
        if (hardware == null) return null;

        ISensor? sensor;
        if (sensorName != null)
        {
            sensor = hardware.Sensors.FirstOrDefault(s =>
                s.SensorType == sensorType &&
                s.Name.Equals(sensorName, StringComparison.OrdinalIgnoreCase));

            sensor ??= hardware.Sensors.FirstOrDefault(s =>
                s.SensorType == sensorType &&
                s.Name.Contains(sensorName, StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            sensor = hardware.Sensors.FirstOrDefault(s => s.SensorType == sensorType);
        }

        if (sensor != null && sensor.Value.HasValue)
        {
            return sensor.Value.Value;
        }

        foreach (IHardware subHardware in hardware.SubHardware)
        {
            float? subValue = GetSensorValue(subHardware, sensorType, sensorName);
            if (subValue.HasValue) return subValue;
        }

        return null;
    }

    private static IEnumerable<ISensor> GetSensors(IHardware? hardware)
    {
        return hardware is null
            ? Enumerable.Empty<ISensor>()
            : hardware.Sensors.Concat(hardware.SubHardware.SelectMany(GetSensors));
    }

    private IEnumerable<ISensor> GetAllSensors()
    {
        return _computer.Hardware.SelectMany(GetSensors);
    }

    private float? FindCpuTemperatureFromAllHardware(IHardware? cpu)
    {
        string cpuName = cpu?.Name ?? string.Empty;

        return GetAllSensors()
            .Where(sensor => sensor.SensorType == SensorType.Temperature)
            .Where(sensor => sensor.Value.GetValueOrDefault() > 0)
            .Where(sensor =>
                sensor.Hardware.HardwareType is not (HardwareType.Storage or HardwareType.GpuAmd or HardwareType.GpuIntel or HardwareType.GpuNvidia)
                && (sensor.Hardware.HardwareType == HardwareType.Cpu
                || sensor.Name.Contains("CPU", StringComparison.OrdinalIgnoreCase)
                || sensor.Name.Contains("Tctl", StringComparison.OrdinalIgnoreCase)
                || sensor.Name.Contains("Tdie", StringComparison.OrdinalIgnoreCase)
                || sensor.Name.Contains("CCD", StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrWhiteSpace(cpuName) && sensor.Name.Contains(cpuName, StringComparison.OrdinalIgnoreCase))))
            .Select(sensor => sensor.Value)
            .FirstOrDefault(value => value.HasValue)
            ?? GetAllSensors()
                .Where(sensor => sensor.SensorType == SensorType.Temperature)
                .Where(sensor => sensor.Value.GetValueOrDefault() > 0)
                .Where(sensor => sensor.Hardware.HardwareType is not (HardwareType.Storage or HardwareType.GpuAmd or HardwareType.GpuIntel or HardwareType.GpuNvidia))
                .Select(sensor => sensor.Value)
                .FirstOrDefault(value => value.HasValue);
    }

    private float? FindCpuPowerFromAllHardware(IHardware? cpu)
    {
        string cpuName = cpu?.Name ?? string.Empty;

        return GetAllSensors()
            .Where(sensor => sensor.SensorType == SensorType.Power)
            .Where(sensor => sensor.Value.GetValueOrDefault() > 0)
            .Where(sensor =>
                sensor.Hardware.HardwareType is not (HardwareType.Storage or HardwareType.GpuAmd or HardwareType.GpuIntel or HardwareType.GpuNvidia)
                && (sensor.Hardware.HardwareType == HardwareType.Cpu
                || sensor.Name.Contains("CPU", StringComparison.OrdinalIgnoreCase)
                || sensor.Name.Contains("Package", StringComparison.OrdinalIgnoreCase)
                || sensor.Name.Contains("PPT", StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrWhiteSpace(cpuName) && sensor.Name.Contains(cpuName, StringComparison.OrdinalIgnoreCase))))
            .Select(sensor => sensor.Value)
            .FirstOrDefault(value => value.HasValue)
            ?? GetAllSensors()
                .Where(sensor => sensor.SensorType == SensorType.Power)
                .Where(sensor => sensor.Value.GetValueOrDefault() > 0)
                .Where(sensor => sensor.Hardware.HardwareType is not (HardwareType.Storage or HardwareType.GpuAmd or HardwareType.GpuIntel or HardwareType.GpuNvidia))
                .Select(sensor => sensor.Value)
                .FirstOrDefault(value => value.HasValue);
    }

    private static float? FindSensorValue(IHardware? hardware, SensorType sensorType, string namePart)
    {
        return GetSensors(hardware)
            .Where(sensor => sensor.SensorType == sensorType)
            .Where(sensor => sensor.Name.Contains(namePart, StringComparison.OrdinalIgnoreCase))
            .Select(sensor => sensor.Value)
            .FirstOrDefault(value => IsValidSensorValue(sensorType, value));
    }

    private static float? FindFirstValue(IHardware? hardware, SensorType sensorType)
    {
        return GetSensors(hardware)
            .Where(sensor => sensor.SensorType == sensorType)
            .Select(sensor => sensor.Value)
            .FirstOrDefault(value => value.HasValue);
    }

    private static float? FindFirstPositiveValue(IHardware? hardware, SensorType sensorType)
    {
        return GetSensors(hardware)
            .Where(sensor => sensor.SensorType == sensorType)
            .Select(sensor => sensor.Value)
            .FirstOrDefault(value => value.HasValue && value.Value > 0);
    }

    private static bool HasWarningSensors(IHardware storage)
    {
        return GetSensors(storage)
            .Where(sensor => sensor.Value.GetValueOrDefault() > 0)
            .Any(sensor =>
                sensor.Name.Contains("Reallocated", StringComparison.OrdinalIgnoreCase)
                || sensor.Name.Contains("Uncorrectable", StringComparison.OrdinalIgnoreCase)
                || sensor.Name.Contains("Critical Warning", StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasExplicitCriticalFailure(IHardware storage)
    {
        return GetSensors(storage)
            .Where(sensor => sensor.Value.GetValueOrDefault() > 0)
            .Any(sensor =>
                sensor.Name.Equals("Bad", StringComparison.OrdinalIgnoreCase)
                || sensor.Name.Contains("Bad Sector", StringComparison.OrdinalIgnoreCase)
                || sensor.Name.Contains("Media and Data Integrity Errors", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsValidSensorValue(SensorType sensorType, float? value)
    {
        if (!value.HasValue)
        {
            return false;
        }

        return sensorType is not (SensorType.Temperature or SensorType.Power) || value.Value > 0;
    }

    private static string DetermineStorageHealth(float? lifeRemaining, bool hasWarnings, bool hasCriticalFailure)
    {
        if (hasCriticalFailure)
        {
            return "BAD";
        }

        if (hasWarnings || lifeRemaining is < 50)
        {
            return "CAUTION";
        }

        if (!lifeRemaining.HasValue)
        {
            return "Unknown";
        }

        return lifeRemaining.Value >= 90 ? "Excellent" : "GOOD";
    }

    private static string FindStorageDataCounter(IHardware storage, bool reads)
    {
        string[] preferredNames = reads
            ? ["Total Host Reads", "Host Reads", "Total Reads", "Data Units Read", "Data Read", "Read"]
            : ["Total Host Writes", "Host Writes", "NAND Writes", "Total Writes", "Data Units Written", "Data Written", "Write"];

        foreach (string preferredName in preferredNames)
        {
            float? value = GetSensors(storage)
                .Where(sensor => sensor.SensorType == SensorType.Data)
                .Where(sensor => IsStorageLifetimeDataSensor(sensor, preferredName))
                .Select(sensor => sensor.Value)
                .FirstOrDefault(v => v.HasValue);

            if (value.HasValue)
            {
                return $"{value.Value:0.#} GB";
            }
        }

        return "Unknown";
    }

    private static bool IsStorageLifetimeDataSensor(ISensor sensor, string namePart)
    {
        return sensor.Name.Contains(namePart, StringComparison.OrdinalIgnoreCase)
            && !sensor.Name.Contains("Rate", StringComparison.OrdinalIgnoreCase)
            && !sensor.Name.Contains("Activity", StringComparison.OrdinalIgnoreCase)
            && !sensor.Name.Contains("Throughput", StringComparison.OrdinalIgnoreCase)
            && !sensor.Name.Contains("Speed", StringComparison.OrdinalIgnoreCase)
            && !sensor.Name.Contains("Load", StringComparison.OrdinalIgnoreCase);
    }
    private static string FindStorageCounter(IHardware storage, string namePart)
    {
        float? value = GetSensors(storage)
            .Where(sensor => sensor.Name.Contains(namePart, StringComparison.OrdinalIgnoreCase))
            .Select(sensor => sensor.Value)
            .FirstOrDefault(v => v.HasValue);

        return value.HasValue
            ? value.Value.ToString("0.#", CultureInfo.InvariantCulture)
            : "Unknown";
    }

    private string GetStorageInterfaceType(IHardware storage)
    {
        string windowsInterface = _windowsSystemInfo.ReadStorageInterface(storage.Name);
        if (windowsInterface != "Unknown")
        {
            return windowsInterface;
        }

        string name = storage.Name;

        if (name.Contains("NVMe", StringComparison.OrdinalIgnoreCase))
        {
            return "NVMe";
        }

        if (name.Contains("USB", StringComparison.OrdinalIgnoreCase))
        {
            return "USB";
        }

        if (name.Contains("SATA", StringComparison.OrdinalIgnoreCase) || name.Contains("SSD", StringComparison.OrdinalIgnoreCase))
        {
            return "SATA";
        }

        return "Unknown";
    }

    private static string FormatTemperature(float? value)
    {
        return value.HasValue && !float.IsNaN(value.Value) && !float.IsInfinity(value.Value)
            ? $"{value.Value.ToString("0.#", CultureInfo.InvariantCulture)} C"
            : "N/A";
    }

    private static string FormatPercent(float? value)
    {
        return value.HasValue && !float.IsNaN(value.Value) && !float.IsInfinity(value.Value)
            ? $"{value.Value.ToString("0.#", CultureInfo.InvariantCulture)}%"
            : "N/A";
    }

    private static string FormatWatts(float? value)
    {
        return value.HasValue && value.Value > 0 && !float.IsNaN(value.Value) && !float.IsInfinity(value.Value)
            ? $"{value.Value.ToString("0.#", CultureInfo.InvariantCulture)} W"
            : "N/A";
    }

    private static string FormatClock(float? value, string fallback)
    {
        return value.HasValue && value.Value > 0 && !float.IsNaN(value.Value) && !float.IsInfinity(value.Value)
            ? $"{value.Value.ToString("0", CultureInfo.InvariantCulture)} MHz"
            : fallback;
    }

    private static string FormatName(string? name, string fallback)
    {
        return string.IsNullOrWhiteSpace(name) ? fallback : name.Trim();
    }

    private static T TryRead<T>(Func<T> action, T fallback)
    {
        try
        {
            return action();
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            return fallback;
        }
    }

    private static void TryRead(Action action, string debugMessage)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"{debugMessage} {ex}");
        }
    }
}

public sealed class UpdateVisitor : IVisitor
{
    public void VisitComputer(IComputer computer)
    {
        computer.Traverse(this);
    }

    public void VisitHardware(IHardware hardware)
    {
        hardware.Update();

        foreach (IHardware subHardware in hardware.SubHardware)
        {
            subHardware.Accept(this);
        }
    }

    public void VisitSensor(ISensor sensor)
    {
    }

    public void VisitParameter(IParameter parameter)
    {
    }
}

public sealed record HardwareSnapshot(
    string CpuName,
    string GpuName,
    string RamInfo,
    string RamTotal,
    string RamType,
    string RamClock,
    IReadOnlyList<string> RamModules,
    string Motherboard,
    string Bios,
    string OsVersion,
    IReadOnlyList<string> MotherboardSubHardware,
    string BatteryInfo,
    string CpuTemperature,
    float? CpuTemperatureValue,
    string CpuUsage,
    float? CpuUsageValue,
    string CpuPower,
    float? CpuPowerValue,
    string CpuClock,
    string CpuCoresThreads,
    string CpuCaches,
    string CpuTdp,
    string GpuClock,
    string GpuRam,
    string GpuBus,
    string GpuMemoryTotal,
    string GpuMemoryUsed,
    string GpuMemoryFree,
    string GpuTemperature,
    float? GpuTemperatureValue,
    string GpuUsage,
    float? GpuUsageValue,
    string GpuPower,
    float? GpuPowerValue,
    IReadOnlyList<StorageHealthSnapshot> StorageDrives)
{
    public static HardwareSnapshot Empty { get; } = new(
        "Unknown CPU",                       // CpuName
        "Unknown GPU",                       // GpuName
        "Unknown",                           // RamInfo
        "Unknown",                           // RamTotal
        "Unknown",                           // RamType
        "Unknown",                           // RamClock
        Array.Empty<string>(),                 // RamModules
        "Unknown",                           // Motherboard
        "Unknown",                           // Bios
        "Unknown",                           // OsVersion
        Array.Empty<string>(),                 // MotherboardSubHardware
        "Not present",                       // BatteryInfo
        "N/A",                               // CpuTemperature
        null,                                  // CpuTemperatureValue
        "N/A",                               // CpuUsage
        null,                                  // CpuUsageValue
        "N/A",                               // CpuPower
        null,                                  // CpuPowerValue
        "Unknown",                            // CpuClock
        "Unknown",                            // CpuCoresThreads
        "Unknown",                            // CpuCaches
        "Unknown",                            // CpuTdp
        "Unknown",                            // GpuClock
        "Unknown",                            // GpuRam
        "Unknown",                            // GpuBus
        "Unknown",                            // GpuMemoryTotal
        "Unknown",                            // GpuMemoryUsed
        "Unknown",                            // GpuMemoryFree
        "N/A",                                // GpuTemperature
        null,                                   // GpuTemperatureValue
        "N/A",                                // GpuUsage
        null,                                   // GpuUsageValue
        "N/A",                                // GpuPower
        null,                                   // GpuPowerValue
        Array.Empty<StorageHealthSnapshot>()    // StorageDrives
        );
}

public sealed record StorageHealthSnapshot(
    string Name,
    string HealthStatus,
    string LifeRemaining,
    string Temperature,
    float? TemperatureValue,
    string TotalReads,
    string TotalWrites,
    string PowerOnCount,
    string PowerOnHours,
    string InterfaceType,
    string ErrorFlag);