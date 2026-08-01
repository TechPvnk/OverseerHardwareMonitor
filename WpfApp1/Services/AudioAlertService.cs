using System;
using System.Collections.Generic;
using System.Media;
using Overseer.Models;

namespace Overseer.Services;

public sealed class AudioAlertService
{
    private const float CpuCriticalCelsius = 95f;
    private const float GpuCriticalCelsius = 90f;
    private const float StorageCriticalCelsius = 70f;
    private const float RearmDeltaCelsius = 10f;
    private static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(5);

    private readonly Dictionary<string, TemperatureAlertState> _temperatureStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _driveHealthStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _smartAlertCooldowns = new(StringComparer.OrdinalIgnoreCase);

    public void ProcessSnapshot(HardwareSnapshot snapshot, bool audioEnabled)
    {
        if (!audioEnabled)
        {
            UpdateKnownDriveHealth(snapshot);
            return;
        }

        CheckTemperature("CPU", snapshot.CpuTemperatureValue, CpuCriticalCelsius);
        CheckTemperature("GPU", snapshot.GpuTemperatureValue, GpuCriticalCelsius);

        foreach (StorageHealthSnapshot drive in snapshot.StorageDrives)
        {
            CheckTemperature($"Storage:{drive.Name}", drive.TemperatureValue, StorageCriticalCelsius);
            CheckSmartHealth(drive);
        }
    }

    public void Reset()
    {
        _temperatureStates.Clear();
        _driveHealthStates.Clear();
        _smartAlertCooldowns.Clear();
    }

    private void CheckTemperature(string sensorKey, float? temperatureCelsius, float thresholdCelsius)
    {
        TemperatureAlertState state = GetTemperatureState(sensorKey);
        if (!temperatureCelsius.HasValue)
        {
            return;
        }

        if (temperatureCelsius.Value <= thresholdCelsius - RearmDeltaCelsius)
        {
            state.Armed = true;
            return;
        }

        if (temperatureCelsius.Value < thresholdCelsius || !state.Armed)
        {
            return;
        }

        DateTime now = DateTime.UtcNow;
        if (now - state.LastPlayedUtc < Cooldown)
        {
            return;
        }

        PlayWarningChime();
        state.LastPlayedUtc = now;
        state.Armed = false;
    }

    private void CheckSmartHealth(StorageHealthSnapshot drive)
    {
        string currentState = BuildDriveHealthState(drive);
        _driveHealthStates.TryGetValue(drive.Name, out string? previousState);
        _driveHealthStates[drive.Name] = currentState;

        if (!IsUnhealthyDriveState(drive) || string.Equals(previousState, currentState, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        DateTime now = DateTime.UtcNow;
        if (_smartAlertCooldowns.TryGetValue(drive.Name, out DateTime lastPlayedUtc) && now - lastPlayedUtc < Cooldown)
        {
            return;
        }

        PlayWarningChime();
        _smartAlertCooldowns[drive.Name] = now;
    }

    private void UpdateKnownDriveHealth(HardwareSnapshot snapshot)
    {
        foreach (StorageHealthSnapshot drive in snapshot.StorageDrives)
        {
            _driveHealthStates[drive.Name] = BuildDriveHealthState(drive);
        }
    }

    private TemperatureAlertState GetTemperatureState(string sensorKey)
    {
        if (!_temperatureStates.TryGetValue(sensorKey, out TemperatureAlertState? state))
        {
            state = new TemperatureAlertState();
            _temperatureStates[sensorKey] = state;
        }

        return state;
    }

    private static string BuildDriveHealthState(StorageHealthSnapshot drive)
    {
        return $"{drive.HealthStatus}|{drive.ErrorFlag}";
    }

    private static bool IsUnhealthyDriveState(StorageHealthSnapshot drive)
    {
        string health = Normalize(drive.HealthStatus);
        string error = Normalize(drive.ErrorFlag);

        bool unhealthyHealth = health.Contains("bad", StringComparison.OrdinalIgnoreCase)
            || health.Contains("caution", StringComparison.OrdinalIgnoreCase)
            || health.Contains("critical", StringComparison.OrdinalIgnoreCase)
            || health.Contains("warning", StringComparison.OrdinalIgnoreCase)
            || health.Contains("fail", StringComparison.OrdinalIgnoreCase);

        bool hasErrors = error.Length > 0
            && !string.Equals(error, "no errors", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(error, "none", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(error, "n/a", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(error, "unknown", StringComparison.OrdinalIgnoreCase);

        return unhealthyHealth || hasErrors;
    }

    private static string Normalize(string value)
    {
        return value.Trim();
    }

    private static void PlayWarningChime()
    {
        try
        {
            SystemSounds.Exclamation.Play();
        }
        catch
        {
            // Audio is best-effort; monitoring should never fail because a chime cannot play.
        }
    }

    private sealed class TemperatureAlertState
    {
        public bool Armed { get; set; } = true;
        public DateTime LastPlayedUtc { get; set; } = DateTime.MinValue;
    }
}