using System;

namespace RoadSnow.Core;

public sealed class SnowTransition
{
    public float DepthMm { get; private set; }

    public void Initialize(float depthMm)
    {
        Guard.NonNegative(depthMm, nameof(depthMm));
        DepthMm = depthMm;
    }

    public bool Advance(float targetMm, float elapsedSeconds)
    {
        Guard.NonNegative(targetMm, nameof(targetMm));
        Guard.NonNegative(elapsedSeconds, nameof(elapsedSeconds));
        float difference = targetMm - DepthMm;
        float step = difference < 0 ? -difference : 2f * elapsedSeconds;
        float next =
            targetMm == 0
                ? 0
                : Math.Max(
                    0,
                    DepthMm + Math.Sign(difference) * Math.Min(Math.Abs(difference), step)
                );
        bool changed = next != DepthMm;
        DepthMm = next;
        return changed;
    }
}
