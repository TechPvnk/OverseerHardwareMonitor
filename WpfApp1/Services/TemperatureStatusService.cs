using System;

namespace Overseer.Services;

public enum TemperatureCategory
{
    Cpu,
    Gpu,
    Storage
}

public enum TemperatureStatusKind
{
    Unavailable,
    Normal,
    High,
    Critical
}

public sealed record TemperatureThreshold(float HighCelsius, float CriticalCelsius);

public sealed record TemperatureStatus(
    TemperatureStatusKind State,
    bool IsAvailable,
    float? TemperatureCelsius,
    TemperatureThreshold Threshold)
{
    public static TemperatureStatus Unavailable(TemperatureThreshold threshold) =>
        new(TemperatureStatusKind.Unavailable, false, null, threshold);
}

public static class TemperatureThresholds
{
    public const float AlertRearmDeltaCelsius = 10f;

    public static TemperatureThreshold Cpu { get; } = new(85f, 95f);
    public static TemperatureThreshold Gpu { get; } = new(80f, 90f);
    public static TemperatureThreshold Storage { get; } = new(60f, 70f);

    public static TemperatureThreshold For(TemperatureCategory category)
    {
        return category switch
        {
            TemperatureCategory.Cpu => Cpu,
            TemperatureCategory.Gpu => Gpu,
            TemperatureCategory.Storage => Storage,
            _ => throw new ArgumentOutOfRangeException(nameof(category), category, null)
        };
    }
}

public static class TemperatureStatusService
{
    public static TemperatureStatus Evaluate(TemperatureCategory category, float? temperatureCelsius)
    {
        TemperatureThreshold threshold = TemperatureThresholds.For(category);
        if (!IsAvailableTemperature(temperatureCelsius))
        {
            return TemperatureStatus.Unavailable(threshold);
        }

        float value = temperatureCelsius.GetValueOrDefault();
        TemperatureStatusKind state = value >= threshold.CriticalCelsius
            ? TemperatureStatusKind.Critical
            : value >= threshold.HighCelsius
                ? TemperatureStatusKind.High
                : TemperatureStatusKind.Normal;

        return new TemperatureStatus(state, true, value, threshold);
    }

    public static bool IsAvailableTemperature(float? temperatureCelsius)
    {
        return temperatureCelsius.HasValue
            && temperatureCelsius.Value > 0f
            && !float.IsNaN(temperatureCelsius.Value)
            && !float.IsInfinity(temperatureCelsius.Value);
    }
}