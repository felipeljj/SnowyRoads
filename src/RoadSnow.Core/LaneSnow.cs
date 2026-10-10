using System;

namespace RoadSnow.Core;

public sealed class LaneSnow
{
    public float Pressure { get; private set; }

    private float _lastNativeDepth;
    private bool _initialized;
    private double _lastSnowfallTotal = -1;

    public void Advance(
        float nativeDepth,
        float volume,
        float hours,
        bool snowing,
        float influence,
        double snowfallTotal = -1
    )
    {
        nativeDepth = Math.Max(0, nativeDepth);
        if (!_initialized)
        {
            _lastNativeDepth = nativeDepth;
            _initialized = true;
        }

        // use snowfall added since the last sample, not the whole storms total.
        // otherwise fresh marks get buried immediately while it is still snowing.
        float freshSnowDepth =
            snowfallTotal >= 0
                ? (
                    _lastSnowfallTotal < 0
                        ? 0
                        : (float)Math.Max(0, snowfallTotal - _lastSnowfallTotal)
                )
                : (snowing ? Math.Max(0, nativeDepth - _lastNativeDepth) : 0);
        _lastSnowfallTotal = snowfallTotal;
        if (nativeDepth < 0.125f)
        {
            Pressure = 0;
        }
        else
        {
            float trafficFactor = Math.Max(0, volume) / (Math.Max(0, volume) + 12);
            float wearRate =
                Math.Max(0, Math.Min(1, influence)) * trafficFactor * Math.Max(0, hours) * 4;
            float snowBurial = freshSnowDepth / 12;
            // traffic and fresh snow compete here, the exponential update keeps wear
            // gradual without making the result depend on the update interval
            float equilibriumPressure = wearRate > 0 ? 1 - snowBurial / wearRate : 0;
            float nextPressure =
                wearRate > 0
                    ? equilibriumPressure
                        + (Pressure - equilibriumPressure) * (float)Math.Exp(-wearRate)
                    : Pressure - snowBurial;
            Pressure = Math.Max(0, Math.Min(1, nextPressure));
        }

        _lastNativeDepth = nativeDepth;
    }
}
