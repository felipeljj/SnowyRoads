using System;
using System.Collections.Generic;

namespace RoadSnow.Core;

public sealed partial class WearHistory
{
    // keep impressions in worldspace tiles so moving the camera does not erase them
    // the rendering atlas can evict a tile without deleting its history here
    public const int Size = 128;
    public const float Cell = 0.0625f;
    public const float TileMeters = Size * Cell;
    private readonly Dictionary<(int, int, int), WearTile> _lookup = new();
    private readonly Dictionary<(int, int), List<WearTile>> _regions = new();
    public readonly List<WearTile> Tiles = new();
    public int Revision { get; private set; }
    public int TopologyRevision { get; private set; }

    private int _compressCursor;

    public void Clear()
    {
        _lookup.Clear();
        _regions.Clear();
        Tiles.Clear();
        _compressCursor = 0;
        _plowChanges.Clear();
        Revision++;
        TopologyRevision++;
    }

    private void AddTile(WearTile tile)
    {
        tile.HistoryIndex = Tiles.Count;
        Tiles.Add(tile);
        var key = (tile.X >> 4, tile.Z >> 4);
        if (!_regions.TryGetValue(key, out var region))
            _regions.Add(key, region = new List<WearTile>());
        region.Add(tile);
        TopologyRevision++;
    }

    // Inclusive tile coordinates, all height layers, in original history order.
    // Empty tiles stay indexed: weather and new stamps can reactivate them.
    public void QueryTiles(int minX, int minZ, int maxX, int maxZ, List<WearTile> result)
    {
        result.Clear();
        if (minX > maxX || minZ > maxZ)
            return;
        int rx0 = minX >> 4,
            rz0 = minZ >> 4,
            rx1 = maxX >> 4,
            rz1 = maxZ >> 4;
        long regionCount = ((long)rx1 - rx0 + 1) * ((long)rz1 - rz0 + 1);
        if (regionCount > _regions.Count)
        {
            foreach (var pair in _regions)
                if (
                    pair.Key.Item1 >= rx0
                    && pair.Key.Item1 <= rx1
                    && pair.Key.Item2 >= rz0
                    && pair.Key.Item2 <= rz1
                )
                    AppendRegion(pair.Value, minX, minZ, maxX, maxZ, result);
        }
        else
        {
            for (int z = rz0; z <= rz1; z++)
            for (int x = rx0; x <= rx1; x++)
                if (_regions.TryGetValue((x, z), out var region))
                    AppendRegion(region, minX, minZ, maxX, maxZ, result);
        }
        result.Sort((a, b) => a.HistoryIndex.CompareTo(b.HistoryIndex));
    }

    private static void AppendRegion(
        List<WearTile> region,
        int minX,
        int minZ,
        int maxX,
        int maxZ,
        List<WearTile> result
    )
    {
        foreach (var tile in region)
            if (tile.X >= minX && tile.X <= maxX && tile.Z >= minZ && tile.Z <= maxZ)
                result.Add(tile);
    }

