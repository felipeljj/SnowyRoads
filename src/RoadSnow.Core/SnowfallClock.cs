using System;

namespace RoadSnow.Core;

public sealed class SnowfallClock
{
    public const float MillimetersPerHour = 12;
    public double TotalMm { get; private set; }

    public void Reset() => TotalMm = 0;

    public void Advance(float gameHours, bool snowing, float precipitation)
    {
        if (
            !snowing
            || gameHours <= 0
            || precipitation <= 0
            || float.IsNaN(gameHours)
            || float.IsInfinity(gameHours)
            || float.IsNaN(precipitation)
        )
        {
            return;
        }

        TotalMm += gameHours * (double)Math.Min(1, precipitation) * MillimetersPerHour;
    }
}
