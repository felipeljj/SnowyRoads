using System;
using System.IO;
using System.IO.Compression;

namespace RoadSnow.Core;

public sealed class WearTile
{
    public readonly int X;
    public readonly int Z;
    public readonly int Layer;
    public float CenterX => (X + 0.5f) * WearHistory.TileMeters;
    public float CenterZ => (Z + 0.5f) * WearHistory.TileMeters;
    public float CenterY => Layer * 4 + 2;

    public float WeatherX;
    public float WeatherZ;
    public int WeatherVersion { get; private set; } = -1;

    private byte[]? _pixels;
    private byte[]? _packed;
    private float _lastDepth;
    private float _burialRemainder;
    public double ObservedSnowfall { get; private set; } = -1;

    public bool Empty = true;
    public int Revision;
    public bool Resident => _pixels != null;

    public WearTile(int x, int z, int layer, float depth)
    {
        X = x;
        Z = z;
        Layer = layer;
        _lastDepth = depth;
        WeatherX = CenterX;
        WeatherZ = CenterZ;
    }

    public void ObserveSnapshot(float depth, bool snowing, int version, double snowfallTotal = -1)
    {
        if (WeatherVersion == version && ObservedSnowfall == snowfallTotal)
        {
            return;
        }

        WeatherVersion = version;
        Observe(depth, snowing, snowfallTotal);
    }

    public byte[] Pixels
    {
        get
        {
            if (_pixels != null)
            {
                return _pixels;
            }

            _pixels = new byte[WearHistory.Size * WearHistory.Size * 4];
            if (_packed != null)
            {
                using var input = new MemoryStream(_packed);
                using var inflater = new DeflateStream(input, CompressionMode.Decompress);
                int at = 0;
                int read;
                while (
                    at < _pixels.Length
                    && (read = inflater.Read(_pixels, at, _pixels.Length - at)) > 0
                )
                {
                    at += read;
                }

                _packed = null;
            }

            return _pixels;
        }
    }

    public void Compress()
    {
        // store inactive tiles compressed, pixels restores them when needed again.
        if (_pixels == null)
        {
            return;
        }

        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.Fastest, true))
        {
            deflate.Write(_pixels, 0, _pixels.Length);
        }

        _packed = output.ToArray();
        _pixels = null;
    }

    public void Observe(float depth, bool snowing, double snowfallTotal = -1)
    {
        float fresh =
            snowfallTotal >= 0
                ? (ObservedSnowfall < 0 ? 0 : (float)Math.Max(0, snowfallTotal - ObservedSnowfall))
                : (snowing ? Math.Max(0, depth - _lastDepth) : 0);
        ObservedSnowfall = snowfallTotal;
        if (depth < 0.125f)
        {
            if (!Empty)
            {
                _pixels = _packed = null;
                Empty = true;
                Revision++;
            }

            _lastDepth = depth;
            _burialRemainder = 0;
            return;
        }

        _lastDepth = depth;
        if (Empty || fresh == 0)
        {
            return;
        }

        _burialRemainder += fresh * 255 / 4;
        int bury = (int)_burialRemainder;
        if (bury == 0)
        {
            return;
        }

        _burialRemainder -= bury;
        var pixels = Pixels;
        bool any = false;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = (byte)Math.Max(0, pixels[i] - bury);
            any |= pixels[i] != 0;
        }

        Empty = !any;
        Revision++;
        if (Empty)
        {
            _pixels = _packed = null;
        }
    }
}
