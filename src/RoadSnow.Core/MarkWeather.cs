using System;

namespace RoadSnow.Core;

public struct MarkWeather
{
    public float LastDepth { get; private set; }
    public float BurialMm { get; private set; }
    public bool Valid { get; private set; }

    public MarkWeather(float depth)
    {
        LastDepth = depth;
        BurialMm = 0;
        Valid = depth >= 0.2f;
    }

    public float Observe(float depth, bool snowing)
    {
        if (!Valid)
        {
            return 0;
        }

        if (depth < 0.125f)
        {
            Valid = false;
            return 0;
        }

        if (snowing)
        {
            BurialMm += Math.Max(0, depth - LastDepth);
        }

        LastDepth = depth;
        if (BurialMm >= 4)
        {
            Valid = false;
            return 0;
        }

        return Math.Min(1, depth / 2) * Math.Max(0, 1 - BurialMm / 4);
    }
}
