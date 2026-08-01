using System;
using System.Globalization;
using System.Linq;
using System.Management;

namespace Overseer.Models;

public sealed record SystemInfoSnapshot(
    string RamInfo,
    string CpuClock,
    string Motherboard,
    string Bios,
    string OsVersion);

public sealed class WindowsSystemInfo
{
    public SystemInfoSnapshot ReadSystemInfo()
    {
        return new SystemInfoSnapshot(
            ReadRamInfo(),
            ReadCpuClock(),
            ReadMotherboard(),
            ReadBios(),
            ReadOsVersion());
    }

    public string ReadStorageInterface(string driveName)
    {
        string fromPhysicalDisk = Query("root\\Microsoft\\Windows\\Storage", "SELECT FriendlyName, BusType FROM MSFT_PhysicalDisk")
            .Where(disk => IsNameMatch(driveName, disk.GetString("FriendlyName")))
            .Select(disk => FormatBusType(disk.GetUInt16("BusType")))
            .FirstOrDefault(value => value != "Unknown") ?? "Unknown";

        if (fromPhysicalDisk != "Unknown")
        {
            return fromPhysicalDisk;
        }

        return Query("root\\CIMV2", "SELECT Model, InterfaceType, PNPDeviceID FROM Win32_DiskDrive")
            .Where(disk => IsNameMatch(driveName, disk.GetString("Model")))
            .Select(disk => FormatDiskInterface(disk.GetString("InterfaceType"), disk.GetString("PNPDeviceID")))
            .FirstOrDefault(value => value != "Unknown") ?? "Unknown";
    }

    private static string ReadRamInfo()
    {
        WmiObject[] memoryModules = Query("root\\CIMV2", "SELECT Capacity, SMBIOSMemoryType, Speed, ConfiguredClockSpeed FROM Win32_PhysicalMemory").ToArray();
        ulong totalBytes = memoryModules
            .Select(module => module.GetUInt64("Capacity"))
            .Aggregate(0UL, (total, capacity) => total + capacity);

        if (totalBytes == 0)
        {
            return "Unknown";
        }

        string capacity = $"{Math.Round(totalBytes / 1024d / 1024d / 1024d):0} GB";
        string type = memoryModules
            .Select(module => FormatMemoryType(module.GetUInt16("SMBIOSMemoryType")))
            .FirstOrDefault(value => value != "Unknown") ?? "Unknown";
        uint speed = memoryModules
            .Select(module => module.GetUInt32("ConfiguredClockSpeed") ?? module.GetUInt32("Speed") ?? 0)
            .DefaultIfEmpty(0U)
            .Max();

        return speed > 0
            ? $"{capacity} {type} @ {speed.ToString(CultureInfo.InvariantCulture)} MHz"
            : $"{capacity} {type}";
    }

    private static string ReadMotherboard()
    {
        return Query("root\\CIMV2", "SELECT Manufacturer, Product FROM Win32_BaseBoard")
            .Select(board => JoinKnown(board.GetString("Manufacturer"), board.GetString("Product")))
            .FirstOrDefault(value => value != "Unknown") ?? "Unknown";
    }

    private static string ReadCpuClock()
    {
        uint clock = Query("root\\CIMV2", "SELECT CurrentClockSpeed, MaxClockSpeed FROM Win32_Processor")
            .Select(cpu => cpu.GetUInt32("CurrentClockSpeed") ?? cpu.GetUInt32("MaxClockSpeed") ?? 0)
            .FirstOrDefault(value => value > 0);

        return clock > 0 ? $"{clock.ToString(CultureInfo.InvariantCulture)} MHz" : "Unknown";
    }

    private static string ReadBios()
    {
        return Query("root\\CIMV2", "SELECT Manufacturer, SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS")
            .Select(bios =>
            {
                string releaseDate = FormatWmiDate(bios.GetString("ReleaseDate"));
                return JoinKnown(bios.GetString("Manufacturer"), bios.GetString("SMBIOSBIOSVersion"), releaseDate);
            })
            .FirstOrDefault(value => value != "Unknown") ?? "Unknown";
    }

    private static string ReadOsVersion()
    {
        return Query("root\\CIMV2", "SELECT Caption, Version, BuildNumber FROM Win32_OperatingSystem")
            .Select(os => JoinKnown(os.GetString("Caption"), $"Build {os.GetString("BuildNumber")}", os.GetString("Version")))
            .FirstOrDefault(value => value != "Unknown") ?? "Unknown";
    }

    private static WmiObject[] Query(string scope, string query)
    {
        try
        {
            using ManagementObjectSearcher searcher = new(new ManagementScope(scope), new ObjectQuery(query));
            return searcher.Get()
                .Cast<ManagementObject>()
                .Select(item => new WmiObject(item))
                .ToArray();
        }
        catch
        {
            return Array.Empty<WmiObject>();
        }
    }

    private static bool IsNameMatch(string driveName, string? wmiName)
    {
        if (string.IsNullOrWhiteSpace(driveName) || string.IsNullOrWhiteSpace(wmiName))
        {
            return false;
        }

        string normalizedDrive = NormalizeName(driveName);
        string normalizedWmi = NormalizeName(wmiName);
        return normalizedDrive.Contains(normalizedWmi, StringComparison.OrdinalIgnoreCase)
            || normalizedWmi.Contains(normalizedDrive, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeName(string value)
    {
        return value
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .Trim();
    }

    private static string FormatBusType(ushort? busType)
    {
        return busType switch
        {
            7 => "USB",
            11 => "SATA",
            17 => "NVMe",
            _ => "Unknown"
        };
    }

    private static string FormatDiskInterface(string? interfaceType, string? pnpDeviceId)
    {
        if (!string.IsNullOrWhiteSpace(pnpDeviceId) && pnpDeviceId.Contains("NVME", StringComparison.OrdinalIgnoreCase))
        {
            return "NVMe";
        }

        return string.IsNullOrWhiteSpace(interfaceType) ? "Unknown" : interfaceType;
    }

    private static string FormatMemoryType(ushort? memoryType)
    {
        return memoryType switch
        {
            24 => "DDR3",
            26 => "DDR4",
            34 => "DDR5",
            _ => "Unknown"
        };
    }

    private static string FormatWmiDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length < 8)
        {
            return "Unknown";
        }

        return DateTime.TryParseExact(value[..8], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime date)
            ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : "Unknown";
    }

    private static string JoinKnown(params string?[] values)
    {
        string[] known = values
            .Where(value => !string.IsNullOrWhiteSpace(value) && value != "Unknown")
            .Select(value => value!.Trim())
            .ToArray();

        return known.Length == 0 ? "Unknown" : string.Join(" ", known);
    }

    private sealed class WmiObject
    {
        private readonly ManagementObject _value;

        public WmiObject(ManagementObject value)
        {
            _value = value;
        }

        public string? GetString(string name)
        {
            return _value.Properties[name]?.Value?.ToString();
        }

        public ushort? GetUInt16(string name)
        {
            return _value.Properties[name]?.Value is ushort value ? value : null;
        }

        public uint? GetUInt32(string name)
        {
            return _value.Properties[name]?.Value is uint value ? value : null;
        }

        public ulong GetUInt64(string name)
        {
            return _value.Properties[name]?.Value is ulong value ? value : 0UL;
        }
    }
}
