using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Overseer.Services.Testing;

public enum CpuTestKind { None, Benchmark, Stress }
public enum CpuBenchmarkState { Idle, Preparing, WarmingUpSingle, RunningSingle, WarmingUpMulti, RunningMulti, Completed, Cancelled, Error }
public enum CpuStressState { Idle, Preparing, Running, Stopping, Completed, StoppedByUser, StoppedThermal, Error }
public enum CpuTestStopReason { Completed, UserStopped, CriticalTemperature, Cancelled, Error }

public sealed record CpuBenchmarkStageResult(double Throughput, int Score, TimeSpan Elapsed);
public sealed record CpuBenchmarkResult(CpuBenchmarkStageResult? SingleThread, CpuBenchmarkStageResult? MultiThread, int WorkerCount, TimeSpan Elapsed, CpuTestStopReason StopReason, string? Error);
public sealed record CpuStressResult(int WorkerCount, TimeSpan Elapsed, CpuTestStopReason StopReason, string? Error);

public sealed class StreamingMetric
{
    public int Count { get; private set; }
    public float? Minimum { get; private set; }
    public float? Maximum { get; private set; }
    public double Average => Count == 0 ? double.NaN : _sum / Count;
    private double _sum;
    public void Add(float? value)
    {
        if (!value.HasValue || float.IsNaN(value.Value) || float.IsInfinity(value.Value)) return;
        float item = value.Value; Count++; _sum += item;
        Minimum = !Minimum.HasValue || item < Minimum.Value ? item : Minimum;
        Maximum = !Maximum.HasValue || item > Maximum.Value ? item : Maximum;
    }
}

public sealed class CpuTestSession
{
    public CpuTestSession(DateTime startedUtc) => StartedUtc = startedUtc;
    public DateTime StartedUtc { get; }
    public StreamingMetric Temperature { get; } = new();
    public StreamingMetric Usage { get; } = new();
    public StreamingMetric Power { get; } = new();
    public void AddTelemetry(float? temperature, float? usage, float? power)
    { Temperature.Add(temperature); Usage.Add(usage); Power.Add(power); }
}

public sealed class TestCoordinator : IDisposable
{
    private readonly object _gate = new();
    private CancellationTokenSource? _cancellation;
    private Task? _activeTask;
    private CpuTestKind _activeKind;
    private bool _disposed;
    public CpuTestKind ActiveKind { get { lock (_gate) return _activeKind; } }
    public bool IsRunning => ActiveKind != CpuTestKind.None;

    public Task<CpuBenchmarkResult> StartBenchmarkAsync(int workers, Action<CpuBenchmarkState>? stateChanged)
    {
        CancellationToken token = Begin(CpuTestKind.Benchmark);
        Task<CpuBenchmarkResult> task = CpuBenchmarkService.RunAsync(workers, stateChanged, token); Track(task); return task;
    }
    public Task<CpuStressResult> StartStressAsync(int workers, Action<CpuStressState>? stateChanged)
    {
        CancellationToken token = Begin(CpuTestKind.Stress);
        Task<CpuStressResult> task = CpuStressService.RunAsync(workers, stateChanged, token); Track(task); return task;
    }
    public async Task StopAsync()
    {
        Task? active; lock (_gate) { _cancellation?.Cancel(); active = _activeTask; }
        if (active is not null) { try { await active.ConfigureAwait(false); } catch (OperationCanceledException) { } catch { } }
    }
    private CancellationToken Begin(CpuTestKind kind)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_activeKind != CpuTestKind.None) throw new InvalidOperationException("A TestLab operation is already running.");
            _cancellation = new CancellationTokenSource(); _activeKind = kind; return _cancellation.Token;
        }
    }
    private void Track(Task task)
    {
        lock (_gate) _activeTask = task;
        _ = task.ContinueWith(_ => { lock (_gate) { _cancellation?.Dispose(); _cancellation = null; _activeTask = null; _activeKind = CpuTestKind.None; } }, TaskScheduler.Default);
    }
    public void Dispose() { if (_disposed) return; _disposed = true; StopAsync().GetAwaiter().GetResult(); }
}

