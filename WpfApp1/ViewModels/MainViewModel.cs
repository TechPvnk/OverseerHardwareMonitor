using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using Overseer.Models;
using Overseer.Services;

namespace Overseer.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private const int MaxHistoryPoints = 120;

    private readonly HardwareMonitorEngine _engine;
    private readonly AudioAlertService _audioAlertService = new();
    private readonly DispatcherTimer _timer;
    private string _cpuTemperature = "Initializing...";
    private string _cpuMinTemperature = "N/A";
    private string _cpuMaxTemperature = "N/A";
    private string _cpuUsage = "N/A";
    private string _cpuPower = "N/A";
    private string _cpuMinPower = "N/A";
    private string _cpuMaxPower = "N/A";
    private string _cpuModel = "Unknown";
    private string _cpuClock = "Unknown";
    private string _cpuCoresThreads = "Unknown";
    private string _cpuCaches = "Unknown";
    private string _cpuTdp = "Unknown";
    private string _gpuTemperature = "N/A";
    private string _gpuMinTemperature = "N/A";
    private string _gpuMaxTemperature = "N/A";
    private string _gpuUsage = "N/A";
    private string _gpuPower = "N/A";
    private string _gpuMinPower = "N/A";
    private string _gpuMaxPower = "N/A";
    private string _gpuModel = "Unknown";
    private string _gpuClock = "Unknown";
    private string _gpuRam = "Unknown";
    private string _gpuBus = "Unknown";
    private string _gpuMemoryTotal = "Unknown";
    private string _gpuMemoryUsed = "Unknown";
    private string _gpuMemoryFree = "Unknown";
    private string _diskTemperature = "N/A";
    private string _diskMinTemperature = "N/A";
    private string _diskMaxTemperature = "N/A";
    private string _diskHealth = "Unknown";
    private string _ramInfo = "Unknown";
    private string _ramTotal = "Unknown";
    private string _ramType = "Unknown";
    private string _ramClock = "Unknown";
    private string _motherboard = "Unknown";
    private string _batteryInfo = "Not present";
    private string _bios = "Unknown";
    private string _osVersion = "Unknown";
    private float? _cpuMinTemperatureValue;
    private float? _cpuMaxTemperatureValue;
    private float? _cpuMinPowerValue;
    private float? _cpuMaxPowerValue;
    private float? _gpuMinTemperatureValue;
    private float? _gpuMaxTemperatureValue;
    private float? _gpuMinPowerValue;
    private float? _gpuMaxPowerValue;
    private float? _diskMinTemperatureValue;
    private float? _diskMaxTemperatureValue;
    private bool _disposed;
    private bool _useFahrenheit;
    private bool _audioAlertsEnabled = true;
    private bool _logWmiQueries = false;

    public ObservableCollection<StorageHealthSnapshot> StorageDrives { get; } = new();
    public ObservableCollection<DriveTemperatureViewModel> DriveTemperatures { get; } = new();
    public ObservableCollection<double> CpuTemperatureHistory { get; } = new();
    public ObservableCollection<double> CpuUsageHistory { get; } = new();
    public ObservableCollection<double> GpuTemperatureHistory { get; } = new();
    public ObservableCollection<double> GpuUsageHistory { get; } = new();
    public ObservableCollection<string> RamModulesList { get; } = new();

    public string CpuTemperature
    {
        get => _cpuTemperature;
        set => SetProperty(ref _cpuTemperature, value);
    }

public sealed class DriveTemperatureViewModel : INotifyPropertyChanged
{
    private string _name;
    private string _temperature = "N/A";

    public DriveTemperatureViewModel(string name)
    {
        _name = name;
        History = new ObservableCollection<double>();
    }

    public string Name => _name;

    public string Temperature
    {
        get => _temperature;
        set
        {
            if (value == _temperature) return;
            _temperature = value;
            OnPropertyChanged();
        }
    }

