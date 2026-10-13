using System;
using System.Collections.Generic;

namespace RoadSnow.Core;

public sealed class WearHistory
{
    // keep impressions in worldspace tiles so moving the camera does not erase them
    // the rendering atlas can evict a tile without deleting its history here
    public const int Size = 128;
    public const float Cell = 0.0625f;
    public const float TileMeters = Size * Cell;
    private readonly Dictionary<(int, int, int), WearTile> _lookup = new();
    public readonly List<WearTile> Tiles = new();
    public int Revision { get; private set; }

    private int _compressCursor;

    public void Clear()
    {
        _lookup.Clear();
        Tiles.Clear();
        _compressCursor = 0;
        Revision++;
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
        double snowfallTotal = -1
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
        for (int z = minZ; z <= maxZ; z++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                double px = (x + 0.5) * Cell - ax;
                double pz = (z + 0.5) * Cell - az;
                double along = px * fx + pz * fz;
                double side = px * fz - pz * fx;
                float strength;
                if (foot)
                {
                    double u = side / radius * (left ? -1 : 1);
                    double v = (along / length) * 2 - 1;
                    double toe = Math.Sqrt(
                        (u + 0.08) * (u + 0.08) / 0.72 + (v - 0.3) * (v - 0.3) / 0.42
                    );
                    double heel = Math.Sqrt(u * u / 0.39 + (v + 0.59) * (v + 0.59) / 0.084);
                    strength = (float)Math.Max(0, Math.Min(1, (1 - Math.Min(toe, heel)) * 3));
                }
                else
                {
                    if (along < 0 || along > length)
                    {
                        continue;
                    }

                    double feather = Math.Max(
                        0,
                        Math.Min(1, (radius + Cell * 0.5 - Math.Abs(side)) / Cell)
                    );
                    strength = (float)(feather * Math.Min(1, width / Cell));
                }

                if (strength <= 0)
                {
                    continue;
                }

                float y = ay + (by - ay) * (float)Math.Max(0, Math.Min(1, along / length));
                int tx = (int)Math.Floor(x / (double)Size);
                int tz = (int)Math.Floor(z / (double)Size);
                int layer = (int)Math.Floor(y / 4);
                var key = (tx, tz, layer);
                if (!_lookup.TryGetValue(key, out var tile))
                {
                    tile = new WearTile(tx, tz, layer, depth)
                    {
                        WeatherX = (ax + bx) * 0.5f,
                        WeatherZ = (az + bz) * 0.5f,
                    };
                    _lookup.Add(key, tile);
                    Tiles.Add(tile);
                }

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

                var data = tile.Pixels;
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
                data[index] = (byte)Math.Min(255, data[index] + amount);
                int height = (int)Math.Max(0, Math.Min(65535, Math.Round((y + 1024) * 10)));
                data[index + 1] = (byte)height;
                data[index + 2] = (byte)(height >> 8);
                data[index + 3] = 255;
                tile.Revision++;
                tile.Empty = false;
            }
        }

        Revision++;
    }

    public float Sample(float x, float y, float z)
    {
        int tx = (int)Math.Floor(x / TileMeters), tz = (int)Math.Floor(z / TileMeters);
        if (!_lookup.TryGetValue((tx, tz, (int)Math.Floor(y / 4)), out var tile) || tile.Empty) return 0;
        int cx = Math.Min(Size-1, Math.Max(0, (int)Math.Floor((x-tx*TileMeters)/Cell)));
        int cz = Math.Min(Size-1, Math.Max(0, (int)Math.Floor((z-tz*TileMeters)/Cell)));
        return tile.Pixels[(cz*Size+cx)*4]/255f;
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
    public void Write(System.IO.BinaryWriter writer)
    {
        var live = new List<WearTile>();
        for (int i = Tiles.Count - 1; i >= 0 && live.Count < 2048; i--)
            if (!Tiles[i].Empty) live.Add(Tiles[i]);
        writer.Write(live.Count);
        for (int i = live.Count - 1; i >= 0; i--) live[i].Write(writer);
    }

    public void Read(System.IO.BinaryReader reader)
    {
        Clear();
        int count = reader.ReadInt32();
        if (count < 0 || count > 2048) throw new System.IO.InvalidDataException("Invalid snow history size.");
        for (int i = 0; i < count; i++)
        {
            var tile = WearTile.Read(reader);
            var key = (tile.X, tile.Z, tile.Layer);
            if (_lookup.ContainsKey(key)) throw new System.IO.InvalidDataException("Duplicate snow tile.");
            _lookup.Add(key, tile);
            Tiles.Add(tile);
        }
        Revision++;
    }
}