public static class CpuBenchmarkService
{
    public const string BenchmarkVersion = "Overseer CPU Benchmark 1.0";
    public static readonly TimeSpan WarmupDuration = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan MeasurementDuration = TimeSpan.FromSeconds(5);
    // Benchmark 1.0 points = deterministic work units per second / 100.
    public const double Benchmark1NormalizationConstant = 0.01d;
    public static async Task<CpuBenchmarkResult> RunAsync(int workers, Action<CpuBenchmarkState>? stateChanged, CancellationToken token)
    {
        workers = ClampWorkers(workers); Stopwatch total = Stopwatch.StartNew();
        try
        {
            stateChanged?.Invoke(CpuBenchmarkState.Preparing);
            stateChanged?.Invoke(CpuBenchmarkState.WarmingUpSingle); await CpuWorkload.RunForAsync(1, WarmupDuration, token).ConfigureAwait(false);
            if (token.IsCancellationRequested) return Cancelled(workers, total, stateChanged);
            stateChanged?.Invoke(CpuBenchmarkState.RunningSingle); CpuBenchmarkStageResult single = ToStage(await CpuWorkload.RunForAsync(1, MeasurementDuration, token).ConfigureAwait(false));
            if (token.IsCancellationRequested) return Cancelled(workers, total, stateChanged);
            stateChanged?.Invoke(CpuBenchmarkState.WarmingUpMulti); await CpuWorkload.RunForAsync(workers, WarmupDuration, token).ConfigureAwait(false);
            if (token.IsCancellationRequested) return Cancelled(workers, total, stateChanged);
            stateChanged?.Invoke(CpuBenchmarkState.RunningMulti); CpuBenchmarkStageResult multi = ToStage(await CpuWorkload.RunForAsync(workers, MeasurementDuration, token).ConfigureAwait(false));
            if (token.IsCancellationRequested) return Cancelled(workers, total, stateChanged);
            stateChanged?.Invoke(CpuBenchmarkState.Completed); return new CpuBenchmarkResult(single, multi, workers, total.Elapsed, CpuTestStopReason.Completed, null);
        }
        catch (OperationCanceledException) { stateChanged?.Invoke(CpuBenchmarkState.Cancelled); return new CpuBenchmarkResult(null, null, workers, total.Elapsed, CpuTestStopReason.Cancelled, null); }
        catch (Exception ex) { stateChanged?.Invoke(CpuBenchmarkState.Error); return new CpuBenchmarkResult(null, null, workers, total.Elapsed, CpuTestStopReason.Error, ex.Message); }
    }
    private static CpuBenchmarkStageResult ToStage(CpuWorkloadResult result) => new(result.WorkUnits / Math.Max(result.Elapsed.TotalSeconds, .001d), Math.Max(0, (int)Math.Round(result.WorkUnits / Math.Max(result.Elapsed.TotalSeconds, .001d) * Benchmark1NormalizationConstant)), result.Elapsed);
    private static CpuBenchmarkResult Cancelled(int workers, Stopwatch total, Action<CpuBenchmarkState>? stateChanged)
    { stateChanged?.Invoke(CpuBenchmarkState.Cancelled); return new CpuBenchmarkResult(null, null, workers, total.Elapsed, CpuTestStopReason.Cancelled, null); }
    internal static int ClampWorkers(int workers) => Math.Clamp(workers, 1, Math.Max(1, Environment.ProcessorCount));
}

public static class CpuStressService
{
    public static async Task<CpuStressResult> RunAsync(int workers, Action<CpuStressState>? stateChanged, CancellationToken token)
    {
        workers = CpuBenchmarkService.ClampWorkers(workers); Stopwatch stopwatch = Stopwatch.StartNew();
        try { stateChanged?.Invoke(CpuStressState.Preparing); stateChanged?.Invoke(CpuStressState.Running); await CpuWorkload.RunUntilCancelledAsync(workers, token).ConfigureAwait(false); return new CpuStressResult(workers, stopwatch.Elapsed, CpuTestStopReason.UserStopped, null); }
        catch (OperationCanceledException) { stateChanged?.Invoke(CpuStressState.StoppedByUser); return new CpuStressResult(workers, stopwatch.Elapsed, CpuTestStopReason.UserStopped, null); }
        catch (Exception ex) { stateChanged?.Invoke(CpuStressState.Error); return new CpuStressResult(workers, stopwatch.Elapsed, CpuTestStopReason.Error, ex.Message); }
    }
}

internal readonly record struct CpuWorkloadResult(long WorkUnits, TimeSpan Elapsed);
internal static class CpuWorkload
{
    private static long s_sink;
    public static Task<CpuWorkloadResult> RunForAsync(int workers, TimeSpan duration, CancellationToken token) => RunAsync(workers, duration, token, false);
    public static async Task RunUntilCancelledAsync(int workers, CancellationToken token) { await RunAsync(workers, Timeout.InfiniteTimeSpan, token, true).ConfigureAwait(false); }
    private static async Task<CpuWorkloadResult> RunAsync(int workers, TimeSpan duration, CancellationToken token, bool untilCancelled)
    {
        using ManualResetEventSlim startGate = new(false); Task<long>[] tasks = new Task<long>[workers];
        for (int worker = 0; worker < workers; worker++) { int seed = worker + 1; tasks[worker] = Task.Factory.StartNew(() => RunWorker(seed, duration, startGate, token, untilCancelled), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default); }
        Stopwatch stopwatch = Stopwatch.StartNew(); startGate.Set();
        await Task.WhenAll(tasks).ConfigureAwait(false);
        long units = 0; foreach (Task<long> task in tasks) if (task.Status == TaskStatus.RanToCompletion) units += task.Result;
        return new CpuWorkloadResult(units, stopwatch.Elapsed);
    }
    private static long RunWorker(int worker, TimeSpan duration, ManualResetEventSlim startGate, CancellationToken token, bool untilCancelled)
    {
        startGate.Wait(); Stopwatch clock = Stopwatch.StartNew(); ulong state = 0x9E3779B97F4A7C15UL ^ (uint)worker; long units = 0;
        while (!token.IsCancellationRequested && (untilCancelled || clock.Elapsed < duration)) { state = ExecuteWorkUnit(state); units++; }
        Interlocked.Exchange(ref s_sink, unchecked((long)state) ^ Interlocked.Read(ref s_sink)); return units;
    }
    // Fixed register-only Benchmark 1.0 composite: integer mixing plus scalar floating point.
    private static ulong ExecuteWorkUnit(ulong state)
    {
        double vector = .6180339887498948d;
        for (int i = 0; i < 96; i++) { state ^= state >> 12; state ^= state << 25; state ^= state >> 27; state *= 0x2545F4914F6CDD1DUL; vector = vector * 1.0000001192092896d + ((state & 0xFFFF) * .000000001d); vector -= Math.Floor(vector); }
        return state ^ (ulong)(vector * 0xFFFFFFFFUL);
    }
}
