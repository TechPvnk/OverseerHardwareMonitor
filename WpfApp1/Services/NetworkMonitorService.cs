using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Management;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Overseer.Services;

public sealed record NetworkAdapterInfo(
    string Id,
    string Name,
    string Description,
    string InterfaceType,
    string LinkSpeed,
    string Ipv4Address,
    string Ipv6Address,
    string MacAddress,
    string MaximumLinkSpeed,
    string HardwareId)
{
    public string MaskedMacAddress => MacAddress == "—" ? "—" : "••-••-••-••-••-••";
    public string MaskedIpv4Address => MaskIpAddress(Ipv4Address);
    public string MaskedIpv6Address => MaskIpAddress(Ipv6Address);
    public bool HasDistinctName => !string.Equals(Name, InterfaceType, StringComparison.OrdinalIgnoreCase);

    private static string MaskIpAddress(string address)
    {
        if (string.IsNullOrWhiteSpace(address) || address == "—")
        {
            return "—";
        }

        return address.Contains(':', StringComparison.Ordinal)
            ? "••••:••••:••••:••••"
            : "•••.•••.•••.•••";
    }
}

public sealed record NetworkThroughputSample(
    double? DownloadMegabitsPerSecond,
    double? UploadMegabitsPerSecond,
    string? AdapterSummary);

public sealed class NetworkMonitorService : IDisposable
{
    private static readonly object MetadataCacheLock = new();
    private static IReadOnlyDictionary<string, NetworkAdapterMetadata> _metadataByAdapterId = new Dictionary<string, NetworkAdapterMetadata>();
    private static DateTime _metadataExpiresUtc = DateTime.MinValue;
    private IReadOnlyList<NetworkInterface> _activeAdapters = Array.Empty<NetworkInterface>();
    private DateTime _nextDiscoveryUtc = DateTime.MinValue;
    private DateTime? _previousSampleUtc;
    private long _previousReceivedBytes;
    private long _previousSentBytes;
    private string _previousAdapterKey = string.Empty;
    private bool _disposed;

    public NetworkThroughputSample Sample()
    {
        if (_disposed)
        {
            return new NetworkThroughputSample(null, null, null);
        }

        DateTime now = DateTime.UtcNow;
        if (now >= _nextDiscoveryUtc || _activeAdapters.Any(adapter => adapter.OperationalStatus != OperationalStatus.Up))
        {
            _activeAdapters = GetEligibleAdapters();
            _nextDiscoveryUtc = now.AddSeconds(5);
        }

        if (_activeAdapters.Count == 0)
        {
            ResetBaseline();
            return new NetworkThroughputSample(null, null, null);
        }

        try
        {
            long receivedBytes = 0;
            long sentBytes = 0;
            foreach (NetworkInterface adapter in _activeAdapters)
            {
                IPv4InterfaceStatistics statistics = adapter.GetIPv4Statistics();
                receivedBytes = checked(receivedBytes + statistics.BytesReceived);
                sentBytes = checked(sentBytes + statistics.BytesSent);
            }

            string adapterKey = string.Join("|", _activeAdapters.Select(adapter => adapter.Id).OrderBy(id => id, StringComparer.Ordinal));
            string adapterSummary = string.Join(" + ", _activeAdapters.Select(adapter => adapter.Name));

            if (!_previousSampleUtc.HasValue
                || !string.Equals(adapterKey, _previousAdapterKey, StringComparison.Ordinal)
                || receivedBytes < _previousReceivedBytes
                || sentBytes < _previousSentBytes)
            {
                SetBaseline(now, receivedBytes, sentBytes, adapterKey);
                return new NetworkThroughputSample(null, null, adapterSummary);
            }

            double elapsedSeconds = (now - _previousSampleUtc.Value).TotalSeconds;
            if (elapsedSeconds < 0.25d || elapsedSeconds > 10d)
            {
                SetBaseline(now, receivedBytes, sentBytes, adapterKey);
                return new NetworkThroughputSample(null, null, adapterSummary);
            }

            double downloadMbps = (receivedBytes - _previousReceivedBytes) * 8d / elapsedSeconds / 1_000_000d;
            double uploadMbps = (sentBytes - _previousSentBytes) * 8d / elapsedSeconds / 1_000_000d;
            SetBaseline(now, receivedBytes, sentBytes, adapterKey);

            return new NetworkThroughputSample(downloadMbps, uploadMbps, adapterSummary);
        }
        catch (Exception ex)
        {
            AppLog.Write("Network throughput sample failed.", ex);
            _activeAdapters = Array.Empty<NetworkInterface>();
            _nextDiscoveryUtc = DateTime.MinValue;
            ResetBaseline();
            return new NetworkThroughputSample(null, null, null);
        }
    }

    public void ResetStatistics()
    {
        ResetBaseline();
    }

