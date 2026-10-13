using System;
using System.IO;
using System.IO.Compression;

namespace RoadSnow.Core;

public sealed class WearTile
{
    public readonly int X;
    public readonly int Z;
    public readonly int Layer;
    internal int HistoryIndex;
    public float CenterX => (X + 0.5f) * WearHistory.TileMeters;
    public float CenterZ => (Z + 0.5f) * WearHistory.TileMeters;
    public float CenterY => Layer * 4 + 2;

    public float WeatherX;
    public float WeatherZ;
    public int WeatherVersion { get; private set; } = -1;

    private byte[]? _pixels;
    private byte[]? _packed;
    private int _packedRevision = -1;
    private float _lastDepth;
    private float _burialRemainder;
    private int _pendingBurial;
    public double ObservedSnowfall { get; private set; } = -1;

    public bool Empty = true;
    public int Revision;
    public long PlowSequence;

    // Weather changes the effective image, not its stamped content. A resident
    // GPU page can subtract the exact integer burial from its uploaded pixels.
    private int _weatherRevisions;
    public int ContentRevision => unchecked(Revision - _weatherRevisions);
    public long BurialTotal { get; private set; }

    public int BurialSince(long uploadedAt) =>
        (int)Math.Min(255, Math.Max(0, BurialTotal - uploadedAt));

    private int _maximumWear;
    private int _maximumContentRevision;
    private int _overviewBaseRevision = -1;
    private int _overviewTrackedRevision = -1;
    private ulong _overview0,
        _overview1,
        _overview2,
        _overview3;
    public bool Resident => _pixels != null;

    internal void PixelChanged(int byteIndex)
    {
        bool maximumKnown = _maximumContentRevision == ContentRevision;
        // Weather and external pixel writers change Revision directly. Their
        // extent is unknown, so any such gap invalidates the complete overview.
        if (_overviewTrackedRevision != Revision || byteIndex == 0)
            _overview0 = _overview1 = _overview2 = _overview3 = ulong.MaxValue;
        Revision++;
        if (maximumKnown && _pixels != null)
        {
            _maximumWear = Math.Max(_maximumWear, _pixels[byteIndex]);
            _maximumContentRevision = ContentRevision;
        }
        _overviewTrackedRevision = Revision;
        int pixel = byteIndex >> 2;
        int block = ((pixel / WearHistory.Size) >> 3) * 16 + ((pixel % WearHistory.Size) >> 3);
        ulong bit = 1UL << (block & 63);
        switch (block >> 6)
        {
            case 0:
                _overview0 |= bit;
                break;
            case 1:
                _overview1 |= bit;
                break;
            case 2:
                _overview2 |= bit;
                break;
            default:
                _overview3 |= bit;
                break;
        }
    }

    internal void SpanChanged(int firstByte, int lastByte)
    {
        _maximumContentRevision = unchecked(ContentRevision - 1);
        // Call once per affected 8-pixel overview block, not once per pixel.
        PixelChanged(firstByte);
        for (int at = (firstByte / 32 + 1) * 32; at <= lastByte; at += 32)
            PixelChanged(at);
    }

    internal bool OverviewBlockChanged(int baseRevision, int block)
    {
        if (_overviewBaseRevision != baseRevision || _overviewTrackedRevision != Revision)
            return true;
        ulong mask =
            block < 64 ? _overview0
            : block < 128 ? _overview1
            : block < 192 ? _overview2
            : _overview3;
        return (mask & (1UL << (block & 63))) != 0;
    }

    internal void AcknowledgeOverview()
    {
        _overviewBaseRevision = _overviewTrackedRevision = Revision;
        _overview0 = _overview1 = _overview2 = _overview3 = 0;
    }

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
            }
            RefreshMaximum();
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

        // A far-atlas read can inflate an unchanged tile that the background
        // compressor visits again. Reuse its lossless bytes until content changes.
        if (_packed != null && _packedRevision == Revision)
        {
            _pixels = null;
            return;
        }

        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.Fastest, true))
        {
            deflate.Write(_pixels, 0, _pixels.Length);
        }

        _packed = output.ToArray();
        _packedRevision = Revision;
        _pixels = null;
    }

    internal int PrepareSave()
    {
        // Materialize deferred weather before serializing the unchanged save format.
        if (_pendingBurial > 0)
            _ = Pixels;
        Compress();
        return 40 + (_packed?.Length ?? 0);
    }

    public void Write(BinaryWriter writer)
    {
        PrepareSave();
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
        tile._packedRevision = tile.Revision;
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
        BurialTotal += bury;
        _weatherRevisions++;
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
        int maximum = 0;
        for (int i = 0; i < _pixels.Length; i += 4)
        {
            _pixels[i] = (byte)Math.Max(0, _pixels[i] - _pendingBurial);
            maximum = Math.Max(maximum, _pixels[i]);
        }
        _pendingBurial = 0;
        Empty = maximum == 0;
        _maximumWear = maximum;
        _maximumContentRevision = ContentRevision;
    }

    private void RefreshMaximum()
    {
        _maximumWear = 0;
        if (_pixels != null)
            for (int i = 0; i < _pixels.Length; i += 4)
                _maximumWear = Math.Max(_maximumWear, _pixels[i]);
        _maximumContentRevision = ContentRevision;
    }

    // Resolve the same all-zero condition as Pixels, without materializing the
    // weather. Empty timing affects selection and subsequent snowfall rounding.
    public void ResolveVisibleWeather()
    {
        if (Empty)
            return;
        if (_maximumContentRevision != ContentRevision)
        {
            _ = Pixels;
            RefreshMaximum();
        }
        if (_maximumWear <= _pendingBurial)
            Empty = true;
    }
}
