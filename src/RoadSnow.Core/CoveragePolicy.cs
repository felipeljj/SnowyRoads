using System;

namespace RoadSnow.Core;

public sealed class CoveragePolicy
{
    public const int MaximumVisibleRoads = 8192;
    private int _remainingDetailParts;

    public CoveragePolicy(int detailParts)
    {
        if (detailParts < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(detailParts));
        }

        _remainingDetailParts = detailParts;
    }

    public bool ReserveDetail(int parts, float metersPerPixel)
    {
        if (parts < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(parts));
        }

        Guard.NonNegative(metersPerPixel, nameof(metersPerPixel));
        if (metersPerPixel > 0.35f || parts > _remainingDetailParts)
        {
            return false;
        }

        _remainingDetailParts -= parts;
        return true;
    }

    public static float HorizontalDistanceSquared(
        float cameraX,
        float cameraZ,
        float minX,
        float minZ,
        float maxX,
        float maxZ
    )
    {
        float dx = Math.Max(minX - cameraX, Math.Max(0, cameraX - maxX));
        float dz = Math.Max(minZ - cameraZ, Math.Max(0, cameraZ - maxZ));
        return dx * dx + dz * dz;
    }

    public static float CellSize(float width, float length, bool pedestrian, bool detailed)
    {
        Guard.Positive(width, nameof(width));
        Guard.Positive(length, nameof(length));
        return detailed
            ? (pedestrian ? 0.05f : 0.18f)
            : Math.Max(pedestrian ? 0.4f : 0.8f, Math.Max(width / 48, length / 128));
    }

    public static float AtlasCellSize(float width, float length, bool pedestrian, bool detailed)
    {
        float preferred = detailed ? (pedestrian ? 0.09f : 0.14f) : 0.8f;
        int columns = detailed ? 254 : 6;
        int rows = detailed ? 254 : 30;
        return Math.Max(preferred, Math.Max(width / columns, length / rows));
    }
}
