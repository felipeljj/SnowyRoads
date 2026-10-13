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
    private int _pendingBurial;
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
                ApplyBurial();
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

                if (at != _pixels.Length || inflater.ReadByte() != -1)
                    throw new InvalidDataException("Invalid snow tile pixel data.");
                _packed = null;
            }
            ApplyBurial();
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

        ApplyBurial();

        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.Fastest, true))
        {
            deflate.Write(_pixels, 0, _pixels.Length);
        }

        _packed = output.ToArray();
        _pixels = null;
    }

    public void Write(BinaryWriter writer)
    {
        // Materialize deferred weather before serializing the unchanged save format.
        if (_pendingBurial > 0)
            _ = Pixels;
        Compress();
        writer.Write(X);
        writer.Write(Z);
        writer.Write(Layer);
        writer.Write(WeatherX);
        writer.Write(WeatherZ);
        writer.Write(_lastDepth);
        writer.Write(_burialRemainder);
        writer.Write(ObservedSnowfall);
        writer.Write(_packed?.Length ?? 0);
        if (_packed != null)
            writer.Write(_packed);
    }

    public static WearTile Read(BinaryReader reader)
    {
        var tile = new WearTile(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(), 0);
        tile.WeatherX = reader.ReadSingle();
        tile.WeatherZ = reader.ReadSingle();
        tile._lastDepth = reader.ReadSingle();
        tile._burialRemainder = reader.ReadSingle();
        tile.ObservedSnowfall = reader.ReadDouble();
        int length = reader.ReadInt32();
        if (length < 0 || length > SizeLimit)
            throw new InvalidDataException("Invalid snow tile size.");
        tile._packed = reader.ReadBytes(length);
        if (tile._packed.Length != length)
            throw new EndOfStreamException();
        tile.Empty = length == 0;
        if (!tile.Empty)
        {
            var packed = tile._packed;
            _ = tile.Pixels;
            tile._pixels = null;
            tile._packed = packed;
        }
        tile.Revision = 1;
        return tile;
    }

    private const int SizeLimit = WearHistory.Size * WearHistory.Size * 4 + 1024;

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
                _pendingBurial = 0;
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
        _pendingBurial = Math.Min(255, _pendingBurial + bury);
        Revision++;
        if (_pendingBurial >= 255)
        {
            // Full burial is known without inflating or walking 16K pixels.
            Empty = true;
            _pixels = _packed = null;
            _pendingBurial = 0;
        }
    }

    private void ApplyBurial()
    {
        if (_pendingBurial == 0 || _pixels == null)
            return;
        // Distant/offscreen history stays compressed. Apply its accumulated
        // snowfall only when a stamp, GPU upload or save actually needs pixels.
        bool any = false;
        for (int i = 0; i < _pixels.Length; i += 4)
        {
            _pixels[i] = (byte)Math.Max(0, _pixels[i] - _pendingBurial);
            any |= _pixels[i] != 0;
        }
        _pendingBurial = 0;
        Empty = !any;
    }
}
