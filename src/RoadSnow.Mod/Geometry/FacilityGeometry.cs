using Unity.Mathematics;

namespace RoadSnow.Mod;

internal static class FacilityGeometry
{
    public static bool Project(float3 p, float3 a, float3 b, float3 c, out float3 result)
    {
        result = default;
        float2 ab = b.xz - a.xz;
        float2 ac = c.xz - a.xz;
        float2 ap = p.xz - a.xz;
        float det = ab.x * ac.y - ab.y * ac.x;
        if (math.abs(det) < .00001f)
        {
            return false;
        }

        float u = (ap.x * ac.y - ap.y * ac.x) / det;
        float v = (ab.x * ap.y - ab.y * ap.x) / det;
        if (u < -.001f || v < -.001f || u + v > 1.001f)
        {
            return false;
        }

        result = a + (b - a) * u + (c - a) * v;
        return math.abs(result.y - p.y) < .8f;
    }
}
