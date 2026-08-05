using System;
using System.Globalization;
using System.Linq;
using System.Management;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Overseer.Models;

public sealed record SystemInfoSnapshot(
    string RamInfo,
    string RamTotal,
    string RamType,
    string RamClock,
    IReadOnlyList<string> RamModules,
    string CpuClock,
    string CpuCoresThreads,
    string CpuCaches,
    string CpuTdp,
    string GpuRam,
    string GpuBus,
    string Motherboard,
    string Bios,
    string OsVersion,
    IReadOnlyList<string> MotherboardSubHardware,
    string BatteryInfo);

public sealed class WindowsSystemInfo
{
    // Toggle to emit WMI queries to Debug output for troubleshooting
    public static bool LogWmiQueries { get; set; } = false;
    // cache scopes that previously failed to avoid repeated WMI exceptions (reduces noisy first-chance exceptions)
    private static readonly System.Collections.Generic.HashSet<string> _failedWmiScopes = new(System.StringComparer.OrdinalIgnoreCase);

    public SystemInfoSnapshot ReadSystemInfo()
    {
        var modules = ReadRamModules();
        string total = ReadRamTotal();
        string type = ReadRamType();
        string clock = ReadRamClock();
        var mbSub = Array.Empty<string>();
        try
        {
            // no direct access to LibreHardwareMonitor here; leave empty, HardwareMonitorEngine will fill this
            mbSub = Array.Empty<string>();
        }
        catch { }

        string battery = ReadBatteryInfo();

        return new SystemInfoSnapshot(
            // RamInfo kept for backward compatibility (formatted)
            !string.IsNullOrWhiteSpace(total) && !string.IsNullOrWhiteSpace(clock) ? $"{total} @ {clock}" : ReadRamInfo(),
            total,
            type,
            clock,
            modules,
            ReadCpuClock(),
            ReadCpuCoresThreads(),
            ReadCpuCaches(),
            ReadCpuTdp(),
            ReadGpuRam(),
            ReadGpuBus(),
            ReadMotherboard(),
            ReadBios(),
            ReadOsVersion(),
            mbSub,
            battery);
    }

    private static string ReadBatteryInfo()
    {
        // Prefer native GetSystemPowerStatus (works on most laptops). Fall back to WMI when not available.
        try
        {
            if (GetSystemPowerStatus(out SYSTEM_POWER_STATUS status))
            {
                // BatteryLifePercent: 0-100, 255 = unknown
                string percent = status.BatteryLifePercent != 255 ? status.BatteryLifePercent + "%" : "Unknown";
                string ac = status.ACLineStatus switch
                {
                    0 => "Offline",
                    1 => "Online",
                    255 => "Unknown",
                    _ => "Unknown"
                };

                string life = status.BatteryLifeTime == uint.MaxValue ? "Unknown" : status.BatteryLifeTime + " sec";
                string full = status.BatteryFullLifeTime == uint.MaxValue ? "Unknown" : status.BatteryFullLifeTime + " sec";

                return $"Charge: {percent} | AC: {ac} | Life: {life} | FullLife: {full}";
            }
        }
        catch
        {
            // ignore and fall back to WMI
        }

        try
        {
            var batteries = Query("root\\CIMV2", "SELECT EstimatedChargeRemaining, BatteryStatus, Voltage, EstimatedRunTime FROM Win32_Battery");
            var bat = batteries.FirstOrDefault();
            if (bat == null)
            {
                return "Not present";
            }

            string percentWmi = bat.GetUInt32("EstimatedChargeRemaining") is uint p ? (p > 0 ? p.ToString(CultureInfo.InvariantCulture) + "%" : "Unknown") : "Unknown";
            string statusStr = bat.GetUInt32("BatteryStatus") is uint s ? s.ToString(CultureInfo.InvariantCulture) : "Unknown";
            string voltage = bat.GetUInt32("Voltage") is uint v ? (v > 0 ? (v / 1000.0).ToString("0.###", CultureInfo.InvariantCulture) + " V" : "Unknown") : "Unknown";
            string runtime = bat.GetUInt32("EstimatedRunTime") is uint r ? (r > 0 ? r + " min" : "Unknown") : "Unknown";

            return $"Charge: {percentWmi} | Status: {statusStr} | Voltage: {voltage} | EstRun: {runtime}";
        }
        catch
        {
            return "Unknown";
        }
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS lpSystemPowerStatus);

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte Reserved1;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    private static string ReadRamTotal()
    {
        WmiObject[] memoryModules = Query("root\\CIMV2", "SELECT Capacity FROM Win32_PhysicalMemory").ToArray();
        ulong totalBytes = memoryModules.Select(m => m.GetUInt64("Capacity")).Aggregate(0UL, (t, v) => t + v);
        if (totalBytes == 0) return "Unknown";
        return $"{Math.Round(totalBytes / 1024d / 1024d / 1024d):0} GB";
    }

