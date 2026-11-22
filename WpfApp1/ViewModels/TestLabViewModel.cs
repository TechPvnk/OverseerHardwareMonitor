using System;
using System.Collections.Generic;
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
public sealed record TestLabResultRow(string Label, string Value);

public sealed class TestLabViewModel : INotifyPropertyChanged, IDisposable
{
    private sealed record BenchmarkRun(CpuBenchmarkResult Result, CpuTestSession? Telemetry);
    private sealed record StressRun(CpuStressResult Result, CpuTestStopReason Reason, CpuTestSession? Telemetry);

    private const int MaxHistoryPoints = 120;
    private const string NoValue = "\u2014";
    private readonly TestCoordinator _coordinator = new();
    private CpuTestSession? _session;
    private DateTime _startedUtc;
    private TestLabThreadOption? _selectedThreadOption;
    private TestLabDurationOption? _selectedDurationOption;
    private CpuBenchmarkState _benchmarkState;
    private CpuStressState _stressState;
    private CpuTestKind _activeTest;
    private bool _isStopping;
    private TimeSpan? _stressDuration;
    private BenchmarkRun? _lastBenchmark;
    private StressRun? _lastStress;
    private bool _thermalSafetyEnabled = true;
    private bool _thermalSafetyAvailable;
    private int _criticalSamples;
    private CpuTestStopReason _requestedStopReason = CpuTestStopReason.UserStopped;
    private string _cpuModel = "—";
    private string _cpuCoresThreads = "—";
    private string _currentTemperature = "—";
    private string _currentUsage = "—";
    private string _currentPower = "—";
    private string _elapsed = "00:00:00";
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
    public bool IsStressRunning => _activeTest == CpuTestKind.Stress;
    public bool IsTestRunning => _activeTest != CpuTestKind.None;
    public bool CanStartTest => !IsTestRunning;
    public bool CanStopTest => IsTestRunning && !_isStopping;
    public bool ThermalSafetyEnabled { get => _thermalSafetyEnabled; set => SetProperty(ref _thermalSafetyEnabled, value); }
    public bool ThermalSafetyAvailable { get => _thermalSafetyAvailable; private set => SetProperty(ref _thermalSafetyAvailable, value); }
    public string CurrentTemperature { get => _currentTemperature; private set => SetProperty(ref _currentTemperature, value); }
    public string CurrentUsage { get => _currentUsage; private set => SetProperty(ref _currentUsage, value); }
    public string CurrentPower { get => _currentPower; private set => SetProperty(ref _currentPower, value); }
    public string BenchmarkStatus => _benchmarkState switch
    {
        _ when _activeTest == CpuTestKind.Benchmark && _isStopping => L("TestLabStopping"),
        CpuBenchmarkState.Preparing => L("TestLabPreparing"),
        CpuBenchmarkState.WarmingUpSingle => L("TestLabWarmupSingle"),
        CpuBenchmarkState.RunningSingle => L("TestLabMeasureSingle"),
        CpuBenchmarkState.WarmingUpMulti => L("TestLabWarmupMulti"),
        CpuBenchmarkState.RunningMulti => L("TestLabMeasureMulti"),
        CpuBenchmarkState.Completed => L("TestLabCompleted"),
        CpuBenchmarkState.Cancelled => L("TestLabCancelled"),
        CpuBenchmarkState.Error => L("TestLabError"),
        _ => L("TestLabReady")
    };
    public string StressStatus => _stressState switch
    {
        _ when IsStressRunning && _isStopping => L("TestLabStopping"),
        CpuStressState.Preparing => L("TestLabPreparing"),
        CpuStressState.Running => L("TestLabRunning"),
        _ => _lastStress is null ? L("TestLabReady") : ResultStatus(_lastStress.Reason)
    };
    public string Elapsed { get => _elapsed; private set => SetProperty(ref _elapsed, value); }
    public string SingleScore => FormatScore(_lastBenchmark?.Result.SingleThread);
    public string MultiScore => FormatScore(_lastBenchmark?.Result.MultiThread);
    public string Ratio => _lastBenchmark?.Result.SingleThread is { Score: > 0 } single &&
        _lastBenchmark.Result.MultiThread is { } multi ? $"{(double)multi.Score / single.Score:0.00}x" : NoValue;
    public IReadOnlyList<TestLabResultRow> BenchmarkResultRows { get; private set; } = Array.Empty<TestLabResultRow>();
    public IReadOnlyList<TestLabResultRow> StressResultRows { get; private set; } = Array.Empty<TestLabResultRow>();
    public bool HasBenchmarkResult => _lastBenchmark is not null;
    public bool HasStressResult => _lastStress is not null;
    public string LastBenchmarkSummary => !HasBenchmarkResult ? string.Empty : string.Join(Environment.NewLine,
        new[] { BenchmarkVersion, $"{L("TestLabSingleThread")}: {SingleScore}", $"{L("TestLabMultiThread")}: {MultiScore}",
            $"{L("TestLabMultiThreadRatio")}: {Ratio}", FormatRows(BenchmarkResultRows) });
    public string LastStressSummary => FormatRows(StressResultRows);
    public string CopyableResultsText =>
        $"{CpuModel}\n{CpuCoresThreads}\n\n{L("TestLabLastBenchmark")}\n{DisplayResult(LastBenchmarkSummary)}\n\n{L("TestLabLastStress")}\n{DisplayResult(LastStressSummary)}";
    private bool _isBatteryPower;
    public bool IsBatteryPower { get => _isBatteryPower; private set => SetProperty(ref _isBatteryPower, value); }
    public bool ShouldShowStressWarning => !TestLabSettingsService.Instance.SuppressStressWarning;
    public bool SuppressStressWarningPreference
    {
        get => TestLabSettingsService.Instance.SuppressStressWarning;
        set { TestLabSettingsService.Instance.SetSuppressStressWarning(value); OnPropertyChanged(nameof(ShouldShowStressWarning)); }
    }
    public string ActiveTestName => _activeTest switch
    {
        CpuTestKind.Benchmark => L("TestLabCpuBenchmark"),
        CpuTestKind.Stress => L("TestLabCpuStress"),
        _ => string.Empty
    };
    public string ActiveTestDetail => _activeTest switch
    {
        CpuTestKind.Benchmark => $"{BenchmarkStatus}  |  {Elapsed}",
        CpuTestKind.Stress => $"{StressStatus}  |  {Elapsed}  |  {CurrentTemperature}  |  {CurrentUsage}",
        _ => string.Empty
    };