    public float? TemperatureValue { get; set; }

    public ObservableCollection<double> History { get; }

    // tracked numeric min/max values
    public float? MinValue;
    public float? MaxValue;

    private string _minTemperature = "N/A";
    private string _maxTemperature = "N/A";

    public string MinTemperature
    {
        get => _minTemperature;
        set
        {
            if (value == _minTemperature) return;
            _minTemperature = value;
            OnPropertyChanged();
        }
    }

    public string MaxTemperature
    {
        get => _maxTemperature;
        set
        {
            if (value == _maxTemperature) return;
            _maxTemperature = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

    public string CpuMinTemperature { get => _cpuMinTemperature; set => SetProperty(ref _cpuMinTemperature, value); }
    public string CpuMaxTemperature { get => _cpuMaxTemperature; set => SetProperty(ref _cpuMaxTemperature, value); }
    public string CpuUsage { get => _cpuUsage; set => SetProperty(ref _cpuUsage, value); }
    public string CpuPower { get => _cpuPower; set => SetProperty(ref _cpuPower, value); }
    public string CpuMinPower { get => _cpuMinPower; set => SetProperty(ref _cpuMinPower, value); }
    public string CpuMaxPower { get => _cpuMaxPower; set => SetProperty(ref _cpuMaxPower, value); }
    public string CpuModel { get => _cpuModel; set => SetProperty(ref _cpuModel, value); }
    public string CpuClock { get => _cpuClock; set => SetProperty(ref _cpuClock, value); }
    public string CpuCoresThreads { get => _cpuCoresThreads; set => SetProperty(ref _cpuCoresThreads, value); }
    public string CpuCaches { get => _cpuCaches; set => SetProperty(ref _cpuCaches, value); }
    public string CpuTdp { get => _cpuTdp; set => SetProperty(ref _cpuTdp, value); }
    public string GpuTemperature { get => _gpuTemperature; set => SetProperty(ref _gpuTemperature, value); }
    public string GpuMinTemperature { get => _gpuMinTemperature; set => SetProperty(ref _gpuMinTemperature, value); }
    public string GpuMaxTemperature { get => _gpuMaxTemperature; set => SetProperty(ref _gpuMaxTemperature, value); }
    public string GpuUsage { get => _gpuUsage; set => SetProperty(ref _gpuUsage, value); }
    public string GpuPower { get => _gpuPower; set => SetProperty(ref _gpuPower, value); }
    public string GpuMinPower { get => _gpuMinPower; set => SetProperty(ref _gpuMinPower, value); }
    public string GpuMaxPower { get => _gpuMaxPower; set => SetProperty(ref _gpuMaxPower, value); }
    public string GpuModel { get => _gpuModel; set => SetProperty(ref _gpuModel, value); }
    public string GpuClock { get => _gpuClock; set => SetProperty(ref _gpuClock, value); }
    public string GpuRam { get => _gpuRam; set => SetProperty(ref _gpuRam, value); }
    public string GpuBus { get => _gpuBus; set => SetProperty(ref _gpuBus, value); }
    public string GpuMemoryTotal { get => _gpuMemoryTotal; set => SetProperty(ref _gpuMemoryTotal, value); }
    public string GpuMemoryUsed { get => _gpuMemoryUsed; set => SetProperty(ref _gpuMemoryUsed, value); }
    public string GpuMemoryFree { get => _gpuMemoryFree; set => SetProperty(ref _gpuMemoryFree, value); }
    public string DiskTemperature { get => _diskTemperature; set => SetProperty(ref _diskTemperature, value); }
    public string DiskMinTemperature { get => _diskMinTemperature; set => SetProperty(ref _diskMinTemperature, value); }
    public string DiskMaxTemperature { get => _diskMaxTemperature; set => SetProperty(ref _diskMaxTemperature, value); }
    public string DiskHealth { get => _diskHealth; set => SetProperty(ref _diskHealth, value); }
    public string RamInfo { get => _ramInfo; set => SetProperty(ref _ramInfo, value); }
    public string RamTotal { get => _ramTotal; set => SetProperty(ref _ramTotal, value); }
    public string RamType { get => _ramType; set => SetProperty(ref _ramType, value); }
    public string RamClock { get => _ramClock; set => SetProperty(ref _ramClock, value); }
    public string Motherboard { get => _motherboard; set => SetProperty(ref _motherboard, value); }
    public string BatteryInfo { get => _batteryInfo; set => SetProperty(ref _batteryInfo, value); }

    public ObservableCollection<string> MotherboardSubHardware { get; } = new();
    public string Bios { get => _bios; set => SetProperty(ref _bios, value); }
    public string OsVersion { get => _osVersion; set => SetProperty(ref _osVersion, value); }

    public bool AudioAlertsEnabled
    {
        get => _audioAlertsEnabled;
        set => SetProperty(ref _audioAlertsEnabled, value);
    }

    public bool LogWmiQueries
    {
        get => _logWmiQueries;
        set
        {
            if (SetProperty(ref _logWmiQueries, value))
            {
                Overseer.Models.WindowsSystemInfo.LogWmiQueries = value;
            }
        }
    }
    public MainViewModel()
    {
        // Prefer the shared engine created at application startup. If it's not available,
        // create and initialize a local instance.
        if (Overseer.App.HardwareEngine != null)
        {
            _engine = Overseer.App.HardwareEngine;
        }
        else
        {
            _engine = new HardwareMonitorEngine();
            _engine.Initialize();
        }

        RefreshData();

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _timer.Tick += (s, e) => RefreshData();
        _timer.Start();
    }

    public void RefreshData()
    {
        try
        {
            HardwareSnapshot snapshot = _engine.ReadSnapshot();

            CpuModel = snapshot.CpuName;
            CpuClock = snapshot.CpuClock;
            CpuCoresThreads = snapshot.CpuCoresThreads;
            CpuCaches = snapshot.CpuCaches;
            CpuTdp = snapshot.CpuTdp;
            CpuTemperature = FormatTemperature(snapshot.CpuTemperatureValue);
            CpuUsage = snapshot.CpuUsage;
            CpuPower = snapshot.CpuPower;
            GpuModel = snapshot.GpuName;
            GpuTemperature = FormatTemperature(snapshot.GpuTemperatureValue);
            GpuClock = snapshot.GpuClock;
            GpuRam = snapshot.GpuRam;
            GpuBus = snapshot.GpuBus;
            GpuUsage = snapshot.GpuUsage;
            GpuPower = snapshot.GpuPower;
            GpuMemoryTotal = snapshot.GpuMemoryTotal;
            GpuMemoryUsed = snapshot.GpuMemoryUsed;
            GpuMemoryFree = snapshot.GpuMemoryFree;
            RamInfo = snapshot.RamInfo;
            RamTotal = snapshot.RamTotal;
            RamType = snapshot.RamType;
            RamClock = snapshot.RamClock;
            BatteryInfo = snapshot.BatteryInfo;
            Motherboard = snapshot.Motherboard;
            Bios = snapshot.Bios;
            OsVersion = snapshot.OsVersion;

            StorageDrives.Clear();
            foreach (StorageHealthSnapshot drive in snapshot.StorageDrives)
            {
                StorageDrives.Add(ConvertDriveTemperature(drive));
            }

            // Update per-drive temperature viewmodels used in the Temps card (maintain history)
            // Keep existing DriveTemperatures in sync with snapshot.StorageDrives
            foreach (StorageHealthSnapshot drive in snapshot.StorageDrives)
            {
                var existing = DriveTemperatures.FirstOrDefault(d => d.Name == drive.Name);
                if (existing == null)
                {
                    existing = new DriveTemperatureViewModel(drive.Name);
                    DriveTemperatures.Add(existing);
                }

                existing.Temperature = FormatTemperature(drive.TemperatureValue);
                existing.TemperatureValue = drive.TemperatureValue;
                AddHistoryPoint(existing.History, drive.TemperatureValue);

                // Update per-drive tracked min/max and formatted labels
                if (drive.TemperatureValue.HasValue)
                {
                    if (!existing.MinValue.HasValue || drive.TemperatureValue.Value < existing.MinValue.Value)
                    {
                        existing.MinValue = drive.TemperatureValue.Value;
                    }

                    if (!existing.MaxValue.HasValue || drive.TemperatureValue.Value > existing.MaxValue.Value)
                    {
                        existing.MaxValue = drive.TemperatureValue.Value;
                    }
                }

                existing.MinTemperature = FormatMetric(existing.MinValue, "C");
                existing.MaxTemperature = FormatMetric(existing.MaxValue, "C");
            }

            // remove any drives that no longer exist
            for (int i = DriveTemperatures.Count - 1; i >= 0; i--)
            {
                if (!snapshot.StorageDrives.Any(d => d.Name == DriveTemperatures[i].Name))
                {
                    DriveTemperatures.RemoveAt(i);
                }
            }

            // Update RAM modules list
            RamModulesList.Clear();
            foreach (var mod in snapshot.RamModules)
            {
                RamModulesList.Add(mod);
            }

            // Update motherboard sub-hardware list
            MotherboardSubHardware.Clear();
            foreach (var item in snapshot.MotherboardSubHardware)
            {
                MotherboardSubHardware.Add(item);
            }

            DiskHealth = snapshot.StorageDrives.Count == 0
                ? "Unknown"
                : string.Join(", ", snapshot.StorageDrives.Select(d => $"{d.Name}: {d.HealthStatus}"));
            DiskTemperature = snapshot.StorageDrives.Count == 0
                ? "N/A"
                : string.Join(", ", snapshot.StorageDrives.Select(d => $"{d.Name}: {FormatTemperature(d.TemperatureValue)}"));

            CpuMinTemperature = FormatTrackedMinimum(snapshot.CpuTemperatureValue, ref _cpuMinTemperatureValue, "C");
            CpuMaxTemperature = FormatTrackedMaximum(snapshot.CpuTemperatureValue, ref _cpuMaxTemperatureValue, "C");
            CpuMinPower = FormatTrackedMinimum(snapshot.CpuPowerValue, ref _cpuMinPowerValue, "W");
            CpuMaxPower = FormatTrackedMaximum(snapshot.CpuPowerValue, ref _cpuMaxPowerValue, "W");
            GpuMinTemperature = FormatTrackedMinimum(snapshot.GpuTemperatureValue, ref _gpuMinTemperatureValue, "C");
            GpuMaxTemperature = FormatTrackedMaximum(snapshot.GpuTemperatureValue, ref _gpuMaxTemperatureValue, "C");
            GpuMinPower = FormatTrackedMinimum(snapshot.GpuPowerValue, ref _gpuMinPowerValue, "W");
            GpuMaxPower = FormatTrackedMaximum(snapshot.GpuPowerValue, ref _gpuMaxPowerValue, "W");

            float? hottestDisk = snapshot.StorageDrives
                .Select(d => d.TemperatureValue)
                .Where(v => v.HasValue)
                .DefaultIfEmpty()
                .Max();
            DiskMinTemperature = FormatTrackedMinimum(hottestDisk, ref _diskMinTemperatureValue, "C");
            DiskMaxTemperature = FormatTrackedMaximum(hottestDisk, ref _diskMaxTemperatureValue, "C");

            AddHistoryPoint(CpuTemperatureHistory, snapshot.CpuTemperatureValue);
            AddHistoryPoint(CpuUsageHistory, snapshot.CpuUsageValue);
            AddHistoryPoint(GpuTemperatureHistory, snapshot.GpuTemperatureValue);
            AddHistoryPoint(GpuUsageHistory, snapshot.GpuUsageValue);
            _audioAlertService.ProcessSnapshot(snapshot, AudioAlertsEnabled);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"RefreshData failed: {ex}");
            ApplyUnavailableHardwareState();
        }
    }


    public void ResetStatistics()
    {
        _cpuMinTemperatureValue = null;
        _cpuMaxTemperatureValue = null;
        _cpuMinPowerValue = null;
        _cpuMaxPowerValue = null;
        _gpuMinTemperatureValue = null;
        _gpuMaxTemperatureValue = null;
        _gpuMinPowerValue = null;
        _gpuMaxPowerValue = null;
        _diskMinTemperatureValue = null;
        _diskMaxTemperatureValue = null;

        CpuMinTemperature = "N/A";
        CpuMaxTemperature = "N/A";
        CpuMinPower = "N/A";
        CpuMaxPower = "N/A";
        GpuMinTemperature = "N/A";
        GpuMaxTemperature = "N/A";
        GpuMinPower = "N/A";
        GpuMaxPower = "N/A";
        DiskMinTemperature = "N/A";
        DiskMaxTemperature = "N/A";
        _audioAlertService.Reset();

        CpuTemperatureHistory.Clear();
        CpuUsageHistory.Clear();
        GpuTemperatureHistory.Clear();
        GpuUsageHistory.Clear();

        RefreshData();
    }

    public void SetTemperatureUnit(bool useFahrenheit)
    {
        if (_useFahrenheit == useFahrenheit)
        {
            return;
        }

        _useFahrenheit = useFahrenheit;
        RefreshData();
    }
    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void ApplyUnavailableHardwareState()
    {
        CpuTemperature = "N/A";
        CpuUsage = "N/A";
        CpuPower = "N/A";
        GpuTemperature = "N/A";
        GpuUsage = "N/A";
        GpuPower = "N/A";
        DiskTemperature = "N/A";
        DiskHealth = "Unknown";

        StorageDrives.Clear();
        DriveTemperatures.Clear();
    }
    private static void AddHistoryPoint(ObservableCollection<double> history, float? value)
    {
        if (!value.HasValue)
        {
            return;
        }

        history.Add(value.Value);

        while (history.Count > MaxHistoryPoints)
        {
            history.RemoveAt(0);
        }
    }

    private string FormatTrackedMinimum(float? value, ref float? tracked, string unit)
    {
        if (value.HasValue && (!tracked.HasValue || value.Value < tracked.Value))
        {
            tracked = value.Value;
        }

        return FormatMetric(tracked, unit);
    }

    private string FormatTrackedMaximum(float? value, ref float? tracked, string unit)
    {
        if (value.HasValue && (!tracked.HasValue || value.Value > tracked.Value))
        {
            tracked = value.Value;
        }

        return FormatMetric(tracked, unit);
    }

    private StorageHealthSnapshot ConvertDriveTemperature(StorageHealthSnapshot drive)
    {
        return drive with { Temperature = FormatTemperature(drive.TemperatureValue) };
    }

    private string FormatTemperature(float? celsius)
    {
        if (!celsius.HasValue || float.IsNaN(celsius.Value) || float.IsInfinity(celsius.Value))
        {
            return "N/A";
        }

        double value = _useFahrenheit ? celsius.Value * 9d / 5d + 32d : celsius.Value;
        string unit = _useFahrenheit ? "F" : "C";
        return $"{value:0.#} {unit}";
    }

    private string FormatMetric(float? value, string unit)
    {
        if (unit == "C")
        {
            return FormatTemperature(value);
        }

        return value.HasValue && !float.IsNaN(value.Value) && !float.IsInfinity(value.Value) ? $"{value.Value:0.#} {unit}" : "N/A";
    }

    public void Dispose()
    {
        if (_disposed) return;

        _timer.Stop();
        _engine.Dispose();
        _disposed = true;
    }
}
