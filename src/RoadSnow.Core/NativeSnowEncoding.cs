using System;

namespace RoadSnow.Core;

public static class NativeSnowEncoding
{
    // native snow depth is nonlinear, keep this conversion in sync with SnowCommon.hlsl
    // CPU weathering and GPu visibility need to agree on the depth in millimeters
    public static float DepthMm(float encoded)
    {
        Guard.NonNegative(encoded, nameof(encoded));
        float value = Math.Min(1, encoded);
        return value * value * 8000f;
    }
}