    public void ObserveTelemetry(HardwareSnapshot snapshot, TemperatureStatus cpuStatus, bool useFahrenheit)
    {
        _useFahrenheit = useFahrenheit;
        CpuModel = snapshot.CpuName; CpuCoresThreads = snapshot.CpuCoresThreads;
        OnPropertyChanged(nameof(CopyableResultsText));
        IsBatteryPower = snapshot.BatteryInfo.Contains("AC: Offline", StringComparison.OrdinalIgnoreCase);
        bool temperatureAvailable = TemperatureStatusService.IsAvailableTemperature(snapshot.CpuTemperatureValue);
        ThermalSafetyAvailable = temperatureAvailable;
        CurrentTemperature = FormatTemperature(snapshot.CpuTemperatureValue);
        CurrentUsage = FormatValue(snapshot.CpuUsageValue, "%"); CurrentPower = FormatValue(snapshot.CpuPowerValue, "W");
        if (_session is null) return;
        _session.AddTelemetry(snapshot.CpuTemperatureValue, snapshot.CpuUsageValue, snapshot.CpuPowerValue);
        AddHistory(TemperatureHistory, snapshot.CpuTemperatureValue, true); AddHistory(UsageHistory, snapshot.CpuUsageValue, false);
        TimeSpan elapsed = DateTime.UtcNow - _startedUtc;
        Elapsed = FormatElapsed(elapsed);
        OnPropertyChanged(nameof(ActiveTestDetail));
        if (!IsStressRunning || _isStopping) return;
        if (ThermalSafetyEnabled && ThermalSafetyAvailable)
        {
            _criticalSamples = cpuStatus.State == TemperatureStatusKind.Critical ? _criticalSamples + 1 : 0;
            if (_criticalSamples >= 3)
            {
                _ = StopAsync(CpuTestStopReason.CriticalTemperature);
                return;
            }
        }
        if (_stressDuration is TimeSpan duration && elapsed >= duration)
        {
            _ = StopAsync(CpuTestStopReason.Completed);
        }
    }

    public async Task StartBenchmarkAsync()
    {
        if (IsTestRunning) return;
        _benchmarkState = CpuBenchmarkState.Preparing;
        StartSession(CpuTestKind.Benchmark);
        try
        {
            CpuBenchmarkResult result = await _coordinator.StartBenchmarkAsync(SelectedThreadOption?.Count ?? Environment.ProcessorCount, UpdateBenchmarkState);
            _lastBenchmark = new(result, _session);
            _benchmarkState = result.StopReason switch
            {
                CpuTestStopReason.Completed => CpuBenchmarkState.Completed,
                CpuTestStopReason.Cancelled => CpuBenchmarkState.Cancelled,
                _ => CpuBenchmarkState.Error
            };
            RefreshResults();
        }
        finally { EndSession(); }
    }

