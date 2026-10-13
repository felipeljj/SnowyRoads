using System;
using System.Collections.Generic;

namespace RoadSnow.Core;

// Exact distant reduction under snowfall. A block histogram is sufficient to
// compute sum(max(pixel - burial, 0)); subtracting from an average is not.
// Derived, bounded data only: the full resolution history remains authoritative.
public sealed class WeatherOverviewCache
{
    private readonly LruCache<WearTile, Entry> _entries;
    private readonly byte[] _counts = new byte[256],
        _values = new byte[64];
    public long WeatherReuses { get; private set; }
    public long Builds { get; private set; }

    public WeatherOverviewCache(int capacity = 4096) => _entries = new(capacity);

    public bool TryGet(WearTile tile, out byte[] pixels)
    {
        pixels = null!;
        if (!_entries.TryGetValue(tile, out var entry) || entry.Content != tile.ContentRevision)
            return false;
        int burial = tile.BurialSince(entry.Burial);
        if (burial < entry.Applied)
            return false;
        if (burial != entry.Applied)
        {
            for (int block = 0; block < 256; block++)
            {
                entry.Sum[block] -= entry.Active[block] * (burial - entry.Applied);
                int cursor = entry.Cursor[block];
                while (cursor < entry.End[block])
                {
                    int run = entry.Runs[cursor],
                        value = run >> 8,
                        count = run & 255;
                    if (value > burial)
                        break;
                    entry.Sum[block] += count * (burial - value);
                    entry.Active[block] -= count;
                    cursor++;
                }
                entry.Cursor[block] = (ushort)cursor;
                int at = block * 4;
                entry.Pixels[at] = (byte)(entry.Sum[block] / 64);
                if (entry.Active[block] == 0)
                {
                    entry.Pixels[at + 1] = entry.ZeroLo;
                    entry.Pixels[at + 2] = entry.ZeroHi;
                }
            }
            entry.Applied = burial;
            WeatherReuses++;
        }
        pixels = entry.Pixels;
        return true;
    }

    public byte[] Build(WearTile tile, byte[] source)
    {
        if (source.Length != WearHistory.Size * WearHistory.Size * 4)
            throw new ArgumentException("Invalid wear image.", nameof(source));
        if (!_entries.TryGetValue(tile, out var entry))
            entry = new Entry();
        entry.Runs.Clear();
        entry.ZeroLo = source[1];
        entry.ZeroHi = source[2];
        for (int block = 0; block < 256; block++)
        {
            int sum = 0,
                best = 0,
                heightAt = 0,
                active = 0,
                distinct = 0;
            int sx = block % 16 * 8,
                sz = block / 16 * 8;
            for (int z = 0; z < 8; z++)
            for (int x = 0; x < 8; x++)
            {
                int at = ((sz + z) * WearHistory.Size + sx + x) * 4;
                int value = source[at];
                sum += value;
                if (value > 0)
                {
                    active++;
                    if (_counts[value]++ == 0)
                        _values[distinct++] = (byte)value;
                }
                if (value > best)
                {
                    best = value;
                    heightAt = at;
                }
            }
            entry.Sum[block] = sum;
            entry.Active[block] = active;
            Array.Sort(_values, 0, distinct);
            entry.Cursor[block] = (ushort)entry.Runs.Count;
            for (int i = 0; i < distinct; i++)
            {
                int value = _values[i];
                entry.Runs.Add((ushort)((value << 8) | _counts[value]));
                _counts[value] = 0;
            }
            entry.End[block] = (ushort)entry.Runs.Count;
            int output = block * 4;
            entry.Pixels[output] = (byte)(sum / 64);
            entry.Pixels[output + 1] = source[heightAt + 1];
            entry.Pixels[output + 2] = source[heightAt + 2];
            entry.Pixels[output + 3] = 255;
        }
        entry.Content = tile.ContentRevision;
        entry.Burial = tile.BurialTotal;
        entry.Applied = 0;
        _entries.Set(tile, entry);
        Builds++;
        return entry.Pixels;
    }

    private sealed class Entry
    {
        public readonly List<ushort> Runs = new();
        public readonly ushort[] Cursor = new ushort[256],
            End = new ushort[256];
        public readonly int[] Sum = new int[256],
            Active = new int[256];
        public readonly byte[] Pixels = new byte[1024];
        public byte ZeroLo,
            ZeroHi;
        public int Content,
            Applied;
        public long Burial;
    }
}
