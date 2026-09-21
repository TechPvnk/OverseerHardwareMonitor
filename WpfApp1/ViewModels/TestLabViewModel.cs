using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using Overseer.Models;
using Overseer.Services;
using Overseer.Services.Testing;

namespace Overseer.ViewModels;

public sealed record TestLabThreadOption(int Count, string Display);
public sealed record TestLabDurationOption(TimeSpan? Duration, string Display);

public sealed class TestLabViewModel : INotifyPropertyChanged, IDisposable
{
    private const int MaxHistoryPoints = 120;
    private readonly TestCoordinator _coordinator = new();
    private CpuTestSession? _session;
    private DateTime _startedUtc;
    private TestLabThreadOption? _selectedThreadOption;
    private TestLabDurationOption? _selectedDurationOption;
    private CpuBenchmarkState _benchmarkState;
    private CpuStressState _stressState;
    private bool _thermalSafetyEnabled = true;
    private bool _thermalSafetyAvailable;
    private int _criticalSamples;
    private CpuTestStopReason _requestedStopReason = CpuTestStopReason.UserStopped;
    private string _cpuModel = "—";
    private string _cpuCoresThreads = "—";
    private string _currentTemperature = "—";
    private string _currentUsage = "—";
    private string _currentPower = "—";
    private string _stressStatus = "";
    private string _benchmarkStatus = "";
    private string _elapsed = "00:00";
    private string _singleScore = "—";
    private string _multiScore = "—";
    private string _ratio = "—";
    private string _lastBenchmarkSummary = "";
    private string _lastStressSummary = "";
    private bool _disposed;
    private bool _useFahrenheit;

    public TestLabViewModel()
    {
        RebuildOptions();
        LocalizationService.Instance.PropertyChanged += LocalizationChanged;
    }

    public ObservableCollection<TestLabThreadOption> ThreadOptions { get; } = new();
    public ObservableCollection<TestLabDurationOption> DurationOptions { get; } = new();
    public ObservableCollection<double> TemperatureHistory { get; } = new();
    public ObservableCollection<double> UsageHistory { get; } = new();
    public string BenchmarkVersion => CpuBenchmarkService.BenchmarkVersion;
    public string CpuModel { get => _cpuModel; private set => SetProperty(ref _cpuModel, value); }
    public string CpuCoresThreads { get => _cpuCoresThreads; private set => SetProperty(ref _cpuCoresThreads, value); }
    public TestLabThreadOption? SelectedThreadOption { get => _selectedThreadOption; set => SetProperty(ref _selectedThreadOption, value); }
    public TestLabDurationOption? SelectedDurationOption { get => _selectedDurationOption; set => SetProperty(ref _selectedDurationOption, value); }
    public CpuBenchmarkState BenchmarkState { get => _benchmarkState; private set { if (SetProperty(ref _benchmarkState, value)) { BenchmarkStatus = LocalizedState(value); OnPropertyChanged(nameof(IsTestRunning)); } } }
    public CpuStressState StressState { get => _stressState; private set { if (SetProperty(ref _stressState, value)) { StressStatus = LocalizedState(value); OnPropertyChanged(nameof(IsStressRunning)); OnPropertyChanged(nameof(IsTestRunning)); } } }
    public bool IsStressRunning => StressState is CpuStressState.Preparing or CpuStressState.Running or CpuStressState.Stopping;
    public bool IsTestRunning => _coordinator.IsRunning;
    public bool ThermalSafetyEnabled { get => _thermalSafetyEnabled; set => SetProperty(ref _thermalSafetyEnabled, value); }
    public bool ThermalSafetyAvailable { get => _thermalSafetyAvailable; private set => SetProperty(ref _thermalSafetyAvailable, value); }
    public string CurrentTemperature { get => _currentTemperature; private set => SetProperty(ref _currentTemperature, value); }
    public string CurrentUsage { get => _currentUsage; private set => SetProperty(ref _currentUsage, value); }
    public string CurrentPower { get => _currentPower; private set => SetProperty(ref _currentPower, value); }
    public string BenchmarkStatus { get => _benchmarkStatus; private set => SetProperty(ref _benchmarkStatus, value); }
    public string StressStatus { get => _stressStatus; private set => SetProperty(ref _stressStatus, value); }
    public string Elapsed { get => _elapsed; private set => SetProperty(ref _elapsed, value); }
    public string SingleScore { get => _singleScore; private set => SetProperty(ref _singleScore, value); }
    public string MultiScore { get => _multiScore; private set => SetProperty(ref _multiScore, value); }
    public string Ratio { get => _ratio; private set => SetProperty(ref _ratio, value); }
    public string LastBenchmarkSummary { get => _lastBenchmarkSummary; private set => SetProperty(ref _lastBenchmarkSummary, value); }
    public string LastStressSummary { get => _lastStressSummary; private set => SetProperty(ref _lastStressSummary, value); }
    public bool IsBatteryPower { get; private set; }
    public bool ShouldShowStressWarning => !TestLabSettingsService.Instance.SuppressStressWarning;
    public bool SuppressStressWarningPreference
    {
        get => TestLabSettingsService.Instance.SuppressStressWarning;
        set { TestLabSettingsService.Instance.SetSuppressStressWarning(value); OnPropertyChanged(nameof(ShouldShowStressWarning)); }
    }
    public string PersistentStressText => IsStressRunning ? $"{L("TestLabCpuStressRunning")} • {CurrentTemperature} • {CurrentUsage} • {Elapsed}" : string.Empty;