    public async Task StartStressAsync()
    {
        if (IsTestRunning) return;
        _stressDuration = SelectedDurationOption?.Duration;
        _requestedStopReason = CpuTestStopReason.UserStopped;
        _stressState = CpuStressState.Preparing;
        StartSession(CpuTestKind.Stress);
        try
        {
            CpuStressResult result = await _coordinator.StartStressAsync(SelectedThreadOption?.Count ?? Environment.ProcessorCount, UpdateStressState);
            CpuTestStopReason reason = result.StopReason == CpuTestStopReason.UserStopped ? _requestedStopReason : result.StopReason;
            _lastStress = new(result, reason, _session);
            _stressState = CpuStressState.Idle;
            Elapsed = FormatElapsed(result.Elapsed);
            RefreshResults();
        }
        finally { EndSession(); }
    }

    public async Task StopAsync(CpuTestStopReason reason = CpuTestStopReason.UserStopped)
    {
        if (!IsTestRunning) return;
        if (!_isStopping)
        {
            _requestedStopReason = reason;
            _isStopping = true;
            OnPropertyChanged(nameof(CanStopTest));
            NotifyStatus();
        }
        await _coordinator.StopAsync();
    }
    public void SuppressStressWarning() => TestLabSettingsService.Instance.SetSuppressStressWarning(true);

    private void StartSession(CpuTestKind kind)
    {
        _startedUtc = DateTime.UtcNow;
        _session = new CpuTestSession(_startedUtc);
        _criticalSamples = 0;
        TemperatureHistory.Clear();
        UsageHistory.Clear();
        Elapsed = "00:00:00";
        _activeTest = kind;
        NotifyActivity();
    }

    private void EndSession()
    {
        _session = null;
        _isStopping = false;
        _activeTest = CpuTestKind.None;
        NotifyActivity();
    }

    private void NotifyActivity()
    {
        OnPropertyChanged(nameof(IsTestRunning));
        OnPropertyChanged(nameof(IsStressRunning));
        OnPropertyChanged(nameof(CanStartTest));
        OnPropertyChanged(nameof(CanStopTest));
        NotifyStatus();
    }

    private void NotifyStatus()
    {
        OnPropertyChanged(nameof(BenchmarkStatus));
        OnPropertyChanged(nameof(StressStatus));
        OnPropertyChanged(nameof(ActiveTestName));
        OnPropertyChanged(nameof(ActiveTestDetail));
    }

    private void UpdateBenchmarkState(CpuBenchmarkState state) => Application.Current.Dispatcher.BeginInvoke(() =>
    {
        if (_activeTest != CpuTestKind.Benchmark || _isStopping) return;
        _benchmarkState = state;
        NotifyStatus();
    });

    private void UpdateStressState(CpuStressState state) => Application.Current.Dispatcher.BeginInvoke(() =>
    {
        if (!IsStressRunning || _isStopping) return;
        _stressState = state;
        NotifyStatus();
    });

    private void RefreshResults()
    {
        if (_lastBenchmark is { } benchmark)
        {
            CpuBenchmarkResult result = benchmark.Result;
            BenchmarkResultRows =
            [
                new(L("TestLabResult"), ResultStatus(result.StopReason, result.Error)),
                new(L("TestLabDuration"), FormatElapsed(result.Elapsed)),
                new(L("TestLabThreads"), result.WorkerCount.ToString()),
                new(L("TestLabPeakTemperature"), FormatTemperature(benchmark.Telemetry?.Temperature.Maximum)),
                new(L("TestLabPeakPower"), FormatValue(benchmark.Telemetry?.Power.Maximum, "W"))
            ];
        }
        if (_lastStress is { } stress)
        {
            StressResultRows =
            [
                new(L("TestLabResult"), ResultStatus(stress.Reason, stress.Result.Error)),
                new(L("TestLabDuration"), FormatElapsed(stress.Result.Elapsed)),
                new(L("TestLabThreads"), stress.Result.WorkerCount.ToString()),
                new(L("TestLabAverageUsage"), FormatDouble(stress.Telemetry?.Usage.Average, "%")),
                new(L("TestLabAverageTemperature"), FormatDouble(stress.Telemetry?.Temperature.Average, "C", true)),
                new(L("TestLabPeakTemperature"), FormatTemperature(stress.Telemetry?.Temperature.Maximum)),
                new(L("TestLabAveragePower"), FormatDouble(stress.Telemetry?.Power.Average, "W")),
                new(L("TestLabPeakPower"), FormatValue(stress.Telemetry?.Power.Maximum, "W"))
            ];
        }
        OnPropertyChanged(nameof(SingleScore));
        OnPropertyChanged(nameof(MultiScore));
        OnPropertyChanged(nameof(Ratio));
        OnPropertyChanged(nameof(BenchmarkResultRows));
        OnPropertyChanged(nameof(StressResultRows));
        OnPropertyChanged(nameof(HasBenchmarkResult));
        OnPropertyChanged(nameof(HasStressResult));
        OnPropertyChanged(nameof(CopyableResultsText));
    }

