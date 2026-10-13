using System;

namespace RoadSnow.Core;

// A complete road section is cleared once. New snowfall, not camera distance,
// pause state or the number of later vehicles, restores its snow coverage.
public sealed class SectionPlowState
{
    public double AtSnowfallMm { get; private set; } = -1;
    public float Strength { get; private set; }

    public bool Complete(double snowfallMm)
    {
        Validate(snowfallMm);
        bool changed = AtSnowfallMm < 0 || Math.Round(Strength * 255) < 255;
        AtSnowfallMm = snowfallMm;
        Strength = 1;
        return changed;
    }

    public bool Observe(double snowfallMm)
    {
        Validate(snowfallMm);
        float value =
            AtSnowfallMm < 0
                ? 0
                : (float)Math.Max(0, 1 - Math.Max(0, snowfallMm - AtSnowfallMm) / 15);
        bool changed = Math.Round(value * 255) != Math.Round(Strength * 255);
        Strength = value;
        return changed;
    }

    public void Restore(double atMm, double snowfallMm)
    {
        if (double.IsNaN(atMm) || double.IsInfinity(atMm) || atMm < -1)
            throw new ArgumentOutOfRangeException(nameof(atMm));
        AtSnowfallMm = atMm;
        Observe(snowfallMm);
    }

    private static void Validate(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value < 0)
            throw new ArgumentOutOfRangeException(nameof(value));
    }
}
