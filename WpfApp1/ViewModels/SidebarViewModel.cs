using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using Overseer.Models;
using Overseer.Services;
using System.Windows.Threading;

namespace Overseer.ViewModels;

public sealed class SidebarViewModel : INotifyPropertyChanged, IDisposable
{
    private const int MaxHistoryPoints = 120;
    private readonly SidebarSettingsService _settingsService;
    private readonly NetworkMonitorService _networkMonitorService = new();
    private FrameMonitorService? _frameMonitorService;
    private readonly DispatcherTimer _networkTimer;
    private MainViewModel.DriveTemperatureViewModel? _selectedDrive;
    private float? _cpuMinTemperature;
    private float? _cpuMaxTemperature;
    private float? _cpuMinUsage;
    private float? _cpuMaxUsage;
    private float? _cpuMinPower;
    private float? _cpuMaxPower;
    private float? _gpuMinTemperature;
    private float? _gpuMaxTemperature;
    private float? _gpuMinUsage;
    private float? _gpuMaxUsage;
    private float? _gpuMinPower;
    private float? _gpuMaxPower;
    private float? _ramMinUsage;
    private float? _ramMaxUsage;
    private float? _ramMinTemperature;
    private float? _ramMaxTemperature;
    private double? _networkDownloadMbps;
    private double? _networkUploadMbps;
    private double? _networkMinDownloadMbps;
    private double? _networkMaxDownloadMbps;
    private double? _networkMinUploadMbps;
    private double? _networkMaxUploadMbps;
    private string? _networkAdapterSummary;
    private FrameMonitorSnapshot _frameSnapshot = new(
        FrameMonitorAvailability.Initializing, null, null, null, null, null, Array.Empty<double>());
    private bool _disposed;

    public SidebarViewModel(MainViewModel telemetry, SidebarSettingsService settingsService)
    {
        Telemetry = telemetry;
        _settingsService = settingsService;
        Telemetry.DriveTemperatures.CollectionChanged += DriveTemperaturesChanged;
        Telemetry.PropertyChanged += TelemetryPropertyChanged;
        Telemetry.StatisticsReset += TelemetryStatisticsReset;
        LocalizationService.Instance.PropertyChanged += LocalizationPropertyChanged;
        SelectConfiguredDrive();
        UpdateStatistics(Telemetry.LatestSnapshot);
        if (ShowFps)
        {
            _frameMonitorService = new FrameMonitorService();
        }
        _networkTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _networkTimer.Tick += NetworkTimerTick;
        if (ShowNetwork)
        {
            SampleNetwork();
        }
        if (ShowFps)
        {
            SampleFrames();
        }
        _networkTimer.Start();
    }

    public MainViewModel Telemetry { get; }
    public ObservableCollection<double> NetworkDownloadHistory { get; } = new();
    public ObservableCollection<double> NetworkUploadHistory { get; } = new();
    public ObservableCollection<double> FrameTimeHistory { get; } = new();

    public MainViewModel.DriveTemperatureViewModel? SelectedDrive
    {
        get => _selectedDrive;
        private set
        {
            if (ReferenceEquals(_selectedDrive, value))
            {
                return;
            }

            _selectedDrive = value;
            OnPropertyChanged();
        }
    }

    public bool IsAlwaysOnTop
    {
        get => _settingsService.Settings.IsAlwaysOnTop;
        set
        {
            if (_settingsService.Settings.IsAlwaysOnTop == value)
            {
                return;
            }

            _settingsService.Settings.IsAlwaysOnTop = value;
            _settingsService.Save();
            OnPropertyChanged();
        }
    }

    public bool IsClickThrough
    {
        get => _settingsService.Settings.IsClickThrough;
        set
        {
            if (_settingsService.Settings.IsClickThrough == value)
            {
                return;
            }

            _settingsService.Settings.IsClickThrough = value;
            _settingsService.Save();
            OnPropertyChanged();
        }
    }

    public bool ShowMinMax
    {
        get => _settingsService.Settings.ShowMinMax;
        set
        {
            if (_settingsService.Settings.ShowMinMax == value)
            {
                return;
            }

            _settingsService.Settings.ShowMinMax = value;
            _settingsService.Save();
            OnPropertyChanged();
        }
    }