    public void ObserveTelemetry(HardwareSnapshot snapshot, TemperatureStatus cpuStatus, bool useFahrenheit)
    {
        _useFahrenheit = useFahrenheit;
        CpuModel = snapshot.CpuName; CpuCoresThreads = snapshot.CpuCoresThreads;
        IsBatteryPower = snapshot.BatteryInfo.Contains("AC: Offline", StringComparison.OrdinalIgnoreCase);
        bool temperatureAvailable = TemperatureStatusService.IsAvailableTemperature(snapshot.CpuTemperatureValue);
        ThermalSafetyAvailable = temperatureAvailable;
        CurrentTemperature = FormatTemperature(snapshot.CpuTemperatureValue);
        CurrentUsage = FormatValue(snapshot.CpuUsageValue, "%"); CurrentPower = FormatValue(snapshot.CpuPowerValue, "W");
        if (_session is null) return;
        _session.AddTelemetry(snapshot.CpuTemperatureValue, snapshot.CpuUsageValue, snapshot.CpuPowerValue);
        AddHistory(TemperatureHistory, snapshot.CpuTemperatureValue, true); AddHistory(UsageHistory, snapshot.CpuUsageValue, false);
        Elapsed = (DateTime.UtcNow - _startedUtc).ToString(@"mm\:ss", CultureInfo.InvariantCulture);
        OnPropertyChanged(nameof(PersistentStressText));
        if (IsStressRunning && ThermalSafetyEnabled && ThermalSafetyAvailable)
        {
            _criticalSamples = cpuStatus.State == TemperatureStatusKind.Critical ? _criticalSamples + 1 : 0;
            if (_criticalSamples >= 3) _ = StopAsync(CpuTestStopReason.CriticalTemperature);
        }
        if (IsStressRunning && SelectedDurationOption?.Duration is TimeSpan duration && DateTime.UtcNow - _startedUtc >= duration)
        {
            _ = StopAsync(CpuTestStopReason.Completed);
        }
    }

    public async Task StartBenchmarkAsync()
    {
        if (IsTestRunning) return;
        StartSession(); SingleScore = MultiScore = Ratio = "—";
        CpuBenchmarkResult result = await _coordinator.StartBenchmarkAsync(SelectedThreadOption?.Count ?? Environment.ProcessorCount, UpdateBenchmarkState);
        if (result.StopReason == CpuTestStopReason.Completed && result.SingleThread is not null && result.MultiThread is not null)
        {
            SingleScore = $"{result.SingleThread.Score:N0} pts"; MultiScore = $"{result.MultiThread.Score:N0} pts";
            Ratio = result.SingleThread.Score > 0 ? $"{(double)result.MultiThread.Score / result.SingleThread.Score:0.00}x" : "—";
            LastBenchmarkSummary = $"{BenchmarkVersion}\n{L("TestLabSingleThread")}: {SingleScore}    {L("TestLabMultiThread")}: {MultiScore}\n{L("TestLabMultiThreadRatio")}: {Ratio}\n{L("TestLabPeakTemperature")}: {FormatTemperature(_session?.Temperature.Maximum)}    {L("TestLabPeakPower")}: {FormatValue(_session?.Power.Maximum, "W")}";
        }
        else LastBenchmarkSummary = result.StopReason == CpuTestStopReason.Cancelled ? L("TestLabCancelled") : $"{L("TestLabError")}: {result.Error ?? L("TestLabCancelled")}";
        _session = null; OnPropertyChanged(nameof(IsTestRunning));
    }

    public async Task StartStressAsync()
    {
        if (IsTestRunning) return;
        StartSession(); _requestedStopReason = CpuTestStopReason.UserStopped;
        CpuStressResult result = await _coordinator.StartStressAsync(SelectedThreadOption?.Count ?? Environment.ProcessorCount, UpdateStressState);
        CpuTestStopReason reason = _requestedStopReason is CpuTestStopReason.CriticalTemperature or CpuTestStopReason.Completed ? _requestedStopReason : result.StopReason;
        string status = reason switch { CpuTestStopReason.Completed => L("TestLabCompleted"), CpuTestStopReason.CriticalTemperature => L("TestLabStoppedCritical"), CpuTestStopReason.Error => L("TestLabError"), _ => L("TestLabStopped") };
        LastStressSummary = $"{CpuModel}\n{L("TestLabDuration")}: {Elapsed}    {L("TestLabThreads")}: {SelectedThreadOption?.Count}\n{L("TestLabAverage")} {L("LabelUsage")}: {FormatDouble(_session?.Usage.Average, "%")}\n{L("LabelTemperature")}: {L("TestLabAverage")} {FormatDouble(_session?.Temperature.Average, "C", true)}    {L("LabelMax")} {FormatTemperature(_session?.Temperature.Maximum)}\n{L("LabelPower")}: {L("TestLabAverage")} {FormatDouble(_session?.Power.Average, "W")}    {L("LabelMax")} {FormatValue(_session?.Power.Maximum, "W")}\n{L("TestLabResult")}: {status}";
        StressState = reason == CpuTestStopReason.CriticalTemperature ? CpuStressState.StoppedThermal : CpuStressState.Completed;
        _session = null; OnPropertyChanged(nameof(IsTestRunning)); OnPropertyChanged(nameof(PersistentStressText));
    }

