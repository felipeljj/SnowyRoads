using System;

namespace RoadSnow.Core;

public sealed class SnowSettings
{
    public float MaximumDepthMm { get; }
    public float FullCoverageDepthMm { get; }
    public float MeltMmPerDegreeHour { get; }
    public float RainMeltMultiplier { get; }
    public float TireRemovalFraction { get; }
    public float TireWidthMeters { get; }
    public float CurbRetentionWidthMeters { get; }
    public int PatternSeed { get; }
    public float PatternOffsetMeters { get; }
    public float FootprintWidthMeters { get; }
    public float FootprintLengthMeters { get; }
    public float FootprintRemovalFraction { get; }

    public SnowSettings(
        float maximumDepthMm = 80,
        float fullCoverageDepthMm = 15,
        float meltMmPerDegreeHour = 4,
        float rainMeltMultiplier = 2,
        float tireRemovalFraction = 0.7f,
        float tireWidthMeters = 0.3f,
        float curbRetentionWidthMeters = 0.7f,
        int patternSeed = 1984,
        float patternOffsetMeters = 0,
        float footprintWidthMeters = 0.12f,
        float footprintLengthMeters = 0.28f,
        float footprintRemovalFraction = 0.8f
    )
    {
        Guard.Positive(maximumDepthMm, nameof(maximumDepthMm));
        Guard.Positive(fullCoverageDepthMm, nameof(fullCoverageDepthMm));
        Guard.NonNegative(meltMmPerDegreeHour, nameof(meltMmPerDegreeHour));
        Guard.NonNegative(rainMeltMultiplier, nameof(rainMeltMultiplier));
        Guard.NonNegative(tireRemovalFraction, nameof(tireRemovalFraction));
        Guard.Positive(tireWidthMeters, nameof(tireWidthMeters));
        Guard.NonNegative(curbRetentionWidthMeters, nameof(curbRetentionWidthMeters));
        Guard.Finite(patternOffsetMeters, nameof(patternOffsetMeters));
        Guard.Positive(footprintWidthMeters, nameof(footprintWidthMeters));
        Guard.Positive(footprintLengthMeters, nameof(footprintLengthMeters));
        Guard.NonNegative(footprintRemovalFraction, nameof(footprintRemovalFraction));
        if (footprintRemovalFraction > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(footprintRemovalFraction));
        }

        if (fullCoverageDepthMm > maximumDepthMm || tireRemovalFraction > 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(fullCoverageDepthMm),
                "Invalid coverage/removal settings."
            );
        }

        MaximumDepthMm = maximumDepthMm;
        FullCoverageDepthMm = fullCoverageDepthMm;
        MeltMmPerDegreeHour = meltMmPerDegreeHour;
        RainMeltMultiplier = rainMeltMultiplier;
        TireRemovalFraction = tireRemovalFraction;
        TireWidthMeters = tireWidthMeters;
        CurbRetentionWidthMeters = curbRetentionWidthMeters;
        PatternSeed = patternSeed;
        PatternOffsetMeters = patternOffsetMeters;
        FootprintWidthMeters = footprintWidthMeters;
        FootprintLengthMeters = footprintLengthMeters;
        FootprintRemovalFraction = footprintRemovalFraction;
    }
}

internal static class Guard
{
    public static void Finite(float value, string name)
    {
        if (float.IsNaN(value) || float.IsInfinity(value))
        {
            throw new ArgumentOutOfRangeException(name, "Must be finite.");
        }
    }

    public static void NonNegative(float value, string name)
    {
        Finite(value, name);
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(name);
        }
    }

    public static void Positive(float value, string name)
    {
        Finite(value, name);
        if (value <= 0)
        {
            throw new ArgumentOutOfRangeException(name);
        }
    }
}
