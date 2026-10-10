using System;

namespace RoadSnow.Core;

public sealed class SnowObservation
{
    private int _lastVersion = -1;
    private int _drySnapshots;
    public float DepthMm { get; private set; }

    public float Observe(float depthMm, int version)
    {
        Guard.NonNegative(depthMm, nameof(depthMm));
        if (version == _lastVersion)
        {
            return DepthMm;
        }

        _lastVersion = version;
        if (depthMm == 0 && DepthMm > 0)
        {
            if (++_drySnapshots < 2)
            {
                return DepthMm;
            }

            DepthMm = 0;
        }
        else
        {
            _drySnapshots = 0;
            DepthMm = (float)Math.Round(depthMm * 4) / 4;
        }

        return DepthMm;
    }
}
