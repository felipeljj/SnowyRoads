using System;

namespace RoadSnow.Core;

// Bounded, derived atlas data. Never replaces the full-resolution saved history.
public sealed class WearOverviewCache
{
    public const int Size = 16;
    private readonly LruCache<WearTile, Entry> _entries;
    public long BlocksBuilt { get; private set; }
    public long BlocksRetained { get; private set; }

    public WearOverviewCache(int capacity = 1024) => _entries = new(capacity);

    public bool TryGet(WearTile tile, out byte[] pixels)
    {
        if (_entries.TryGetValue(tile, out var entry) && entry.Revision == tile.Revision)
        {
            pixels = entry.Pixels;
            return true;
        }
        pixels = null!;
        return false;
    }

    public byte[] Build(WearTile tile, byte[] source)
    {
        if (source.Length != WearHistory.Size * WearHistory.Size * 4)
            throw new ArgumentException("Invalid wear tile pixels.", nameof(source));
        if (!_entries.TryGetValue(tile, out var entry))
            entry = new Entry();
        const int scale = WearHistory.Size / Size;
        for (int i = 0; i < Size * Size; i++)
        {
            if (entry.Ready && !tile.OverviewBlockChanged(entry.Revision, i))
            {
                BlocksRetained++;
                continue;
            }
            BlocksBuilt++;
            int sum = 0,
                best = 0,
                heightAt = 0;
            int sx = (i % Size) * scale,
                sz = (i / Size) * scale;
            for (int dz = 0; dz < scale; dz++)
            for (int dx = 0; dx < scale; dx++)
            {
                int at = ((sz + dz) * WearHistory.Size + sx + dx) * 4;
                sum += source[at];
                if (source[at] > best)
                {
                    best = source[at];
                    heightAt = at;
                }
            }
            int destination = i * 4;
            entry.Pixels[destination] = (byte)(sum / (scale * scale));
            entry.Pixels[destination + 1] = source[heightAt + 1];
            entry.Pixels[destination + 2] = source[heightAt + 2];
            entry.Pixels[destination + 3] = 255;
        }
        entry.Revision = tile.Revision;
        entry.Ready = true;
        tile.AcknowledgeOverview();
        _entries.Set(tile, entry);
        return entry.Pixels;
    }

    private sealed class Entry
    {
        public readonly byte[] Pixels = new byte[Size * Size * 4];
        public int Revision;
        public bool Ready;
    }
}