    public double BackgroundOpacity
    {
        get => _settingsService.Settings.BackgroundOpacity;
        set
        {
            double opacity = Math.Clamp(value, 0.4d, 1d);
            if (Math.Abs(_settingsService.Settings.BackgroundOpacity - opacity) < 0.001d)
            {
                return;
            }

            _settingsService.Settings.BackgroundOpacity = opacity;
            _settingsService.Save();
            OnPropertyChanged();
            OnPropertyChanged(nameof(BackgroundOpacityPercent));
        }
    }

    public string BackgroundOpacityPercent => $"{BackgroundOpacity:P0}";

    public SidebarDockEdge DockEdge
    {
        get => _settingsService.Settings.DockEdge;
        set
        {
            if (_settingsService.Settings.DockEdge == value)
            {
                return;
            }

            _settingsService.Settings.DockEdge = value;
            _settingsService.Save();
            OnPropertyChanged();
        }
    }

    public bool ShowFps
    {
        get => _settingsService.Settings.ShowFps;
        set
        {
            if (_settingsService.Settings.ShowFps == value)
            {
                return;
            }

            _settingsService.Settings.ShowFps = value;
            if (value)
            {
                _frameMonitorService = new FrameMonitorService();
                SampleFrames();
            }
            else
            {
                _frameMonitorService?.Dispose();
                _frameMonitorService = null;
                FrameTimeHistory.Clear();
            }

            SaveModuleVisibility(nameof(ShowFps));
        }
    }

    public bool ShowCpu
    {
        get => _settingsService.Settings.ShowCpu;
        set => SetModuleVisibility(_settingsService.Settings.ShowCpu, value, () => _settingsService.Settings.ShowCpu = value, nameof(ShowCpu));
    }

    public bool ShowGpu
    {
        get => _settingsService.Settings.ShowGpu;
        set => SetModuleVisibility(_settingsService.Settings.ShowGpu, value, () => _settingsService.Settings.ShowGpu = value, nameof(ShowGpu));
    }

    public bool ShowRam
    {
        get => _settingsService.Settings.ShowRam;
        set => SetModuleVisibility(_settingsService.Settings.ShowRam, value, () => _settingsService.Settings.ShowRam = value, nameof(ShowRam));
    }

    public bool ShowDrives
    {
        get => _settingsService.Settings.ShowDrives;
        set => SetModuleVisibility(_settingsService.Settings.ShowDrives, value, () => _settingsService.Settings.ShowDrives = value, nameof(ShowDrives));
    }

    public bool ShowNetwork
    {
        get => _settingsService.Settings.ShowNetwork;
        set
        {
            if (_settingsService.Settings.ShowNetwork == value)
            {
                return;
            }

            _settingsService.Settings.ShowNetwork = value;
            if (value)
            {
                _networkMonitorService.ResetStatistics();
                SampleNetwork();
            }
            SaveModuleVisibility(nameof(ShowNetwork));
        }
    }

    public int VisibleModuleCount =>
        (ShowFps ? 1 : 0) + (ShowCpu ? 1 : 0) + (ShowGpu ? 1 : 0)
        + (ShowRam ? 1 : 0) + (ShowDrives ? 1 : 0) + (ShowNetwork ? 1 : 0);

