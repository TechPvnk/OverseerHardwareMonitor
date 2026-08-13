using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Overseer.Services;

// Adapter for the separately distributed GPL smartmontools executable. No smartmontools code is linked into Overseer.
public sealed class SmartctlService
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(20);
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private IReadOnlyList<SmartctlDriveReport> _cachedReports = Array.Empty<SmartctlDriveReport>();
    private DateTime _lastRefreshUtc = DateTime.MinValue;

    public async Task<SmartctlRefreshResult> GetReportsAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        if (!forceRefresh && DateTime.UtcNow - _lastRefreshUtc < RefreshInterval)
        {
            return SmartctlRefreshResult.FromCache(_cachedReports);
        }

        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!forceRefresh && DateTime.UtcNow - _lastRefreshUtc < RefreshInterval)
            {
                return SmartctlRefreshResult.FromCache(_cachedReports);
            }

            string executablePath = GetExecutablePath();
            if (!File.Exists(executablePath))
            {
                return SmartctlRefreshResult.Unavailable("Smartmontools is not installed.");
            }

            ProcessResult scan = await RunAsync(executablePath, "--scan-open --json=o", cancellationToken).ConfigureAwait(false);
            if (scan.TimedOut)
            {
                return SmartctlRefreshResult.Unavailable("smartctl scan timed out.");
            }

            if (!TryGetDevices(scan.Output, out IReadOnlyList<SmartctlDevice> devices, out string? scanError))
            {
                return SmartctlRefreshResult.Unavailable(scanError ?? "smartctl returned malformed JSON.");
            }

            var reports = new List<SmartctlDriveReport>();
            foreach (SmartctlDevice device in devices)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ProcessResult result = await RunAsync(executablePath, BuildDeviceArguments(device), cancellationToken).ConfigureAwait(false);
                reports.Add(ParseReport(device, result));
            }

            _cachedReports = reports;
            _lastRefreshUtc = DateTime.UtcNow;
            return SmartctlRefreshResult.Success(reports);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return SmartctlRefreshResult.Unavailable($"smartctl failed: {ex.Message}");
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private static string GetExecutablePath()
    {
        return Path.Combine(AppContext.BaseDirectory, "ThirdParty", "Smartmontools", "bin", "smartctl.exe");
    }

    private static string BuildDeviceArguments(SmartctlDevice device)
    {
        string typeArgument = string.IsNullOrWhiteSpace(device.Type) || device.Type.Equals("auto", StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : $" -d {Quote(device.Type)}";
        return $"-a --json=o{typeArgument} {Quote(device.Name)}";
    }

    private static async Task<ProcessResult> RunAsync(string executablePath, string arguments, CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

        process.Start();
        Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
        Task<string> errorTask = process.StandardError.ReadToEndAsync();
        Task completed = await Task.WhenAny(process.WaitForExitAsync(cancellationToken), Task.Delay(ProcessTimeout, cancellationToken)).ConfigureAwait(false);
        if (completed != outputTask && !process.HasExited)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            return new ProcessResult(string.Empty, "smartctl timed out.", -1, true);
        }

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return new ProcessResult(await outputTask.ConfigureAwait(false), await errorTask.ConfigureAwait(false), process.ExitCode, false);
    }

    private static bool TryGetDevices(string json, out IReadOnlyList<SmartctlDevice> devices, out string? error)
    {
        devices = Array.Empty<SmartctlDevice>();
        error = null;
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("devices", out JsonElement array) || array.ValueKind != JsonValueKind.Array)
            {
                error = ReadMessages(document.RootElement) ?? "No supported SMART devices were found.";
                return false;
            }

            devices = array.EnumerateArray()
                .Select(element => new SmartctlDevice(GetString(element, "name") ?? string.Empty, GetString(element, "type"), GetString(element, "info_name")))
                .Where(device => !string.IsNullOrWhiteSpace(device.Name))
                .ToArray();
            return true;
        }
        catch (JsonException)
        {
            error = "smartctl returned malformed JSON.";
            return false;
        }
    }

    private static SmartctlDriveReport ParseReport(SmartctlDevice device, ProcessResult result)
    {
        if (result.TimedOut)
        {
            return SmartctlDriveReport.Unavailable(device, "smartctl timed out.");
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(result.Output);
            JsonElement root = document.RootElement;
            string? message = ReadMessages(root) ?? (string.IsNullOrWhiteSpace(result.Error) ? null : result.Error.Trim());
            bool? smartPassed = TryGetBool(root, "smart_status", "passed");
            var attributes = ParseAtaAttributes(root);
            SmartctlNvmeHealth? nvme = ParseNvmeHealth(root);
            string? hostReads = nvme?.HostReads ?? FindAtaRawValue(attributes, "Total_LBAs_Read");
            string? hostWrites = nvme?.HostWrites ?? FindAtaRawValue(attributes, "Total_LBAs_Written");
            return new SmartctlDriveReport(
                device,
                true,
                message,
                GetString(root, "model_name") ?? GetString(root, "model_family") ?? device.InfoName ?? device.Name,
                GetString(root, "serial_number"),
                GetString(root, "firmware_version"),
                FormatCapacity(root),
                GetString(root, "device", "protocol") ?? device.Type,
                GetString(root, "device", "type"),
                smartPassed,
                GetNumberText(root, "power_on_time", "hours"),
                GetNumberText(root, "user_capacity", "bytes"),
                hostReads,
                hostWrites,
                GetString(root, "ata_smart_data", "self_test", "status", "string"),
                attributes,
                nvme);
        }
        catch (JsonException)
        {
            return SmartctlDriveReport.Unavailable(device, "smartctl returned malformed JSON.");
        }
    }

    private static IReadOnlyList<SmartctlAtaAttribute> ParseAtaAttributes(JsonElement root)
    {
        if (!TryGet(root, out JsonElement table, "ata_smart_attributes", "table") || table.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<SmartctlAtaAttribute>();
        }

        return table.EnumerateArray().Select(item => new SmartctlAtaAttribute(
            GetNumberText(item, "id") ?? "-",
            GetString(item, "name") ?? "Unknown",
            GetNumberText(item, "value") ?? "-",
            GetNumberText(item, "worst") ?? "-",
            GetNumberText(item, "thresh") ?? "-",
            GetString(item, "when_failed") ?? "-",
            GetString(item, "raw", "string") ?? GetNumberText(item, "raw", "value") ?? "-")).ToArray();
    }

    private static SmartctlNvmeHealth? ParseNvmeHealth(JsonElement root)
    {
        if (!root.TryGetProperty("nvme_smart_health_information_log", out JsonElement health) || health.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        string criticalWarning = GetNumberText(health, "critical_warning") ?? "0";
        string temperature = GetNumberText(health, "temperature") ?? "N/A";
        string availableSpare = GetNumberText(health, "available_spare") ?? "N/A";
        string spareThreshold = GetNumberText(health, "available_spare_threshold") ?? "N/A";
        string percentageUsed = GetNumberText(health, "percentage_used") ?? "N/A";
        string dataUnitsRead = GetNumberText(health, "data_units_read") ?? "N/A";
        string dataUnitsWritten = GetNumberText(health, "data_units_written") ?? "N/A";
        string hostReads = GetNumberText(health, "host_reads") ?? "N/A";
        string hostWrites = GetNumberText(health, "host_writes") ?? "N/A";
        string controllerBusyTime = GetNumberText(health, "controller_busy_time") ?? "N/A";
        string powerCycles = GetNumberText(health, "power_cycles") ?? "N/A";
        string powerOnHours = GetNumberText(health, "power_on_hours") ?? "N/A";
        string unsafeShutdowns = GetNumberText(health, "unsafe_shutdowns") ?? "N/A";
        string mediaErrors = GetNumberText(health, "media_and_data_integrity_errors") ?? "N/A";
        string errorLogEntries = GetNumberText(health, "num_err_log_entries") ?? "N/A";
        string warningTempTime = GetNumberText(health, "warning_temp_time") ?? "N/A";
        string criticalTempTime = GetNumberText(health, "critical_comp_time") ?? "N/A";
        string temperatureWarning = GetNumberText(root, "nvme_composite_temperature_threshold", "warning") ?? "N/A";
        string temperatureCritical = GetNumberText(root, "nvme_composite_temperature_threshold", "critical") ?? "N/A";

        var attributes = new List<SmartctlNvmeAttribute>
        {
            new("01", "Critical Warning", criticalWarning, "0", criticalWarning),
            new("02", "Composite Temperature", $"{temperature} C", $"{temperatureWarning} C / {temperatureCritical} C", temperature),
            new("03", "Available Spare", $"{availableSpare}%", $"{spareThreshold}%", availableSpare),
            new("04", "Percentage Used", $"{percentageUsed}%", "100%", percentageUsed),
            new("05", "Data Units Read", dataUnitsRead, "-", dataUnitsRead),
            new("06", "Data Units Written", dataUnitsWritten, "-", dataUnitsWritten),
            new("07", "Host Read Commands", hostReads, "-", hostReads),
            new("08", "Host Write Commands", hostWrites, "-", hostWrites),
            new("09", "Controller Busy Time", controllerBusyTime, "-", controllerBusyTime),
            new("0A", "Power Cycles", powerCycles, "-", powerCycles),
            new("0B", "Power On Hours", powerOnHours, "-", powerOnHours),
            new("0C", "Unsafe Shutdowns", unsafeShutdowns, "-", unsafeShutdowns),
            new("0D", "Media and Data Integrity Errors", mediaErrors, "0", mediaErrors),
            new("0E", "Error Information Log Entries", errorLogEntries, "-", errorLogEntries),
            new("0F", "Warning Composite Temperature Time", warningTempTime, "-", warningTempTime),
            new("10", "Critical Composite Temperature Time", criticalTempTime, "-", criticalTempTime)
        };

        if (health.TryGetProperty("temperature_sensors", out JsonElement sensors) && sensors.ValueKind == JsonValueKind.Array)
        {
            int sensorIndex = 0;
            foreach (JsonElement sensor in sensors.EnumerateArray())
            {
                sensorIndex++;
                string value = sensor.ValueKind == JsonValueKind.Number ? sensor.GetRawText() : "N/A";
                attributes.Add(new SmartctlNvmeAttribute((0x10 + sensorIndex).ToString("X2", CultureInfo.InvariantCulture), $"Temperature Sensor {sensorIndex}", $"{value} C", "-", value));
            }
        }

        return new SmartctlNvmeHealth(criticalWarning, temperature, percentageUsed, dataUnitsRead, dataUnitsWritten, hostReads, hostWrites, powerCycles, powerOnHours, unsafeShutdowns, mediaErrors, errorLogEntries, attributes);
    }

    private static string? FormatCapacity(JsonElement root)
    {
        if (!TryGet(root, out JsonElement bytes, "user_capacity", "bytes") || !bytes.TryGetInt64(out long value))
        {
            return null;
        }
        return $"{value / 1_000_000_000d:0.0} GB";
    }

    private static string? FindAtaRawValue(IEnumerable<SmartctlAtaAttribute> attributes, string attributeName)
    {
        return attributes.FirstOrDefault(attribute => attribute.Name.Equals(attributeName, StringComparison.OrdinalIgnoreCase))?.RawValue;
    }

    private static bool TryGet(JsonElement element, out JsonElement value, params string[] path)
    {
        value = element;
        foreach (string part in path)
        {
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(part, out value))
            {
                return false;
            }
        }
        return true;
    }

    private static string? GetString(JsonElement element, params string[] path)
    {
        return TryGet(element, out JsonElement value, path) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static string? GetNumberText(JsonElement element, params string[] path)
    {
        if (!TryGet(element, out JsonElement value, path)) return null;
        return value.ValueKind == JsonValueKind.Number ? value.GetRawText() : value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static bool? TryGetBool(JsonElement element, params string[] path)
    {
        return TryGet(element, out JsonElement value, path) && (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False) ? value.GetBoolean() : null;
    }

    private static string? ReadMessages(JsonElement root)
    {
        if (!root.TryGetProperty("messages", out JsonElement messages) || messages.ValueKind != JsonValueKind.Array) return null;
        return string.Join(" ", messages.EnumerateArray().Select(message => GetString(message, "string")).Where(message => !string.IsNullOrWhiteSpace(message))!);
    }

    private static string Quote(string value) => $"\"{value.Replace("\"", "\\\"")}\"";

    private sealed record ProcessResult(string Output, string Error, int ExitCode, bool TimedOut);
}