    private static string ReadRamType()
    {
        WmiObject[] memoryModules = Query("root\\CIMV2", "SELECT SMBIOSMemoryType FROM Win32_PhysicalMemory");
        var type = memoryModules.Select(m => FormatMemoryType(m.GetUInt16("SMBIOSMemoryType"))).FirstOrDefault(v => v != "Unknown");
        return type ?? "Unknown";
    }

    private static string ReadRamClock()
    {
        WmiObject[] memoryModules = Query("root\\CIMV2", "SELECT ConfiguredClockSpeed, Speed FROM Win32_PhysicalMemory").ToArray();
        uint speed = memoryModules.Select(m => m.GetUInt32("ConfiguredClockSpeed") ?? m.GetUInt32("Speed") ?? 0u).DefaultIfEmpty(0u).Max();
        return speed > 0 ? $"{speed.ToString(CultureInfo.InvariantCulture)} MHz" : "Unknown";
    }

    private static IReadOnlyList<string> ReadRamModules()
    {
        var modules = Query("root\\CIMV2", "SELECT Manufacturer, PartNumber, Capacity, Speed, ConfiguredClockSpeed FROM Win32_PhysicalMemory")
            .Select(m =>
            {
                string man = m.GetString("Manufacturer") ?? "";
                string part = m.GetString("PartNumber") ?? "";
                ulong cap = m.GetUInt64("Capacity");
                uint speed = m.GetUInt32("ConfiguredClockSpeed") ?? m.GetUInt32("Speed") ?? 0u;
                string capStr = cap > 0 ? $"{Math.Round(cap / 1024d / 1024d / 1024d):0} GB" : "Unknown";
                string speedStr = speed > 0 ? $"@ {speed} MHz" : string.Empty;
                string name = string.IsNullOrWhiteSpace(part) ? man : $"{man} {part}".Trim();
                if (string.IsNullOrWhiteSpace(name)) name = "Unknown Module";
                return $"{name}: {capStr} {speedStr}".Trim();
            })
            .ToArray();

        return modules;
    }

