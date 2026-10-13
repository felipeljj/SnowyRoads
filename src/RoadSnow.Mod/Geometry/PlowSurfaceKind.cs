using Game.Prefabs;
using RoadSnow.Core;
using Unity.Mathematics;

namespace RoadSnow.Mod;

internal enum PlowSurfaceKind : byte
{
    Unknown,
    Road,
    Excluded,
    Median,
}

internal static class PlowSurfaceClassification
{
    // The game's composition builder tags the pieces containing real lanes.
    // A raised bus/bicycle lane is still road; its world height is not a type.
    public static PlowSurfaceKind Classify(NetPieceFlags piece, NetSectionFlags section)
    {
        if ((piece & (NetPieceFlags.HasRoadLanes | NetPieceFlags.HasBicycleLanes)) != 0)
            return PlowSurfaceKind.Road;
        if ((piece & NetPieceFlags.BlockTraffic) != 0)
            return PlowSurfaceKind.Excluded;
        if ((section & NetSectionFlags.Median) != 0)
            return PlowSurfaceKind.Median;
        return PlowSurfaceKind.Unknown;
    }

    public static bool IsRoad(PlowSurfaceKind kind, float height, float roadHeight) =>
        kind == PlowSurfaceKind.Road
        || (kind == PlowSurfaceKind.Unknown || kind == PlowSurfaceKind.Median)
            && !float.IsNaN(height)
            && height <= roadHeight + .08f;

    public static bool IsRoad(
        PlowSurfaceKind kind,
        float height,
        float roadHeight,
        SurfaceBand[] profile,
        float2? range
    )
    {
        if (kind == PlowSurfaceKind.Excluded)
            return false;
        // Median also names the native central deformation domain. A flat
        // asphalt fan there is not a raised physical traffic island.
        if (kind == PlowSurfaceKind.Median && height > roadHeight + .08f)
            return false;
        if (!range.HasValue || profile == null)
            return IsRoad(kind, height, roadHeight);
        // A native piece may contain both a lane and its lifted curb. Its lane
        // tag alone must not make that curb clearable along with the asphalt.
        foreach (var band in profile)
            if (
                !band.Pedestrian
                && (kind == PlowSurfaceKind.Road || height <= band.Height + .08f)
                && range.Value.y > band.Left + .001f
                && range.Value.x < band.Right - .001f
            )
                return true;
        return false;
    }
}
