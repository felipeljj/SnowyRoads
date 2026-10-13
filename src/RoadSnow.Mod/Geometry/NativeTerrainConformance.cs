using System;
using Unity.Mathematics;

namespace RoadSnow.Mod;

// The network shader samples terrain at the nearest deformation boundary, not
// at each pavement vertex. Matching that distinction keeps slopes and retaining
// pieces from poking through a separately reconstructed snow surface.
internal static class NativeTerrainConformance
{
    internal static float Height(
        float surfaceY,
        float boundaryY,
        float pieceHeight,
        float terrainY,
        int mode
    )
    {
        if (mode == 2)
            return surfaceY + math.max(terrainY - boundaryY + .2f, -pieceHeight);
        if (mode == 1)
            return surfaceY + math.min(terrainY - boundaryY - .2f, 0);
        return surfaceY;
    }

    internal static int Slice(float z, float length) =>
        math.clamp((int)Math.Round(z * 256 / math.max(.001f, length)) + 128, 0, 256);

    // Same 257 longitudinal height bounds used by BatchMeshHelpers. Global
    // min/max would misclassify the top of a ramp or a curb transition.
    internal static void AddEdge(float2[] bounds, float3 a, float3 b, float length)
    {
        int start = Slice(a.z, length),
            end = Slice(b.z, length);
        if (start > end)
        {
            (start, end) = (end, start);
            (a, b) = (b, a);
        }
        for (int i = start; i <= end; i++)
        {
            float h = math.lerp(a.y, b.y, (i - start) / (float)(end - start + 1));
            bounds[i] = new float2(math.min(bounds[i].x, h), math.max(bounds[i].y, h));
        }
    }
}