    public static IReadOnlyList<NetworkAdapterInfo> GetActiveAdapterInformation()
    {
        List<NetworkAdapterInfo> results = new();
        foreach (NetworkInterface adapter in GetEligibleAdapters())
        {
            try
            {
                IPInterfaceProperties properties = adapter.GetIPProperties();
                string ipv4 = properties.UnicastAddresses
                    .Select(address => address.Address)
                    .FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork)
                    ?.ToString() ?? "—";
                string ipv6 = properties.UnicastAddresses
                    .Select(address => address.Address)
                    .FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetworkV6 && !address.IsIPv6LinkLocal)
                    ?.ToString() ?? "—";
                string mac = FormatMacAddress(adapter.GetPhysicalAddress());
                NetworkAdapterMetadata metadata = GetAdapterMetadata(adapter.Id);
                string currentLinkSpeed = FormatLinkSpeed(adapter.Speed);
                string maximumLinkSpeed = metadata.MaximumLinkSpeed == "—"
                    ? currentLinkSpeed
                    : metadata.MaximumLinkSpeed;

                results.Add(new NetworkAdapterInfo(
                    adapter.Id,
                    adapter.Name,
                    adapter.Description,
                    FormatInterfaceType(adapter.NetworkInterfaceType),
                    currentLinkSpeed,
                    ipv4,
                    ipv6,
                    mac,
                    maximumLinkSpeed,
                    metadata.HardwareId));
            }
            catch (Exception ex)
            {
                AppLog.Write($"Unable to read network adapter information for '{adapter.Name}'.", ex);
            }
        }

        return results;
    }

    private static NetworkAdapterMetadata GetAdapterMetadata(string adapterId)
    {
        string normalizedId = NormalizeAdapterId(adapterId);
        lock (MetadataCacheLock)
        {
            if (DateTime.UtcNow >= _metadataExpiresUtc)
            {
                _metadataByAdapterId = ReadAdapterMetadata();
                _metadataExpiresUtc = DateTime.UtcNow.AddMinutes(10);
            }

            return _metadataByAdapterId.TryGetValue(normalizedId, out NetworkAdapterMetadata? metadata)
                ? metadata
                : NetworkAdapterMetadata.Unavailable;
        }
    }

    private static IReadOnlyDictionary<string, NetworkAdapterMetadata> ReadAdapterMetadata()
    {
        Dictionary<string, NetworkAdapterMetadata> metadata = new(StringComparer.OrdinalIgnoreCase);
        try
        {
            using ManagementObjectSearcher searcher = new("SELECT GUID, MaxSpeed, PNPDeviceID FROM Win32_NetworkAdapter");
            foreach (ManagementObject adapter in searcher.Get().Cast<ManagementObject>())
            {
                string id = NormalizeAdapterId(adapter["GUID"]?.ToString());
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                long maximumSpeed = ToInt64(adapter["MaxSpeed"]);
                string? hardwareId = adapter["PNPDeviceID"]?.ToString();
                metadata[id] = new NetworkAdapterMetadata(
                    FormatLinkSpeed(maximumSpeed),
                    string.IsNullOrWhiteSpace(hardwareId) ? "—" : hardwareId);
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("Network adapter metadata query failed; using managed adapter information only.", ex);
        }

        return metadata;
    }

    private static long ToInt64(object? value)
    {
        try
        {
            return value is null ? 0L : Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }
        catch
        {
            return 0L;
        }
    }

    private static string NormalizeAdapterId(string? value) => value?.Trim().Trim('{', '}') ?? string.Empty;

    private static IReadOnlyList<NetworkInterface> GetEligibleAdapters()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up)
                .Where(adapter => adapter.NetworkInterfaceType != NetworkInterfaceType.Loopback
                    && adapter.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
                .Where(adapter => !IsClearlyVirtual(adapter))
                .Where(HasUsableNetworkAddress)
                .OrderBy(adapter => adapter.NetworkInterfaceType == NetworkInterfaceType.Ethernet ? 0 : 1)
                .ThenBy(adapter => adapter.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception ex)
        {
            AppLog.Write("Network adapter discovery failed.", ex);
            return Array.Empty<NetworkInterface>();
        }
    }

    private static bool IsClearlyVirtual(NetworkInterface adapter)
    {
        string identity = $"{adapter.Name} {adapter.Description}";
        string[] excludedTerms =
        {
            "virtual", "hyper-v", "vmware", "virtualbox", "vbox", "loopback",
            "tunnel", "tap-windows", "wireguard", "tailscale", "hamachi", "bluetooth"
        };

        return excludedTerms.Any(term => identity.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasUsableNetworkAddress(NetworkInterface adapter)
    {
        try
        {
            return adapter.GetIPProperties().UnicastAddresses.Any(entry =>
                entry.Address.AddressFamily == AddressFamily.InterNetwork
                || (entry.Address.AddressFamily == AddressFamily.InterNetworkV6 && !entry.Address.IsIPv6LinkLocal));
        }
        catch
        {
            return false;
        }
    }

    private static string FormatInterfaceType(NetworkInterfaceType type)
    {
        return type switch
        {
            NetworkInterfaceType.Wireless80211 => "Wi-Fi",
            NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.FastEthernetT => "Ethernet",
            _ => type.ToString()
        };
    }

    private static string FormatLinkSpeed(long bitsPerSecond)
    {
        if (bitsPerSecond <= 0)
        {
            return "—";
        }

        double gigabits = bitsPerSecond / 1_000_000_000d;
        return gigabits >= 1d
            ? $"{gigabits:0.##} Gbps"
            : $"{bitsPerSecond / 1_000_000d:0.#} Mbps";
    }

    private static string FormatMacAddress(PhysicalAddress address)
    {
        byte[] bytes = address.GetAddressBytes();
        return bytes.Length == 0 ? "—" : string.Join("-", bytes.Select(value => value.ToString("X2")));
    }

    private sealed record NetworkAdapterMetadata(string MaximumLinkSpeed, string HardwareId)
    {
        public static NetworkAdapterMetadata Unavailable { get; } = new("—", "—");
    }

    private void SetBaseline(DateTime timestamp, long receivedBytes, long sentBytes, string adapterKey)
    {
        _previousSampleUtc = timestamp;
        _previousReceivedBytes = receivedBytes;
        _previousSentBytes = sentBytes;
        _previousAdapterKey = adapterKey;
    }

    private void ResetBaseline()
    {
        _previousSampleUtc = null;
        _previousReceivedBytes = 0;
        _previousSentBytes = 0;
        _previousAdapterKey = string.Empty;
    }

    public void Dispose()
    {
        _disposed = true;
        _activeAdapters = Array.Empty<NetworkInterface>();
        ResetBaseline();
    }
}
