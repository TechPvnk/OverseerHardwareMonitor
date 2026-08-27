using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace Overseer.Services;

public enum FrameMonitorAvailability
{
    Initializing,
    Available,
    NoActiveApplication,
    MissingExecutable,
    AccessDenied,
    Failed
}

public sealed record FrameMonitorSnapshot(
    FrameMonitorAvailability Availability,
    string? TargetApplication,
    double? CurrentFps,
    double? AverageFps,
    double? OnePercentLowFps,
    double? HighFps,
    IReadOnlyList<double> RecentFrameTimes);

public sealed class FrameMonitorService : IDisposable
{
    private const string SessionName = "OverseerFrameMonitor";
    private const double RollingWindowSeconds = 0.75d;
    private const double CurrentFpsGraceSeconds = 2d;
    private const int MinimumOnePercentLowSamples = 100;
    private const double HighFpsWindowMilliseconds = 100d;
    private const int MinimumHighFpsWindows = 10;
    private const int MaximumSessionSamples = 1_000_000;
    private static readonly HashSet<string> ExcludedApplications = new(StringComparer.OrdinalIgnoreCase)
    {
        "Overseer.exe", "PresentMon.exe", "dwm.exe", "explorer.exe", "ShellExperienceHost.exe",
        "StartMenuExperienceHost.exe", "SearchHost.exe", "TextInputHost.exe", "LockApp.exe",
        "ChatGPT.exe", "devenv.exe", "Code.exe", "chrome.exe", "msedge.exe", "firefox.exe"
    };

    private readonly object _sync = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly string? _executablePath;
    private readonly Queue<TimedFrame> _rollingFrames = new();
    private readonly List<double> _sessionFrameTimes = new();
    private Process? _process;
    private Dictionary<string, int>? _columns;
    private FrameMonitorAvailability _availability = FrameMonitorAvailability.Initializing;
    private int? _targetProcessId;
    private string? _targetApplication;
    private double? _lastCurrentFps;
    private double _lastCurrentFpsSeconds;
    private bool _disposed;

    public FrameMonitorService(string? executablePath = null)
    {
        _executablePath = executablePath;
        Start();
    }

    public FrameMonitorSnapshot GetSnapshot()
    {
        lock (_sync)
        {
            if (_targetProcessId.HasValue && !IsProcessAlive(_targetProcessId.Value))
            {
                ClearTarget();
            }

            PruneRollingFrames(_clock.Elapsed.TotalSeconds);
            if (!_targetProcessId.HasValue)
            {
                FrameMonitorAvailability availability = _availability == FrameMonitorAvailability.Initializing
                    ? FrameMonitorAvailability.Initializing
                    : _availability is FrameMonitorAvailability.MissingExecutable or FrameMonitorAvailability.AccessDenied or FrameMonitorAvailability.Failed
                        ? _availability
                        : FrameMonitorAvailability.NoActiveApplication;
                return new FrameMonitorSnapshot(availability, null, null, null, null, null, Array.Empty<double>());
            }

            double now = _clock.Elapsed.TotalSeconds;
            double? currentFps = FpsFromAverageFrameTime(_rollingFrames.Select(frame => frame.FrameTimeMilliseconds));
            if (currentFps.HasValue)
            {
                _lastCurrentFps = currentFps;
                _lastCurrentFpsSeconds = now;
            }
            else if (_lastCurrentFps.HasValue && now - _lastCurrentFpsSeconds <= CurrentFpsGraceSeconds)
            {
                // PresentMon can deliver frames in short bursts; retain only the last real sample during a brief gap.
                currentFps = _lastCurrentFps;
            }
            double? averageFps = FpsFromAverageFrameTime(_sessionFrameTimes);
            double? onePercentLow = CalculateOnePercentLow(_sessionFrameTimes);
            double? onePercentHigh = CalculateOnePercentHigh(_sessionFrameTimes);
            double[] recent = _rollingFrames.Select(frame => frame.FrameTimeMilliseconds).TakeLast(120).ToArray();
            return new FrameMonitorSnapshot(FrameMonitorAvailability.Available, _targetApplication, currentFps, averageFps, onePercentLow, onePercentHigh, recent);
        }
    }

    public void ResetStatistics()
    {
        lock (_sync)
        {
            _rollingFrames.Clear();
            _sessionFrameTimes.Clear();
            _lastCurrentFps = null;
            _lastCurrentFpsSeconds = 0d;
        }
    }

