using System;
using Colossal.Mathematics;
using Unity.Mathematics;
using UnityEngine;

namespace RoadSnow.Mod;

internal static class PlowSweep
{
    public const float MinimumDistance = .001f;

    // Retain the previous sample until real movement accumulates. Updating it
    // for every submillimeter step would discard a slowly moving plow's path.
    public static bool Accumulate(float distanceSquared, bool sameLane) =>
        sameLane && distanceSquared < MinimumDistance * MinimumDistance;

    // Limit chord error to a quarter of a history texel. Long straight sweeps
    // need far fewer stamps than .45 m slices, while bends retain their shape.
    public static int Steps(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
    {
        float second = 6 * Mathf.Max((a - 2 * b + c).magnitude, (b - 2 * c + d).magnitude);
        float bound =
            Mathf.Max((b - a).magnitude, Mathf.Max((c - b).magnitude, (d - c).magnitude)) * 3;
        return Math.Max(
            1,
            Math.Max(Mathf.CeilToInt(bound / 4), Mathf.CeilToInt(Mathf.Sqrt(second / .125f)))
        );
    }

    public static bool ProjectLane(
        Bezier4x3 curve,
        Vector3 position,
        float halfWidth,
        out Vector3 surface
    )
    {
        // Only a fallback for a real road lane while overlay geometry is absent
        // (especially junctions). Never substitute terrain underneath a bridge.
        float best = float.MaxValue,
            at = 0;
        for (int i = 0; i <= 32; i++)
        {
            float t = i / 32f;
            float3 p = MathUtils.Position(curve, t);
            float distance = math.lengthsq(p.xz - new float2(position.x, position.z));
            if (distance < best)
            {
                best = distance;
                at = t;
            }
        }
        float lo = Mathf.Max(0, at - 1f / 32),
            hi = Mathf.Min(1, at + 1f / 32);
        for (int i = 0; i < 10; i++)
        {
            float l = Mathf.Lerp(lo, hi, 1f / 3),
                r = Mathf.Lerp(lo, hi, 2f / 3);
            float2 query = new(position.x, position.z);
            if (
                math.lengthsq(MathUtils.Position(curve, l).xz - query)
                < math.lengthsq(MathUtils.Position(curve, r).xz - query)
            )
                hi = r;
            else
                lo = l;
        }
        float3 closest = MathUtils.Position(curve, (lo + hi) * .5f);
        surface = new Vector3(position.x, closest.y, position.z);
        return math.lengthsq(closest.xz - new float2(position.x, position.z))
                <= halfWidth * halfWidth
            && Mathf.Abs(closest.y - position.y) < 2.5f;
    }
}