    public void Stamp(
        float ax,
        float ay,
        float az,
        float bx,
        float by,
        float bz,
        float width,
        bool foot,
        bool left,
        float depth,
        bool plow = false,
        Func<WearTile, float>? weather = null,
        int weatherVersion = -1,
        bool snowing = false,
        double snowfallTotal = -1,
        bool roundCaps = false
    )
    {
        if (depth < 0.2f || width <= 0)
        {
            return;
        }

        double dx = bx - ax;
        double dz = bz - az;
        double length = Math.Sqrt(dx * dx + dz * dz);
        if (length < 0.001 || length > 5)
        {
            return;
        }

        double fx = dx / length;
        double fz = dz / length;
        double radius = width * 0.5;
        int minX = (int)Math.Floor((Math.Min(ax, bx) - radius - Cell) / Cell);
        int maxX = (int)Math.Ceiling((Math.Max(ax, bx) + radius + Cell) / Cell);
        int minZ = (int)Math.Floor((Math.Min(az, bz) - radius - Cell) / Cell);
        int maxZ = (int)Math.Ceiling((Math.Max(az, bz) + radius + Cell) / Cell);
        WearTile? previousTile = null;
        byte[]? data = null;
        bool changed = false;
        for (int z = minZ; z <= maxZ; z++)
        {
            double pz = (z + 0.5) * Cell - az;
            for (int x = minX; x <= maxX; x++)
            {
                double px = (x + 0.5) * Cell - ax;
                double along = px * fx + pz * fz;
                double side = px * fz - pz * fx;
                float strength;
                if (foot)
                {
                    double u = side / radius * (left ? -1 : 1);
                    double v = (along / length) * 2 - 1;
                    double toe = ((u + 0.08) * (u + 0.08) / 0.72 + (v - 0.3) * (v - 0.3) / 0.42);
                    double heel = u * u / 0.39 + (v + 0.59) * (v + 0.59) / 0.084;
                    strength = (float)
                        Math.Max(0, Math.Min(1, (1 - Math.Sqrt(Math.Min(toe, heel))) * 3));
                }
                else
                {
                    if (!roundCaps && (along < 0 || along > length))
                    {
                        continue;
                    }

                    double edgeDistance = Math.Abs(side);
                    if (roundCaps)
                    {
                        double beyond = Math.Max(0, Math.Max(-along, along - length));
                        edgeDistance = Math.Sqrt(side * side + beyond * beyond);
                    }
                    double feather = Math.Max(
                        0,
                        Math.Min(1, (radius + Cell * 0.5 - edgeDistance) / Cell)
                    );
                    strength = (float)(feather * Math.Min(1, width / Cell));
                }

                if (strength <= 0)
                {
                    continue;
                }

                float y = ay + (by - ay) * (float)Math.Max(0, Math.Min(1, along / length));
                int tx = x >> 7;
                int tz = z >> 7;
                int layer = (int)Math.Floor(y / 4);
                var key = (tx, tz, layer);
                // Adjacent stamp pixels usually belong to the same tile. Keep
                // the raster order unchanged (including negative coordinates).
                var tile = previousTile;
                if (tile == null || tile.X != tx || tile.Z != tz || tile.Layer != layer)
                {
                    if (!_lookup.TryGetValue(key, out tile))
                    {
                        tile = new WearTile(tx, tz, layer, depth)
                        {
                            WeatherX = (ax + bx) * 0.5f,
                            WeatherZ = (az + bz) * 0.5f,
                        };
                        _lookup.Add(key, tile);
                        AddTile(tile);
                    }
                    previousTile = tile;

                    int revisionBeforeWeather = tile.Revision;
                    if (
                        weather != null
                        && (
                            tile.WeatherVersion != weatherVersion
                            || tile.ObservedSnowfall != snowfallTotal
                        )
                    )
                    {
                        tile.ObserveSnapshot(weather(tile), snowing, weatherVersion, snowfallTotal);
                    }
                    changed |= tile.Revision != revisionBeforeWeather;

                    data = tile.Pixels;
                }
                int index = ((z - tz * Size) * Size + x - tx * Size) * 4;
                byte amount = (byte)
                    Math.Max(
                        1,
                        (int)
                            Math.Round(
                                strength
                                    * (
                                        plow ? 255
                                        : foot ? 76
                                        : 48
                                    )
                            )
                    );
                byte wear = (byte)Math.Min(255, data![index] + amount);
                int height = (int)Math.Max(0, Math.Min(65535, Math.Round((y + 1024) * 10)));
                // Saturated traffic must not dirty the atlas indefinitely. Height
                // and occupancy still invalidate even when wear is already 255.
                if (
                    data[index] == wear
                    && data[index + 1] == (byte)height
                    && data[index + 2] == (byte)(height >> 8)
                    && data[index + 3] == 255
                )
                    continue;
                data[index] = wear;
                data[index + 1] = (byte)height;
                data[index + 2] = (byte)(height >> 8);
                data[index + 3] = 255;
                tile.PixelChanged(index);
                tile.Empty = false;
                changed = true;
            }
        }

        if (changed)
            Revision++;
    }

    public float Sample(float x, float y, float z)
    {
        int tx = (int)Math.Floor(x / TileMeters),
            tz = (int)Math.Floor(z / TileMeters);
        if (!_lookup.TryGetValue((tx, tz, (int)Math.Floor(y / 4)), out var tile) || tile.Empty)
            return 0;
        int cx = Math.Min(Size - 1, Math.Max(0, (int)Math.Floor((x - tx * TileMeters) / Cell)));
        int cz = Math.Min(Size - 1, Math.Max(0, (int)Math.Floor((z - tz * TileMeters) / Cell)));
        return tile.Pixels[(cz * Size + cx) * 4] / 255f;
    }

    public void CompressBackground(ISet<WearTile> visible)
    {
        int checkedTiles = 0;
        int compressed = 0;
        while (Tiles.Count > 0 && checkedTiles++ < Math.Min(64, Tiles.Count) && compressed < 4)
        {
            if (_compressCursor >= Tiles.Count)
            {
                _compressCursor = 0;
            }

            var tile = Tiles[_compressCursor++];
            if (visible.Contains(tile) || !tile.Resident)
            {
                continue;
            }

            tile.Compress();
            compressed++;
        }
    }

    // A bounded recent history keeps saves manageable; lane wear is saved separately.
    public int LastSaveOmitted { get; private set; }

    public void Write(
        System.IO.BinaryWriter writer,
        int maximumTiles = 2048,
        int maximumBytes = int.MaxValue
    )
    {
        var live = new List<WearTile>();
        long bytes = 4;
        LastSaveOmitted = 0;
        for (int i = Tiles.Count - 1; i >= 0; i--)
        {
            var tile = Tiles[i];
            if (tile.Empty)
                continue;
            if (live.Count >= maximumTiles)
            {
                LastSaveOmitted++;
                continue;
            }
            int size = tile.PrepareSave();
            if (bytes + size > maximumBytes)
            {
                LastSaveOmitted++;
                continue;
            }
            bytes += size;
            live.Add(tile);
        }
        writer.Write(live.Count);
        for (int i = live.Count - 1; i >= 0; i--)
            live[i].Write(writer);
    }

    public void Read(System.IO.BinaryReader reader, int maximumTiles = 2048)
    {
        Clear();
        int count = reader.ReadInt32();
        if (count < 0 || count > maximumTiles)
            throw new System.IO.InvalidDataException("Invalid snow history size.");
        for (int i = 0; i < count; i++)
        {
            var tile = WearTile.Read(reader);
            var key = (tile.X, tile.Z, tile.Layer);
            if (_lookup.ContainsKey(key))
                throw new System.IO.InvalidDataException("Duplicate snow tile.");
            _lookup.Add(key, tile);
            AddTile(tile);
        }
        Revision++;
    }
}