    public async Task StopAsync(CpuTestStopReason reason = CpuTestStopReason.UserStopped)
    {
        if (!IsTestRunning) return;
        _requestedStopReason = reason; if (IsStressRunning) StressState = CpuStressState.Stopping;
        await _coordinator.StopAsync();
    }
    public void SuppressStressWarning() => TestLabSettingsService.Instance.SetSuppressStressWarning(true);

    private void StartSession() { _session = new CpuTestSession(DateTime.UtcNow); _startedUtc = DateTime.UtcNow; _criticalSamples = 0; TemperatureHistory.Clear(); UsageHistory.Clear(); Elapsed = "00:00"; }
    private void UpdateBenchmarkState(CpuBenchmarkState state) => Application.Current.Dispatcher.BeginInvoke(() => BenchmarkState = state);
    private void UpdateStressState(CpuStressState state) => Application.Current.Dispatcher.BeginInvoke(() => StressState = state);
    private static void AddHistory(ObservableCollection<double> history, float? value, bool temp)
    { if ((temp && !TemperatureStatusService.IsAvailableTemperature(value)) || (!temp && (!value.HasValue || float.IsNaN(value.Value) || float.IsInfinity(value.Value)))) return; history.Add(value!.Value); while (history.Count > MaxHistoryPoints) history.RemoveAt(0); }
    private string FormatTemperature(float? value) => !TemperatureStatusService.IsAvailableTemperature(value) ? "—" : $"{(_useFahrenheit ? value!.Value * 9d / 5d + 32d : value!.Value):0.#} {(_useFahrenheit ? "F" : "C")}";
    private static string FormatValue(float? value, string unit) => value.HasValue && !float.IsNaN(value.Value) && !float.IsInfinity(value.Value) ? $"{value.Value:0.#} {unit}" : "—";
    private string FormatDouble(double? value, string unit, bool temperature = false) => !value.HasValue || double.IsNaN(value.Value) ? "—" : temperature ? FormatTemperature((float)value.Value) : $"{value.Value:0.#} {unit}";
    private void RebuildOptions()
    {
        int oldThreads = SelectedThreadOption?.Count ?? Environment.ProcessorCount; TimeSpan? oldDuration = SelectedDurationOption?.Duration ?? TimeSpan.FromMinutes(5);
        ThreadOptions.Clear(); ThreadOptions.Add(new(Environment.ProcessorCount, $"{L("TestLabAllThreads")} ({Environment.ProcessorCount}T)")); for (int i = 1; i <= Environment.ProcessorCount; i++) ThreadOptions.Add(new(i, $"{i} {L("TestLabThreads")}"));
        DurationOptions.Clear(); DurationOptions.Add(new(TimeSpan.FromMinutes(1), L("TestLabOneMinute"))); DurationOptions.Add(new(TimeSpan.FromMinutes(5), L("TestLabFiveMinutes"))); DurationOptions.Add(new(TimeSpan.FromMinutes(10), L("TestLabTenMinutes"))); DurationOptions.Add(new(TimeSpan.FromMinutes(30), L("TestLabThirtyMinutes"))); DurationOptions.Add(new(null, L("TestLabUntilStopped")));
        SelectedThreadOption = ThreadOptions[System.Math.Clamp(oldThreads == Environment.ProcessorCount ? 0 : oldThreads, 0, ThreadOptions.Count - 1)]; SelectedDurationOption = DurationOptions.FirstOrDefault(option => option.Duration == oldDuration) ?? DurationOptions[1];
    }
    private static string L(string key) => LocalizationService.Instance[key];
    private string LocalizedState(object state) => state.ToString() switch { "Preparing" => L("TestLabPreparing"), "Running" or "RunningSingle" or "RunningMulti" => L("TestLabRunning"), "WarmingUpSingle" or "WarmingUpMulti" => L("TestLabWarmingUp"), "Completed" => L("TestLabCompleted"), "Cancelled" => L("TestLabCancelled"), "StoppedThermal" => L("TestLabStoppedCritical"), _ => string.Empty };
    private void LocalizationChanged(object? sender, PropertyChangedEventArgs e) { if (e.PropertyName is "Item[]" or nameof(LocalizationService.Culture)) RebuildOptions(); }
    public void Dispose() { if (_disposed) return; _disposed = true; LocalizationService.Instance.PropertyChanged -= LocalizationChanged; _coordinator.Dispose(); }
    public event PropertyChangedEventHandler? PropertyChanged;
    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? name = null) { if (Equals(field, value)) return false; field = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name)); return true; }
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