    public string CpuTemperatureRange => FormatRange(_cpuMinTemperature, _cpuMaxTemperature, "C", true);
    public string CpuUsageRange => FormatRange(_cpuMinUsage, _cpuMaxUsage, "%");
    public string CpuPowerRange => FormatRange(_cpuMinPower, _cpuMaxPower, "W");
    public string GpuTemperatureRange => FormatRange(_gpuMinTemperature, _gpuMaxTemperature, "C", true);
    public string GpuUsageRange => FormatRange(_gpuMinUsage, _gpuMaxUsage, "%");
    public string GpuPowerRange => FormatRange(_gpuMinPower, _gpuMaxPower, "W");
    public string RamUsageRange => FormatRange(_ramMinUsage, _ramMaxUsage, "%");
    public string RamTemperatureRange => FormatRange(_ramMinTemperature, _ramMaxTemperature, "C", true);
    public string NetworkDownload => FormatThroughput(_networkDownloadMbps);
    public string NetworkUpload => FormatThroughput(_networkUploadMbps);
    public string NetworkDownloadRange => FormatThroughputRange(_networkMinDownloadMbps, _networkMaxDownloadMbps);
    public string NetworkUploadRange => FormatThroughputRange(_networkMinUploadMbps, _networkMaxUploadMbps);
    public double NetworkGraphMaximum => Math.Max(1d, Math.Max(_networkMaxDownloadMbps ?? 0d, _networkMaxUploadMbps ?? 0d));
    public string? NetworkAdapterSummary => _networkAdapterSummary;
    public bool HasActiveNetworkAdapter => !string.IsNullOrWhiteSpace(_networkAdapterSummary);
    public string CurrentFps => FormatFps(_frameSnapshot.CurrentFps);
    public string AverageFps => FormatFps(_frameSnapshot.AverageFps);
    public string OnePercentLowFps => FormatFps(_frameSnapshot.OnePercentLowFps);
    public string HighFps => FormatFps(_frameSnapshot.HighFps);
    public string FpsTargetOrStatus => _frameSnapshot.TargetApplication is null
        ? GetFrameStatusText(_frameSnapshot.Availability)
        : _frameSnapshot.CurrentFps.HasValue
            ? _frameSnapshot.TargetApplication
            : $"{_frameSnapshot.TargetApplication} // {LocalizationService.Instance["FpsNoNewFrames"]}";
    public double FrameTimeGraphMaximum => Math.Max(16.67d, FrameTimeHistory.Count > 0 ? FrameTimeHistory.Max() : 0d);

    public void SelectDrive(MainViewModel.DriveTemperatureViewModel drive)
    {
        if (!Telemetry.DriveTemperatures.Contains(drive))
        {
            return;
        }

        SelectedDrive = drive;
        _settingsService.Settings.SelectedDriveName = drive.Name;
        _settingsService.Save();
    }

    private void SetModuleVisibility(bool currentValue, bool newValue, Action assign, string propertyName)
    {
        if (currentValue == newValue)
        {
            return;
        }

        assign();
        SaveModuleVisibility(propertyName);
    }

    private void SaveModuleVisibility(string propertyName)
    {
        _settingsService.Save();
        OnPropertyChanged(propertyName);
        OnPropertyChanged(nameof(VisibleModuleCount));
    }