public sealed record SmartctlDevice(string Name, string? Type, string? InfoName);

public sealed record SmartctlAtaAttribute(string Id, string Name, string Value, string Worst, string Threshold, string WhenFailed, string RawValue);

public sealed record SmartctlNvmeAttribute(string Id, string Name, string Current, string Threshold, string RawValue);

public sealed record SmartctlNvmeHealth(string CriticalWarning, string Temperature, string PercentageUsed, string DataUnitsRead, string DataUnitsWritten, string HostReads, string HostWrites, string PowerCycles, string PowerOnHours, string UnsafeShutdowns, string MediaErrors, string ErrorLogEntries, IReadOnlyList<SmartctlNvmeAttribute> Attributes);

public sealed record SmartctlDriveReport(
    SmartctlDevice Device,
    bool IsAvailable,
    string? StatusMessage,
    string Model,
    string? SerialNumber,
    string? FirmwareVersion,
    string? Capacity,
    string? Protocol,
    string? DeviceType,
    bool? SmartPassed,
    string? PowerOnHours,
    string? CapacityBytes,
    string? HostReads,
    string? HostWrites,
    string? SelfTestStatus,
    IReadOnlyList<SmartctlAtaAttribute> AtaAttributes,
    SmartctlNvmeHealth? NvmeHealth)
{
    public static SmartctlDriveReport Unavailable(SmartctlDevice device, string message) => new(device, false, message, device.InfoName ?? device.Name, null, null, null, null, null, null, null, null, null, null, null, Array.Empty<SmartctlAtaAttribute>(), null);
}

public sealed record SmartctlRefreshResult(bool IsAvailable, bool IsFromCache, string? StatusMessage, IReadOnlyList<SmartctlDriveReport> Reports)
{
    public static SmartctlRefreshResult Success(IReadOnlyList<SmartctlDriveReport> reports) => new(true, false, null, reports);
    public static SmartctlRefreshResult FromCache(IReadOnlyList<SmartctlDriveReport> reports) => new(true, true, null, reports);
    public static SmartctlRefreshResult Unavailable(string message) => new(false, false, message, Array.Empty<SmartctlDriveReport>());
}
