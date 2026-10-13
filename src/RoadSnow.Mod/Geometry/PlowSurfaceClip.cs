using System;
using UnityEngine;

namespace RoadSnow.Mod;

// Reused Sutherland-Hodgman scratch; clips the actual road mesh to the traveled
// interval and asphalt band. No new geometry or per-triangle allocations.
internal sealed class PlowSurfaceClip
{
    private Vertex[] _a = new Vertex[12],
        _b = new Vertex[12];

    private struct Vertex
    {
        public Vector3 Point;
        public Vector2 UV;

        public Vertex(Vector3 point, Vector2 uv)
        {
            Point = point;
            UV = uv;
        }
    }

    public int Clip(
        Vector3 a,
        Vector3 b,
        Vector3 c,
        Vector2 ua,
        Vector2 ub,
        Vector2 uc,
        float minX,
        float maxX,
        float minY,
        float maxY,
        Action<Vector3, Vector3, Vector3> stamp,
        float? roadHeight = null,
        float roadSlope = 0
    )
    {
        _a[0] = new Vertex(a, ua);
        _a[1] = new Vertex(b, ub);
        _a[2] = new Vertex(c, uc);
        int count = 3;
        count = Plane(count, 0, minX, true);
        count = Plane(count, 0, maxX, false);
        count = Plane(count, 1, minY, true);
        count = Plane(count, 1, maxY, false);
        if (roadHeight.HasValue)
        {
            count = HeightPlane(count, roadHeight.Value, roadSlope, .12f, false);
            count = HeightPlane(count, roadHeight.Value, roadSlope, -.12f, true);
        }
        for (int i = 1; i + 1 < count; i++)
            stamp(_a[0].Point, _a[i].Point, _a[i + 1].Point);
        return Math.Max(0, count - 2);
    }

    private int Plane(int count, int axis, float boundary, bool greater)
    {
        if (count == 0)
            return 0;
        int output = 0;
        Vertex previous = _a[count - 1];
        float previousDistance = (previous.UV[axis] - boundary) * (greater ? 1 : -1);
        for (int i = 0; i < count; i++)
        {
            Vertex current = _a[i];
            float distance = (current.UV[axis] - boundary) * (greater ? 1 : -1);
            if ((distance >= 0) != (previousDistance >= 0))
            {
                float t = previousDistance / (previousDistance - distance);
                _b[output++] = new Vertex(
                    Vector3.LerpUnclamped(previous.Point, current.Point, t),
                    Vector2.LerpUnclamped(previous.UV, current.UV, t)
                );
            }
            if (distance >= 0)
                _b[output++] = current;
            previous = current;
            previousDistance = distance;
        }
        var swap = _a;
        _a = _b;
        _b = swap;
        return output;
    }

    private int HeightPlane(int count, float height, float slope, float boundary, bool greater)
    {
        if (count == 0)
            return 0;
        int output = 0;
        Vertex previous = _a[count - 1];
        float previousDistance =
            (previous.Point.y - height - previous.UV.y * slope - boundary) * (greater ? 1 : -1);
        for (int i = 0; i < count; i++)
        {
            Vertex current = _a[i];
            float distance =
                (current.Point.y - height - current.UV.y * slope - boundary) * (greater ? 1 : -1);
            if ((distance >= 0) != (previousDistance >= 0))
            {
                float t = previousDistance / (previousDistance - distance);
                _b[output++] = new Vertex(
                    Vector3.LerpUnclamped(previous.Point, current.Point, t),
                    Vector2.LerpUnclamped(previous.UV, current.UV, t)
                );
            }
            if (distance >= 0)
                _b[output++] = current;
            previous = current;
            previousDistance = distance;
        }
        var swap = _a;
        _a = _b;
        _b = swap;
        return output;
    }
}
