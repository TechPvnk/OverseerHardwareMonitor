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
            float? gpuUsage = FindSensorValue(gpu, SensorType.Load, "Core")
                ?? FindSensorValue(gpu, SensorType.Load, "GPU Core")
                ?? FindFirstValue(gpu, SensorType.Load);
            float? gpuPower = FindSensorValue(gpu, SensorType.Power, "Package")
                ?? FindSensorValue(gpu, SensorType.Power, "GPU")
                ?? FindFirstValue(gpu, SensorType.Power);

            StorageHealthSnapshot[] storage = _computer.Hardware
                .Where(h => h.HardwareType == HardwareType.Storage)
                .Select(CreateStorageSnapshot)
                .ToArray();

            return new HardwareSnapshot(
                        FormatName(cpu?.Name, "Unknown CPU"),
                        FormatName(gpu?.Name, "Unknown GPU"),
                        _systemInfo?.RamInfo ?? "Unknown",
                        _systemInfo?.Motherboard ?? "Unknown",
                        _systemInfo?.Bios ?? "Unknown",
                        _systemInfo?.OsVersion ?? "Unknown",
                        FormatTemperature(cpuTemp),
                        cpuTemp,
                        FormatPercent(cpuUsage),
                        cpuUsage,
                        FormatWatts(cpuPower),
                        cpuPower,
                        FormatClock(cpuClock, _systemInfo?.CpuClock ?? "Unknown"),
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
                return (temp / 10f) - 273.15f;
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
            FindStorageCounter(storage, "Read"),
            FindStorageCounter(storage, "Write"),
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
        return value.HasValue
            ? $"{value.Value.ToString("0.#", CultureInfo.InvariantCulture)} C"
            : "N/A";
    }

    private static string FormatPercent(float? value)
    {
        return value.HasValue
            ? $"{value.Value.ToString("0.#", CultureInfo.InvariantCulture)}%"
            : "N/A";
    }

    private static string FormatWatts(float? value)
    {
        return value.HasValue
            ? $"{value.Value.ToString("0.#", CultureInfo.InvariantCulture)} W"
            : "N/A";
    }

    private static string FormatClock(float? value, string fallback)
    {
        return value.HasValue && value.Value > 0
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
    string Motherboard,
    string Bios,
    string OsVersion,
    string CpuTemperature,
    float? CpuTemperatureValue,
    string CpuUsage,
    float? CpuUsageValue,
    string CpuPower,
    float? CpuPowerValue,
    string CpuClock,
    string GpuTemperature,
    float? GpuTemperatureValue,
    string GpuUsage,
    float? GpuUsageValue,
    string GpuPower,
    float? GpuPowerValue,
    IReadOnlyList<StorageHealthSnapshot> StorageDrives)
{
    public static HardwareSnapshot Empty { get; } = new(
        "Unknown CPU",
        "Unknown GPU",
        "Unknown",
        "Unknown",
        "Unknown",
        "Unknown",
        "N/A",
        null,
        "N/A",
        null,
        "N/A",
        null,
        "Unknown",
        "N/A",
        null,
        "N/A",
        null,
        "N/A",
        null,
        []);
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