    private static string ResultStatus(CpuTestStopReason reason, string? error = null) => reason switch
    {
        CpuTestStopReason.Completed => L("TestLabCompleted"),
        CpuTestStopReason.CriticalTemperature => L("TestLabStoppedCritical"),
        CpuTestStopReason.Cancelled => L("TestLabCancelled"),
        CpuTestStopReason.Error => string.IsNullOrWhiteSpace(error) ? L("TestLabError") : $"{L("TestLabError")}: {error}",
        _ => L("TestLabStopped")
    };

    private static string FormatRows(IEnumerable<TestLabResultRow> rows) =>
        string.Join(Environment.NewLine, rows.Select(row => $"{row.Label}: {row.Value}"));

    private static string FormatScore(CpuBenchmarkStageResult? stage) =>
        stage is null ? NoValue : string.Format(CultureInfo.CurrentCulture, L("TestLabPointsFormat"), stage.Score);

    private static string FormatElapsed(TimeSpan elapsed) =>
        $"{(long)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";
    private static void AddHistory(ObservableCollection<double> history, float? value, bool temp)
    { if ((temp && !TemperatureStatusService.IsAvailableTemperature(value)) || (!temp && (!value.HasValue || float.IsNaN(value.Value) || float.IsInfinity(value.Value)))) return; history.Add(value!.Value); while (history.Count > MaxHistoryPoints) history.RemoveAt(0); }
    private string FormatTemperature(float? value) => !TemperatureStatusService.IsAvailableTemperature(value) ? "—" : $"{(_useFahrenheit ? value!.Value * 9d / 5d + 32d : value!.Value):0.#} {(_useFahrenheit ? "F" : "C")}";
    private static string FormatValue(float? value, string unit) => value.HasValue && !float.IsNaN(value.Value) && !float.IsInfinity(value.Value) ? $"{value.Value:0.#} {unit}" : "—";
    private string FormatDouble(double? value, string unit, bool temperature = false) => !value.HasValue || double.IsNaN(value.Value) ? "—" : temperature ? FormatTemperature((float)value.Value) : $"{value.Value:0.#} {unit}";
    private static string DisplayResult(string value) => string.IsNullOrWhiteSpace(value) ? "—" : value;
    private void RebuildOptions()
    {
        int oldThreads = SelectedThreadOption?.Count ?? Environment.ProcessorCount;
        TimeSpan? oldDuration = SelectedDurationOption is null ? TimeSpan.FromMinutes(5) : SelectedDurationOption.Duration;
        ThreadOptions.Clear(); ThreadOptions.Add(new(Environment.ProcessorCount, $"{L("TestLabAllThreads")} ({Environment.ProcessorCount}T)")); for (int i = 1; i <= Environment.ProcessorCount; i++) ThreadOptions.Add(new(i, $"{i} {L("TestLabThreads")}"));
        DurationOptions.Clear(); DurationOptions.Add(new(TimeSpan.FromMinutes(1), L("TestLabOneMinute"))); DurationOptions.Add(new(TimeSpan.FromMinutes(5), L("TestLabFiveMinutes"))); DurationOptions.Add(new(TimeSpan.FromMinutes(10), L("TestLabTenMinutes"))); DurationOptions.Add(new(TimeSpan.FromMinutes(30), L("TestLabThirtyMinutes"))); DurationOptions.Add(new(null, L("TestLabUntilStopped")));
        SelectedThreadOption = ThreadOptions[System.Math.Clamp(oldThreads == Environment.ProcessorCount ? 0 : oldThreads, 0, ThreadOptions.Count - 1)]; SelectedDurationOption = DurationOptions.FirstOrDefault(option => option.Duration == oldDuration) ?? DurationOptions[1];
    }
    private static string L(string key) => LocalizationService.Instance[key];
    private void LocalizationChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(LocalizationService.Culture)) return;
        RebuildOptions();
        RefreshResults();
        NotifyStatus();
    }
    public void Dispose() { if (_disposed) return; _disposed = true; LocalizationService.Instance.PropertyChanged -= LocalizationChanged; _coordinator.Dispose(); }
    public event PropertyChangedEventHandler? PropertyChanged;
    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? name = null) { if (Equals(field, value)) return false; field = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name)); return true; }
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