    private void DriveTemperaturesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        SelectConfiguredDrive();
    }

    private void TelemetryPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.LatestSnapshot))
        {
            UpdateStatistics(Telemetry.LatestSnapshot);
        }
        else if (e.PropertyName == nameof(MainViewModel.UseFahrenheit))
        {
            NotifyRangeProperties();
        }
    }

    private void TelemetryStatisticsReset(object? sender, EventArgs e)
    {
        _cpuMinTemperature = null;
        _cpuMaxTemperature = null;
        _cpuMinUsage = null;
        _cpuMaxUsage = null;
        _cpuMinPower = null;
        _cpuMaxPower = null;
        _gpuMinTemperature = null;
        _gpuMaxTemperature = null;
        _gpuMinUsage = null;
        _gpuMaxUsage = null;
        _gpuMinPower = null;
        _gpuMaxPower = null;
        _ramMinUsage = null;
        _ramMaxUsage = null;
        _ramMinTemperature = null;
        _ramMaxTemperature = null;
        _networkMinDownloadMbps = null;
        _networkMaxDownloadMbps = null;
        _networkMinUploadMbps = null;
        _networkMaxUploadMbps = null;
        NetworkDownloadHistory.Clear();
        NetworkUploadHistory.Clear();
        _networkMonitorService.ResetStatistics();
        _frameMonitorService?.ResetStatistics();
        FrameTimeHistory.Clear();
        if (ShowFps)
        {
            SampleFrames();
        }
        NotifyRangeProperties();
        OnPropertyChanged(nameof(NetworkDownloadRange));
        OnPropertyChanged(nameof(NetworkUploadRange));
    }

    private void UpdateStatistics(HardwareSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return;
        }

        TrackTemperature(snapshot.CpuTemperatureValue, ref _cpuMinTemperature, ref _cpuMaxTemperature);
        TrackMetric(snapshot.CpuUsageValue, ref _cpuMinUsage, ref _cpuMaxUsage);
        TrackMetric(snapshot.CpuPowerValue, ref _cpuMinPower, ref _cpuMaxPower);
        TrackTemperature(snapshot.GpuTemperatureValue, ref _gpuMinTemperature, ref _gpuMaxTemperature);
        TrackMetric(snapshot.GpuUsageValue, ref _gpuMinUsage, ref _gpuMaxUsage);
        TrackMetric(snapshot.GpuPowerValue, ref _gpuMinPower, ref _gpuMaxPower);
        TrackMetric(snapshot.RamUsageValue, ref _ramMinUsage, ref _ramMaxUsage);
        TrackTemperature(snapshot.RamTemperatureValue, ref _ramMinTemperature, ref _ramMaxTemperature);
        NotifyRangeProperties();
    }

    private void NotifyRangeProperties()
    {
        OnPropertyChanged(nameof(CpuTemperatureRange));
        OnPropertyChanged(nameof(CpuUsageRange));
        OnPropertyChanged(nameof(CpuPowerRange));
        OnPropertyChanged(nameof(GpuTemperatureRange));
        OnPropertyChanged(nameof(GpuUsageRange));
        OnPropertyChanged(nameof(GpuPowerRange));
        OnPropertyChanged(nameof(RamUsageRange));
        OnPropertyChanged(nameof(RamTemperatureRange));
    }

    private static void TrackTemperature(float? value, ref float? minimum, ref float? maximum)
    {
        if (!TemperatureStatusService.IsAvailableTemperature(value))
        {
            return;
        }

        TrackMetric(value, ref minimum, ref maximum);
    }

    private static void TrackMetric(float? value, ref float? minimum, ref float? maximum)
    {
        if (!value.HasValue || float.IsNaN(value.Value) || float.IsInfinity(value.Value))
        {
            return;
        }

        minimum = !minimum.HasValue || value.Value < minimum.Value ? value.Value : minimum;
        maximum = !maximum.HasValue || value.Value > maximum.Value ? value.Value : maximum;
    }

    private string FormatRange(float? minimum, float? maximum, string unit, bool temperature = false)
    {
        if (!minimum.HasValue || !maximum.HasValue)
        {
            return "—";
        }

        if (temperature)
        {
            double min = Telemetry.UseFahrenheit ? minimum.Value * 9d / 5d + 32d : minimum.Value;
            double max = Telemetry.UseFahrenheit ? maximum.Value * 9d / 5d + 32d : maximum.Value;
            string temperatureUnit = Telemetry.UseFahrenheit ? "F" : "C";
            return $"{min:0.#}–{max:0.#} {temperatureUnit}";
        }

        return $"{minimum.Value:0.#}–{maximum.Value:0.#} {unit}";
    }

    private void NetworkTimerTick(object? sender, EventArgs e)
    {
        if (ShowNetwork)
        {
            SampleNetwork();
        }
        if (ShowFps)
        {
            SampleFrames();
        }
    }

    private void LocalizationPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == "Item[]")
        {
            OnPropertyChanged(nameof(FpsTargetOrStatus));
        }
    }

    private void SampleFrames()
    {
        if (_frameMonitorService is null)
        {
            return;
        }

        _frameSnapshot = _frameMonitorService.GetSnapshot();
        FrameTimeHistory.Clear();
        foreach (double frameTime in _frameSnapshot.RecentFrameTimes)
        {
            FrameTimeHistory.Add(frameTime);
        }

        OnPropertyChanged(nameof(CurrentFps));
        OnPropertyChanged(nameof(AverageFps));
        OnPropertyChanged(nameof(OnePercentLowFps));
        OnPropertyChanged(nameof(HighFps));
        OnPropertyChanged(nameof(FpsTargetOrStatus));
        OnPropertyChanged(nameof(FrameTimeGraphMaximum));
    }

    private static string FormatFps(double? fps, bool includeUnit = false)
    {
        if (!fps.HasValue || double.IsNaN(fps.Value) || double.IsInfinity(fps.Value) || fps.Value <= 0d)
        {
            return "—";
        }

        string value = fps.Value >= 100d ? fps.Value.ToString("0") : fps.Value.ToString("0.0");
        return includeUnit ? $"{value} FPS" : value;
    }

    private static string GetFrameStatusText(FrameMonitorAvailability availability)
    {
        string key = availability switch
        {
            FrameMonitorAvailability.Initializing => "FpsInitializing",
            FrameMonitorAvailability.MissingExecutable => "FpsPresentMonMissing",
            FrameMonitorAvailability.AccessDenied => "FpsAccessDenied",
            FrameMonitorAvailability.Failed => "FpsMonitorUnavailable",
            _ => "FpsNoActiveApplication"
        };
        return LocalizationService.Instance[key];
    }

    private void SampleNetwork()
    {
        NetworkThroughputSample sample = _networkMonitorService.Sample();
        _networkAdapterSummary = sample.AdapterSummary;
        _networkDownloadMbps = sample.DownloadMegabitsPerSecond;
        _networkUploadMbps = sample.UploadMegabitsPerSecond;

        TrackNetworkMetric(_networkDownloadMbps, ref _networkMinDownloadMbps, ref _networkMaxDownloadMbps, NetworkDownloadHistory);
        TrackNetworkMetric(_networkUploadMbps, ref _networkMinUploadMbps, ref _networkMaxUploadMbps, NetworkUploadHistory);

        OnPropertyChanged(nameof(NetworkDownload));
        OnPropertyChanged(nameof(NetworkUpload));
        OnPropertyChanged(nameof(NetworkDownloadRange));
        OnPropertyChanged(nameof(NetworkUploadRange));
        OnPropertyChanged(nameof(NetworkGraphMaximum));
        OnPropertyChanged(nameof(NetworkAdapterSummary));
        OnPropertyChanged(nameof(HasActiveNetworkAdapter));
    }

    private static void TrackNetworkMetric(double? value, ref double? minimum, ref double? maximum, ObservableCollection<double> history)
    {
        if (!value.HasValue || double.IsNaN(value.Value) || double.IsInfinity(value.Value) || value.Value < 0d)
        {
            return;
        }

        minimum = !minimum.HasValue || value.Value < minimum.Value ? value.Value : minimum;
        maximum = !maximum.HasValue || value.Value > maximum.Value ? value.Value : maximum;
        history.Add(value.Value);
        while (history.Count > MaxHistoryPoints)
        {
            history.RemoveAt(0);
        }
    }

    private static string FormatThroughput(double? megabitsPerSecond)
    {
        if (!megabitsPerSecond.HasValue)
        {
            return "—";
        }

        return megabitsPerSecond.Value < 1d
            ? $"{megabitsPerSecond.Value:0.00} Mbps"
            : $"{megabitsPerSecond.Value:0.0} Mbps";
    }

    private static string FormatThroughputRange(double? minimum, double? maximum)
    {
        return minimum.HasValue && maximum.HasValue
            ? $"{minimum.Value:0.##}–{maximum.Value:0.##} Mbps"
            : "—";
    }

    private void SelectConfiguredDrive()
    {
        string? configuredName = _settingsService.Settings.SelectedDriveName;
        MainViewModel.DriveTemperatureViewModel? drive = Telemetry.DriveTemperatures.FirstOrDefault(candidate =>
            !string.IsNullOrWhiteSpace(configuredName)
            && string.Equals(candidate.Name, configuredName, StringComparison.OrdinalIgnoreCase));

        drive ??= Telemetry.DriveTemperatures.FirstOrDefault();
        SelectedDrive = drive;

        if (drive is not null && !string.Equals(configuredName, drive.Name, StringComparison.OrdinalIgnoreCase))
        {
            _settingsService.Settings.SelectedDriveName = drive.Name;
            _settingsService.Save();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Telemetry.DriveTemperatures.CollectionChanged -= DriveTemperaturesChanged;
        Telemetry.PropertyChanged -= TelemetryPropertyChanged;
        Telemetry.StatisticsReset -= TelemetryStatisticsReset;
        LocalizationService.Instance.PropertyChanged -= LocalizationPropertyChanged;
        _networkTimer.Stop();
        _networkTimer.Tick -= NetworkTimerTick;
        _networkMonitorService.Dispose();
        _frameMonitorService?.Dispose();
        _frameMonitorService = null;
        _disposed = true;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
