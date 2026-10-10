using System;

namespace RoadSnow.Core;

public sealed class ResidualSnow
{
    public float DepthMm { get; private set; }

    public void Advance(
        float observed,
        float hours,
        float retentionHours,
        float temperature,
        bool snowing
    )
    {
        observed = Math.Max(0, observed);
        if (retentionHours <= 0)
        {
            DepthMm = observed;
            return;
        }

        float melting = 30 * Math.Max(0, hours) / retentionHours * Math.Max(1, temperature / 5);
        DepthMm = Math.Max(observed, Math.Max(0, Math.Min(30, DepthMm) - melting));
    }
}
