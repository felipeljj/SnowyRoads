using System;
using System.Collections.Generic;

namespace RoadSnow.Core;

public sealed partial class WearHistory
{
    // Scan filled spans, not a wide bounding box with a distance calculation and
    // dictionary lookup at every pixel. Uses the original 6.25 cm history/save format.
    public long PlowPixelsVisited { get; private set; }
    private long _plowSequence;
    private readonly HashSet<WearTile> _plowChanges = new();

    public void DrainPlowChanges(List<WearTile> changed)
    {
        changed.Clear();
        changed.AddRange(_plowChanges);
        _plowChanges.Clear();
    }

    public void StampPlowTriangle(
        float ax,
        float ay,
        float az,
        float bx,
        float by,
        float bz,
        float cx,
        float cy,
        float cz,
        float depth,
        Func<WearTile, float>? weather = null,
        int weatherVersion = -1,
        bool snowing = false,
        double snowfallTotal = -1,
        PlowFeather feather = default
    )
    {
        if (
            !FinitePlowPoint(ax, ay, az)
            || !FinitePlowPoint(bx, by, bz)
            || !FinitePlowPoint(cx, cy, cz)
            || float.IsNaN(depth)
            || float.IsInfinity(depth)
        )
            return;
        double determinant = (bx - ax) * (double)(cz - az) - (bz - az) * (double)(cx - ax);
        if (Math.Abs(determinant) < .000001 || depth < .125f)
            return;
        double dydx = ((by - ay) * (double)(cz - az) - (cy - ay) * (double)(bz - az)) / determinant;
        double dydz = ((bx - ax) * (double)(cy - ay) - (cx - ax) * (double)(by - ay)) / determinant;
        int z0 = (int)Math.Ceiling(Math.Min(az, Math.Min(bz, cz)) / Cell - .5);
        int z1 = (int)Math.Floor(Math.Max(az, Math.Max(bz, cz)) / Cell - .5);
        // Reject corrupt geometry rather than running an unbounded raster loop.
        if (z1 - z0 > 4096 || Math.Max(ax, Math.Max(bx, cx)) - Math.Min(ax, Math.Min(bx, cx)) > 256)
            return;
        bool changed = false;
        for (int z = z0; z <= z1; z++)
        {
            double worldZ = (z + .5) * Cell,
                left = double.MaxValue,
                right = double.MinValue;
            Intersect(ax, az, bx, bz, worldZ, ref left, ref right);
            Intersect(bx, bz, cx, cz, worldZ, ref left, ref right);
            Intersect(cx, cz, ax, az, worldZ, ref left, ref right);
            if (left > right)
                continue;
            int x0 = (int)Math.Ceiling(left / Cell - .5 - .00001);
            int x1 = (int)Math.Floor(right / Cell - .5 + .00001);
            PlowPixelsVisited += Math.Max(0, x1 - x0 + 1);
            WearTile? tile = null;
            byte[]? data = null;
            int firstChanged = -1,
                lastChanged = -1;
            int residueBlock = int.MinValue;
            bool residue = false;
            for (int x = x0; x <= x1; x++)
            {
                double y = ay + dydx * ((x + .5) * Cell - ax) + dydz * (worldZ - az);
                int tx = x >> 7,
                    tz = z >> 7,
                    layer = (int)Math.Floor(y / 4);
                if (tile == null || tile.X != tx || tile.Z != tz || tile.Layer != layer)
                {
                    if (firstChanged >= 0)
                        tile!.SpanChanged(firstChanged, lastChanged);
                    firstChanged = lastChanged = -1;
                    if (!_lookup.TryGetValue((tx, tz, layer), out tile))
                    {
                        tile = new WearTile(tx, tz, layer, depth)
                        {
                            WeatherX = (ax + bx + cx) / 3,
                            WeatherZ = (az + bz + cz) / 3,
                        };
                        _lookup.Add((tx, tz, layer), tile);
                        AddTile(tile);
                    }
                    int before = tile.Revision;
                    if (
                        weather != null
                        && (
                            tile.WeatherVersion != weatherVersion
                            || tile.ObservedSnowfall != snowfallTotal
                        )
                    )
                        tile.ObserveSnapshot(weather(tile), snowing, weatherVersion, snowfallTotal);
                    changed |= before != tile.Revision;
                    data = tile.Pixels;
                }
                if ((x >> 3) != residueBlock)
                {
                    residueBlock = x >> 3;
                    uint hash = unchecked(
                        (uint)residueBlock * 374761393u + (uint)(z >> 3) * 668265263u + 1984u
                    );
                    hash = unchecked((hash ^ (hash >> 13)) * 1274126177u);
                    residue = ((hash ^ (hash >> 16)) & 31) == 0;
                }
                int dx = (x & 7) * 2 - 7,
                    dz = (z & 7) * 2 - 7;
                byte strength = (byte)(
                    255 - (residue ? Math.Max(0, 14 - dx * dx - dz * dz) * 3 : 0)
                );
                strength = (byte)
                    Math.Round(strength * feather.Weight((x + .5f) * Cell, (z + .5f) * Cell));
                int at = (((z & 127) << 7) + (x & 127)) * 4;
                int height = (int)Math.Max(0, Math.Min(65535, Math.Round((y + 1024) * 10)));
                byte wear = Math.Max(data![at], strength);
                if (
                    data[at] == wear
                    && data[at + 1] == (byte)height
                    && data[at + 2] == (byte)(height >> 8)
                    && data[at + 3] == 255
                )
                    continue;
                data[at] = wear;
                data[at + 1] = (byte)height;
                data[at + 2] = (byte)(height >> 8);
                data[at + 3] = 255;
                if (firstChanged < 0)
                {
                    tile.PlowSequence = ++_plowSequence;
                    _plowChanges.Add(tile);
                    firstChanged = at;
                }
                lastChanged = at;
                tile.Empty = false;
                changed = true;
            }
            if (firstChanged >= 0)
                tile!.SpanChanged(firstChanged, lastChanged);
        }
        if (changed)
            Revision++;
    }

    private static bool FinitePlowPoint(float x, float y, float z) =>
        Math.Abs(x) <= 100000 && Math.Abs(y) <= 100000 && Math.Abs(z) <= 100000;

    private static void Intersect(
        double ax,
        double az,
        double bx,
        double bz,
        double z,
        ref double left,
        ref double right
    )
    {
        if (z < Math.Min(az, bz) || z > Math.Max(az, bz) || Math.Abs(bz - az) < 1e-12)
            return;
        double x = ax + (bx - ax) * ((z - az) / (bz - az));
        left = Math.Min(left, x);
        right = Math.Max(right, x);
    }
}