    // Best-effort: get GPU RAM matching a LibreHardwareMonitor GPU name
    public string ReadGpuRamFor(string? gpuName)
    {
        if (!string.IsNullOrWhiteSpace(gpuName))
        {
            try
            {
                var adapters = DxgiNative.EnumerateAdapters();
                // try to match by name fragments
                var match = adapters.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a.Description) &&
                    (a.Description.Contains(gpuName, StringComparison.OrdinalIgnoreCase)
                     || gpuName.Contains(a.Description, StringComparison.OrdinalIgnoreCase)));

                // If no direct contains match, try token intersection (fuzzy match)
                if (match == null && !string.IsNullOrWhiteSpace(gpuName))
                {
                    var gpuTokens = Tokenize(gpuName);
                    int bestScore = 0;
                    foreach (var a in adapters)
                    {
                        var descTokens = Tokenize(a.Description ?? string.Empty);
                        int inter = gpuTokens.Intersect(descTokens).Count();
                        if (inter > bestScore && inter > 0)
                        {
                            bestScore = inter;
                            match = a;
                        }
                    }
                }

                if (match is not null && match.DedicatedVideoMemory > 0)
                {
                    double gb = Math.Round(match.DedicatedVideoMemory / 1024d / 1024d / 1024d, 2);
                    return gb >= 1 ? $"{gb:0.##} GB" : $"{Math.Round(match.DedicatedVideoMemory / 1024d / 1024d, 2)} MB";
                }
            }
            catch
            {
                // ignore and fall back
            }
        }

        // fallback to generic DXGI/WMI method
        return ReadGpuRam();
    }

    public string ReadGpuBusFor(string? gpuName)
    {
        if (!string.IsNullOrWhiteSpace(gpuName))
        {
            try
            {
                var adapters = DxgiNative.EnumerateAdapters();
                var match = adapters.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a.Description) &&
                    (a.Description.Contains(gpuName, StringComparison.OrdinalIgnoreCase)
                     || gpuName.Contains(a.Description, StringComparison.OrdinalIgnoreCase)));

                if (match == null && !string.IsNullOrWhiteSpace(gpuName))
                {
                    var gpuTokens = Tokenize(gpuName);
                    int bestScore = 0;
                    foreach (var a in adapters)
                    {
                        var descTokens = Tokenize(a.Description ?? string.Empty);
                        int inter = gpuTokens.Intersect(descTokens).Count();
                        if (inter > bestScore && inter > 0)
                        {
                            bestScore = inter;
                            match = a;
                        }
                    }
                }

                if (match is not null)
                {
                    // Try to locate a matching Win32_VideoController by name
                    var controllers = Query("root\\CIMV2", "SELECT Name, PNPDeviceID FROM Win32_VideoController");
                    var controller = controllers.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c.GetString("Name")) &&
                        (c.GetString("Name").Contains(match.Description, StringComparison.OrdinalIgnoreCase)
                         || (!string.IsNullOrWhiteSpace(gpuName) && c.GetString("Name").Contains(gpuName, StringComparison.OrdinalIgnoreCase))));

                    if (controller != null)
                    {
                        string? pnp = controller.GetString("PNPDeviceID");
                        if (!string.IsNullOrWhiteSpace(pnp))
                        {
                            try
                            {
                // Avoid selecting LocationInformation directly to prevent WMI providers that reject that property.
                // Select DeviceID and PNPDeviceID/Name and inspect them instead.
                var ents = Query("root\\CIMV2", $"SELECT DeviceID, PNPDeviceID, Name FROM Win32_PnPEntity WHERE DeviceID = '{EscapeWmiString(pnp)}'");
                var ent = ents.FirstOrDefault();
                string? pnpId = ent?.GetString("PNPDeviceID");
                string? name = ent?.GetString("Name");
                if (!string.IsNullOrWhiteSpace(pnpId) && (pnpId.Contains("PCI", StringComparison.OrdinalIgnoreCase) || pnpId.Contains("PCIVEN", StringComparison.OrdinalIgnoreCase)))
                {
                    return "PCIe";
                }
                if (!string.IsNullOrWhiteSpace(name) && name.Contains("PCI", StringComparison.OrdinalIgnoreCase))
                {
                    return name;
                }
                            }
                            catch
                            {
                                // ignore
                            }

                            if (pnp.Contains("PCI", StringComparison.OrdinalIgnoreCase) || pnp.Contains("PCIVEN", StringComparison.OrdinalIgnoreCase))
                            {
                                return "PCIe";
                            }
                        }
                    }
                }
            }
            catch
            {
                // ignore
            }
        }

        return ReadGpuBus();
    }

    private static string ReadGpuRam()
    {
        // Try DXGI native enumeration first
        try
        {
            var adapters = DxgiNative.EnumerateAdapters();
            var first = adapters.FirstOrDefault();
            if (first is not null && first.DedicatedVideoMemory > 0)
            {
                double gb = Math.Round(first.DedicatedVideoMemory / 1024d / 1024d / 1024d, 2);
                return gb >= 1 ? $"{gb:0.##} GB" : $"{Math.Round(first.DedicatedVideoMemory / 1024d / 1024d, 2)} MB";
            }
        }
        catch
        {
            // fall back to WMI
        }

        // Fallback to WMI if DXGI did not return a value
        var gpus = Query("root\\CIMV2", "SELECT Name, AdapterRAM, PNPDeviceID FROM Win32_VideoController");
        var gpu = gpus.FirstOrDefault();
        if (gpu == null) return "Unknown";

        // AdapterRAM may be returned as bytes
        ulong ramBytes = gpu.GetUInt64("AdapterRAM");
        if (ramBytes == 0UL)
        {
            // try as 32-bit
            uint? ram32 = gpu.GetUInt32("AdapterRAM");
            if (ram32.HasValue && ram32.Value > 0)
            {
                ramBytes = ram32.Value;
            }
        }

        if (ramBytes == 0UL) return "Unknown";

        double gbFallback = Math.Round(ramBytes / 1024d / 1024d / 1024d, 2);
        return gbFallback >= 1 ? $"{gbFallback:0.##} GB" : $"{Math.Round(ramBytes / 1024d / 1024d, 2)} MB";
    }

    private static string ReadGpuBus()
    {
        var gpus = Query("root\\CIMV2", "SELECT Name, AdapterRAM, PNPDeviceID FROM Win32_VideoController");
        var gpu = gpus.FirstOrDefault();
        if (gpu == null) return "Unknown";

        string? pnp = gpu.GetString("PNPDeviceID");
        if (string.IsNullOrWhiteSpace(pnp)) return "Unknown";

        // Try LocationInformation via Win32_PnPEntity which sometimes contains PCI bus/device/function info
        try
        {
            // First try an exact WMI lookup. If it fails (invalid query or no result),
            // fall back to querying all PnPEntity rows and match DeviceID in managed code
            try
            {
                // Query PNPDeviceID and Name instead of LocationInformation which some providers reject
                var ents = Query("root\\CIMV2", $"SELECT DeviceID, PNPDeviceID, Name FROM Win32_PnPEntity WHERE DeviceID = '{EscapeWmiString(pnp)}'");
                var ent = ents.FirstOrDefault();
                string? pnpIdLocal = ent?.GetString("PNPDeviceID");
                string? nameLocal = ent?.GetString("Name");
                if (!string.IsNullOrWhiteSpace(pnpIdLocal) && (pnpIdLocal.Contains("PCI", StringComparison.OrdinalIgnoreCase) || pnpIdLocal.Contains("PCIVEN", StringComparison.OrdinalIgnoreCase)))
                {
                    return "PCIe";
                }
                if (!string.IsNullOrWhiteSpace(nameLocal) && nameLocal.Contains("PCI", StringComparison.OrdinalIgnoreCase))
                {
                    return nameLocal;
                }
            }
            catch
            {
                // exact-query failed; fall through to managed-match fallback below
            }

            // Fallback: enumerate PnP entities and match DeviceID in managed code to avoid WQL escaping issues
            ManagementException? lastEx = null;
            WmiObject[] all = Array.Empty<WmiObject>();
            try
            {
                all = Query("root\\CIMV2", "SELECT DeviceID, PNPDeviceID, Name FROM Win32_PnPEntity");
            }
            catch (ManagementException mex)
            {
                lastEx = mex;
            }

            if ((all == null || all.Length == 0) && lastEx != null)
            {
                // try a more permissive query if selecting LocationInformation failed on this system
                try
                {
                    all = Query("root\\CIMV2", "SELECT DeviceID, Name FROM Win32_PnPEntity");
                }
                catch
                {
                    // try wildcard star
                    try { all = Query("root\\CIMV2", "SELECT * FROM Win32_PnPEntity"); } catch { all = Array.Empty<WmiObject>(); }
                }
            }
            if (all.Length > 0)
            {
                // Normalize DeviceID by collapsing multiple backslashes and uppercasing for comparison
                string NormalizeId(string? id) => (id ?? string.Empty).Replace("\\\\", "\\").Trim().ToUpperInvariant();
                string target = NormalizeId(pnp);
                var ent = all.FirstOrDefault(e => target == NormalizeId(e.GetString("DeviceID")) || (NormalizeId(e.GetString("DeviceID")).Contains(target)));
                string? pnpId2 = ent?.GetString("PNPDeviceID");
                string? name2 = ent?.GetString("Name");
                if (!string.IsNullOrWhiteSpace(pnpId2) && (pnpId2.Contains("PCI", StringComparison.OrdinalIgnoreCase) || pnpId2.Contains("PCIVEN", StringComparison.OrdinalIgnoreCase)))
                {
                    return "PCIe";
                }
                if (!string.IsNullOrWhiteSpace(name2) && name2.Contains("PCI", StringComparison.OrdinalIgnoreCase))
                {
                    return name2;
                }
            }
        }
        catch
        {
            // ignore and fall back
        }

        // Best-effort: if PNPDeviceID references PCI, assume PCIe
        if (pnp.Contains("PCI", StringComparison.OrdinalIgnoreCase) || pnp.Contains("PCIVEN", StringComparison.OrdinalIgnoreCase))
        {
            return "PCIe";
        }

        return "Unknown";
    }

    private static string EscapeWmiString(string value)
    {
        // Escape backslashes and quotes for WMI query
        if (string.IsNullOrEmpty(value)) return string.Empty;
        // WMI/WQL string literals: single quotes are doubled to escape; backslashes must be doubled
        // because many DeviceID values contain backslashes (e.g., "ROOT\\DISPLAY\\0000").
        return value.Replace("\\", "\\\\").Replace("'", "''");
    }

    private static System.Collections.Generic.HashSet<string> Tokenize(string? value)
    {
        var set = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(value)) return set;

        // split on non-alphanumeric and filter short tokens
        var parts = System.Text.RegularExpressions.Regex.Split(value, "[^A-Za-z0-9]+");
        foreach (var p in parts)
        {
            if (string.IsNullOrWhiteSpace(p)) continue;
            if (p.Length < 2) continue;
            set.Add(p.ToLowerInvariant());
        }

        return set;
    }

    private static string ReadCpuCoresThreads()
    {
        var cpus = Query("root\\CIMV2", "SELECT NumberOfCores, NumberOfLogicalProcessors FROM Win32_Processor");
        if (cpus.Length == 0) return "Unknown";

        int cores = cpus.Select(c => Convert.ToInt32(c.GetUInt32("NumberOfCores") ?? 0u)).Sum();
        int threads = cpus.Select(c => Convert.ToInt32(c.GetUInt32("NumberOfLogicalProcessors") ?? 0u)).Sum();

        return cores > 0 ? threads > 0 ? $"{cores} cores / {threads} threads" : $"{cores} cores" : "Unknown";
    }

    private static string ReadCpuCaches()
    {
        // Try Win32_Processor first (L2/L3 commonly available)
        var procs = Query("root\\CIMV2", "SELECT L2CacheSize, L3CacheSize FROM Win32_Processor");
        uint? l2 = procs.Select(p => p.GetUInt32("L2CacheSize")).FirstOrDefault(v => v.GetValueOrDefault() > 0u);
        uint? l3 = procs.Select(p => p.GetUInt32("L3CacheSize")).FirstOrDefault(v => v.GetValueOrDefault() > 0u);

        // Fallback to Win32_CacheMemory for L1/L2/L3 if available
        var caches = Query("root\\CIMV2", "SELECT Level, MaxCacheSize FROM Win32_CacheMemory");
        uint? l1FromCache = caches.Where(c => c.GetUInt32("Level") == 1).Select(c => c.GetUInt32("MaxCacheSize")).FirstOrDefault(v => v.GetValueOrDefault() > 0u);
        uint? l2FromCache = caches.Where(c => c.GetUInt32("Level") == 2).Select(c => c.GetUInt32("MaxCacheSize")).FirstOrDefault(v => v.GetValueOrDefault() > 0u);
        uint? l3FromCache = caches.Where(c => c.GetUInt32("Level") == 3).Select(c => c.GetUInt32("MaxCacheSize")).FirstOrDefault(v => v.GetValueOrDefault() > 0u);

        uint? l1 = l1FromCache;
        if (!l2.HasValue || l2.GetValueOrDefault() == 0u) l2 = l2FromCache;
        if (!l3.HasValue || l3.GetValueOrDefault() == 0u) l3 = l3FromCache;

        if (!l1.HasValue && !l2.HasValue && !l3.HasValue)
        {
            return "Unknown";
        }

        string FormatKb(uint kb)
        {
            if (kb >= 1024) return $"{kb / 1024} MB";
            return $"{kb} KB";
        }

        var parts = new System.Collections.Generic.List<string>();
        if (l1.HasValue && l1.GetValueOrDefault() > 0u) parts.Add($"L1: {FormatKb(l1.GetValueOrDefault())}");
        if (l2.HasValue && l2.GetValueOrDefault() > 0u) parts.Add($"L2: {FormatKb(l2.GetValueOrDefault())}");
        if (l3.HasValue && l3.GetValueOrDefault() > 0u) parts.Add($"L3: {FormatKb(l3.GetValueOrDefault())}");

        return parts.Count > 0 ? string.Join(", ", parts) : "Unknown";
    }

    private static string ReadCpuTdp()
    {
        // TDP is often not exposed via WMI. Try common property names; otherwise unknown.
        var procs = Query("root\\CIMV2", "SELECT ThermalDesignPower, PowerManagementCapabilities, Name FROM Win32_Processor");
        foreach (var p in procs)
        {
            // some systems may expose a ThermalDesignPower property
            uint? tdp = p.GetUInt32("ThermalDesignPower") ?? p.GetUInt32("TDP") ?? p.GetUInt32("MaxTDP");
            if (tdp.HasValue && tdp.Value > 0)
            {
                return $"{tdp.Value} W";
            }
        }

        return "Unknown";
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
        if (_failedWmiScopes.Contains(scope))
        {
            return Array.Empty<WmiObject>();
        }

        if (LogWmiQueries)
        {
            try { System.Diagnostics.Debug.WriteLine($"WMI Query (scope={scope}): {query}"); } catch { }
        }

        try
        {
            ManagementScope ms = new(scope);
            using ManagementObjectSearcher searcher = new(ms, new ObjectQuery(query));
            var results = searcher.Get()
                .Cast<ManagementObject>()
                .Select(item => new WmiObject(item))
                .ToArray();
            return results;
        }
        catch (ManagementException mex)
        {
            // record failing scope to avoid repeating the exception on subsequent calls
            // but do not cache common system scopes (CIMV2/WMI) to avoid accidental data loss
            try
            {
                if (!scope.Equals("root\\CIMV2", StringComparison.OrdinalIgnoreCase)
                    && !scope.Equals("root\\WMI", StringComparison.OrdinalIgnoreCase))
                {
                    _failedWmiScopes.Add(scope);
                }
            }
            catch { }

            // Log full query for debugging
            try
            {
                System.Diagnostics.Debug.WriteLine($"WMI query failed for scope '{scope}' query: {query} -> {mex.Message}");
            }
            catch
            {
                System.Diagnostics.Debug.WriteLine($"WMI query failed for scope '{scope}': {mex.Message}");
            }

            return Array.Empty<WmiObject>();
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