    private void Start()
    {
        string executablePath = _executablePath
            ?? Path.Combine(AppContext.BaseDirectory, "ThirdParty", "PresentMon", "PresentMon.exe");
        if (!File.Exists(executablePath))
        {
            _availability = FrameMonitorAvailability.MissingExecutable;
            AppLog.Write($"PresentMon executable was not found at '{executablePath}'.");
            return;
        }

        try
        {
            ProcessStartInfo startInfo = new()
            {
                FileName = executablePath,
                Arguments = $"--output_stdout --no_console_stats --v2_metrics --exclude Overseer.exe --exclude PresentMon.exe --session_name {SessionName} --stop_existing_session",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(executablePath)!
            };

            _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            _process.OutputDataReceived += ProcessOutputDataReceived;
            _process.ErrorDataReceived += ProcessErrorDataReceived;
            _process.Exited += ProcessExited;
            if (!_process.Start())
            {
                _availability = FrameMonitorAvailability.Failed;
                return;
            }

            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
        }
        catch (Exception ex)
        {
            _availability = ex is UnauthorizedAccessException
                ? FrameMonitorAvailability.AccessDenied
                : FrameMonitorAvailability.Failed;
            AppLog.Write("PresentMon could not be started.", ex);
        }
    }

    private void ProcessOutputDataReceived(object sender, DataReceivedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.Data))
        {
            return;
        }

        try
        {
            IReadOnlyList<string> fields = ParseCsvLine(e.Data);
            lock (_sync)
            {
                if (fields.Count > 0 && string.Equals(fields[0], "Application", StringComparison.OrdinalIgnoreCase))
                {
                    _columns = fields.Select((name, index) => (name, index))
                        .ToDictionary(item => item.name, item => item.index, StringComparer.OrdinalIgnoreCase);
                    _availability = FrameMonitorAvailability.NoActiveApplication;
                    return;
                }

                if (_columns is null
                    || !TryGetField(fields, "Application", out string application)
                    || !TryGetField(fields, "ProcessID", out string processIdText)
                    || !TryGetField(fields, "FrameTime", out string frameTimeText)
                    || !int.TryParse(processIdText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int processId)
                    || !double.TryParse(frameTimeText, NumberStyles.Float, CultureInfo.InvariantCulture, out double frameTime)
                    || frameTime < 0.1d
                    || frameTime > 1000d
                    || string.IsNullOrWhiteSpace(application)
                    || application == "<unknown>"
                    || IsExcluded(application))
                {
                    return;
                }

                double now = _clock.Elapsed.TotalSeconds;
                if (!_targetProcessId.HasValue)
                {
                    uint foregroundProcessId = GetForegroundProcessId();
                    if (foregroundProcessId == processId)
                    {
                        SetTarget(processId, application);
                    }
                }

                if (_targetProcessId == processId)
                {
                    _availability = FrameMonitorAvailability.Available;
                    _rollingFrames.Enqueue(new TimedFrame(now, frameTime));
                    _sessionFrameTimes.Add(frameTime);
                    if (_sessionFrameTimes.Count > MaximumSessionSamples)
                    {
                        _sessionFrameTimes.RemoveRange(0, _sessionFrameTimes.Count - MaximumSessionSamples);
                    }

                    PruneRollingFrames(now);
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("PresentMon emitted an unreadable output row.", ex);
        }
    }

    private void ProcessErrorDataReceived(object sender, DataReceivedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.Data))
        {
            return;
        }

        if (e.Data.Contains("access denied", StringComparison.OrdinalIgnoreCase)
            || e.Data.Contains("requires elevated", StringComparison.OrdinalIgnoreCase))
        {
            lock (_sync)
            {
                if (_columns is null)
                {
                    _availability = FrameMonitorAvailability.AccessDenied;
                }
            }
        }

        AppLog.Write($"PresentMon: {e.Data}");
    }

    private void ProcessExited(object? sender, EventArgs e)
    {
        lock (_sync)
        {
            if (!_disposed)
            {
                _availability = FrameMonitorAvailability.Failed;
                ClearTarget();
                AppLog.Write("PresentMon stopped unexpectedly.");
            }
        }
    }

    private bool TryGetField(IReadOnlyList<string> fields, string name, out string value)
    {
        value = string.Empty;
        return _columns is not null
            && _columns.TryGetValue(name, out int index)
            && index >= 0
            && index < fields.Count
            && (value = fields[index]) is not null;
    }

    private void SetTarget(int processId, string application)
    {
        if (_targetProcessId == processId)
        {
            return;
        }

        _targetProcessId = processId;
        _targetApplication = application;
        _rollingFrames.Clear();
        _sessionFrameTimes.Clear();
        _lastCurrentFps = null;
        _lastCurrentFpsSeconds = 0d;
    }

    private void ClearTarget()
    {
        _targetProcessId = null;
        _targetApplication = null;
        _rollingFrames.Clear();
        _sessionFrameTimes.Clear();
        _lastCurrentFps = null;
        _lastCurrentFpsSeconds = 0d;
        if (_availability == FrameMonitorAvailability.Available)
        {
            _availability = FrameMonitorAvailability.NoActiveApplication;
        }
    }

    private void PruneRollingFrames(double now)
    {
        while (_rollingFrames.Count > 0 && now - _rollingFrames.Peek().TimestampSeconds > RollingWindowSeconds)
        {
            _rollingFrames.Dequeue();
        }
    }

    private static double? CalculateOnePercentLow(IReadOnlyCollection<double> frameTimes)
    {
        if (frameTimes.Count < MinimumOnePercentLowSamples)
        {
            return null;
        }

        // Conventional 1% low: average the slowest 1% of frame times, then convert that average to FPS.
        int slowFrameCount = Math.Max(1, (int)Math.Ceiling(frameTimes.Count * 0.01d));
        double averageSlowFrameTime = frameTimes.OrderByDescending(value => value).Take(slowFrameCount).Average();
        return averageSlowFrameTime > 0d ? 1000d / averageSlowFrameTime : null;
    }

    private static double? CalculateOnePercentHigh(IReadOnlyCollection<double> frameTimes)
    {
        List<double> windowRates = new();
        double elapsedMilliseconds = 0d;
        int frameCount = 0;
        foreach (double frameTime in frameTimes)
        {
            elapsedMilliseconds += frameTime;
            frameCount++;
            if (elapsedMilliseconds < HighFpsWindowMilliseconds)
            {
                continue;
            }

            windowRates.Add((frameCount * 1000d) / elapsedMilliseconds);
            elapsedMilliseconds = 0d;
            frameCount = 0;
        }

        if (windowRates.Count < MinimumHighFpsWindows)
        {
            return null;
        }

        // Aggregate first so one sub-millisecond PresentMon row cannot dominate the session high.
        double[] ordered = windowRates.OrderBy(value => value).ToArray();
        double position = (ordered.Length - 1) * 0.99d;
        int lowerIndex = (int)Math.Floor(position);
        int upperIndex = (int)Math.Ceiling(position);
        double fraction = position - lowerIndex;
        return ordered[lowerIndex]
            + ((ordered[upperIndex] - ordered[lowerIndex]) * fraction);
    }

    private static double? FpsFromAverageFrameTime(IEnumerable<double> frameTimes)
    {
        double[] values = frameTimes as double[] ?? frameTimes.ToArray();
        return values.Length > 0 ? 1000d / values.Average() : null;
    }

    private static bool IsExcluded(string application)
    {
        return ExcludedApplications.Contains(Path.GetFileName(application));
    }

    private static bool IsProcessAlive(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private static IReadOnlyList<string> ParseCsvLine(string line)
    {
        List<string> fields = new();
        System.Text.StringBuilder current = new();
        bool quoted = false;
        for (int index = 0; index < line.Length; index++)
        {
            char character = line[index];
            if (character == '"')
            {
                if (quoted && index + 1 < line.Length && line[index + 1] == '"')
                {
                    current.Append('"');
                    index++;
                }
                else
                {
                    quoted = !quoted;
                }
            }
            else if (character == ',' && !quoted)
            {
                fields.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(character);
            }
        }

        fields.Add(current.ToString());
        return fields;
    }

    private static uint GetForegroundProcessId()
    {
        IntPtr window = GetForegroundWindow();
        return window == IntPtr.Zero ? 0u : GetWindowThreadProcessId(window, out uint processId) == 0 ? 0u : processId;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        if (_process is not null)
        {
            _process.OutputDataReceived -= ProcessOutputDataReceived;
            _process.ErrorDataReceived -= ProcessErrorDataReceived;
            _process.Exited -= ProcessExited;
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                    _process.WaitForExit(2000);
                }
            }
            catch (Exception ex)
            {
                AppLog.Write("PresentMon could not be stopped cleanly.", ex);
            }
            finally
            {
                _process.Dispose();
                _process = null;
            }
        }
    }

    private readonly record struct TimedFrame(double TimestampSeconds, double FrameTimeMilliseconds);
}
