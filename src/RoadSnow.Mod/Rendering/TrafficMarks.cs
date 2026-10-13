using System;
using System.Collections.Generic;
using RoadSnow.Core;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace RoadSnow.Mod;

internal sealed class TrafficMarks : IDisposable
{
    // This atlas is a GPU cache of WearHistory, not the history itself. The page table
    // maps world tiles to reusable slots, including separate layers for stacked roads.
    private readonly int AtlasSize;
    private readonly int TileResolution;
    private readonly bool _distant;
    private readonly string _binding;
    private readonly int SlotsPerSide;
    private readonly WearHistory _history;
    private readonly int PageSide;
    private const int PageLayers = 4;
    private readonly Texture2D _pages;
    private readonly Color32[] _pagePixels;
    private readonly int[] _pageLayers;
    private readonly List<int> _occupiedPages = new();
    private readonly HashSet<int> _nextOccupiedPages = new();
    private readonly ComputeShader _probe;
    private readonly ComputeBuffer _probeResult;
    private Vector4 _area;
    private bool _disposed;
    private readonly Texture2D _texture;
    private readonly List<Candidate> _candidates = new();
    private readonly HashSet<WearTile> _selected = new();
    private readonly Dictionary<WearTile, Slot> _slots = new();
    private readonly List<WearTile> _evict = new();
    private readonly List<int> _free = new();
    private readonly Color32[] _upload;
    private int _weatherCursor;
    private int _uploadCursor;
    private readonly HashSet<WearTile> _uploadSelection = new();
    private int _drawRevision = -1;
    private int _drawSnapshot = -1;
    private int _drawBudget;
    private Matrix4x4 _drawView;
    private bool _drawSnowing;
    public int Drawn { get; private set; }
    public int Stored => _history.Tiles.Count;
    public double LastDrawMs { get; private set; }

    private Func<Vector3, float> _depth;
    private readonly Func<WearTile, float> _tileDepth;
    private int _weatherVersion;
    private bool _snowing;
    private double _snowfallTotal = -1;
    private double _drawSnowfallTotal = -1;
    public int LiveTiles { get; private set; }
    public int ViewCandidates { get; private set; }
    public Vector3 ProbePoint { get; private set; }
    public float ProbeExpected { get; private set; }

    private bool _probePending;

    public TrafficMarks(ComputeShader probe, WearHistory history, bool distant = false)
    {
        _distant = distant;
        _binding = distant ? "_RoadSnowDistantTrack" : "_RoadSnowTrack";
        AtlasSize = distant ? 1024 : 2048;
        TileResolution = distant ? 16 : WearHistory.Size;
        SlotsPerSide = AtlasSize / TileResolution;
        PageSide = distant ? 512 : 128;
        _pagePixels = new Color32[PageSide * PageSide * PageLayers];
        _pageLayers = new int[PageSide * PageSide];
        _upload = new Color32[TileResolution * TileResolution];
        _history = history;
        _tileDepth = tile => _depth(new Vector3(tile.WeatherX, tile.CenterY, tile.WeatherZ));
        _texture = new Texture2D(AtlasSize, AtlasSize, TextureFormat.RGBA32, false, true)
        {
            name = "RoadSnow individual impressions",
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp,
        };
        _pages = new Texture2D(PageSide, PageSide * PageLayers, TextureFormat.RGBA32, false, true)
        {
            name = "RoadSnow impression page table",
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp,
        };
        _pages.SetPixels32(_pagePixels);
        _pages.Apply(false, false);
        if (probe != null)
        {
            _probe = Object.Instantiate(probe);
            _probeResult = new ComputeBuffer(1, 16);
        }

        for (int i = SlotsPerSide * SlotsPerSide - 1; i >= 0; i--)
        {
            _free.Add(i);
        }

        Bind();
    }

    private void Bind()
    {
        Shader.SetGlobalVector(_binding + "Area", _area);
        Shader.SetGlobalTexture(_binding + "Atlas", _texture);
        Shader.SetGlobalTexture(_binding + "Pages", _pages);
    }

    public void Add(
        Vector3 from,
        Vector3 to,
        bool footprint,
        float initialDepth,
        bool leftFoot = false,
        float widthMeters = 0.38f,
        bool plow = false
    ) =>
        _history.Stamp(
            from.x,
            from.y,
            from.z,
            to.x,
            to.y,
            to.z,
            widthMeters,
            footprint,
            leftFoot,
            initialDepth,
            plow,
            _depth == null ? null : _tileDepth,
            _weatherVersion,
            _snowing,
            _snowfallTotal
        );

    public void SetWeather(
        Func<Vector3, float> depth,
        int version,
        bool snowing,
        double snowfallTotal = -1
    )
    {
        _depth = depth;
        _weatherVersion = version;
        _snowing = snowing;
        _snowfallTotal = snowfallTotal;
    }

    public void Draw(
        Camera camera,
        int budget,
        Color snowColor,
        Func<Vector3, float> depth,
        int snapshotVersion,
        bool snowing,
        double snowfallTotal = -1
    )
    {
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            DrawCore(camera, budget, snowColor, depth, snapshotVersion, snowing, snowfallTotal);
        }
        finally
        {
            LastDrawMs =
                (System.Diagnostics.Stopwatch.GetTimestamp() - started)
                * 1000.0
                / System.Diagnostics.Stopwatch.Frequency;
        }
    }

    private void DrawCore(
        Camera camera,
        int budget,
        Color snowColor,
        Func<Vector3, float> depth,
        int snapshotVersion,
        bool snowing,
        double snowfallTotal
    )
    {
        SetWeather(depth, snapshotVersion, snowing, snowfallTotal);
        var cameraView = new WearView.Snapshot(camera);
        Matrix4x4 view = cameraView.Matrix;
        if (
            _drawRevision == _history.Revision
            && _drawSnapshot == snapshotVersion
            && _drawBudget == budget
            && _drawView == view
            && _drawSnowing == snowing
            && _drawSnowfallTotal == snowfallTotal
        )
        {
            if (!_distant)
                _history.CompressBackground(_selected);
            return;
        }

        _drawRevision = _history.Revision;
        _drawSnapshot = snapshotVersion;
        _drawBudget = budget;
        _drawView = view;
        _drawSnowing = snowing;
        _drawSnowfallTotal = snowfallTotal;
        int count = _history.Tiles.Count;
        for (int i = 0; i < Math.Min(64, count); i++)
        {
            if (_weatherCursor >= count)
            {
                _weatherCursor = 0;
            }

            var tile = _history.Tiles[_weatherCursor++];
            tile.ObserveSnapshot(_tileDepth(tile), snowing, snapshotVersion, snowfallTotal);
        }

        _candidates.Clear();
        _selected.Clear();
        Vector3 eye = cameraView.Eye;
        LiveTiles = 0;
        foreach (var tile in _history.Tiles)
        {
            if (tile.Empty)
            {
                continue;
            }

            LiveTiles++;
            Vector3 center = Center(tile);
            float distanceSquared = (center - eye).sqrMagnitude;
            if (distanceSquared > (_distant ? 5000 * 5000 : 1600 * 1600))
            {
                continue;
            }

            float mpp = cameraView.PixelSize(distanceSquared);
            if (!_distant && mpp >= 0.65f)
            {
                continue;
            }

            Vector3 screen = cameraView.Viewport(center);
            if (
                screen.z <= 0
                || screen.x < -0.15f
                || screen.x > 1.15f
                || screen.y < -0.15f
                || screen.y > 1.15f
            )
            {
                continue;
            }

            float focusDistance = cameraView.Priority(screen, distanceSquared);
            _candidates.Add(
                new Candidate(
                    tile,
                    focusDistance,
                    _distant ? 1 : 1 - Mathf.SmoothStep(0, 1, Mathf.InverseLerp(.25f, .65f, mpp))
                )
            );
        }

        _candidates.Sort(Candidate.ByDistance);
        ViewCandidates = _candidates.Count;
        Vector3 focus = _candidates.Count > 0 ? Center(_candidates[0].Tile) : Vector3.zero;
        float rasterMeters = PageSide * WearHistory.TileMeters;
        int originX = Mathf.FloorToInt(focus.x / WearHistory.TileMeters) - PageSide / 2;
        int originZ = Mathf.FloorToInt(focus.z / WearHistory.TileMeters) - PageSide / 2;
        _area = new Vector4(
            originX * WearHistory.TileMeters,
            originZ * WearHistory.TileMeters,
            1 / rasterMeters,
            0
        );
        _candidates.RemoveAll(c =>
            c.Tile.X < originX
            || c.Tile.X >= originX + PageSide
            || c.Tile.Z < originZ
            || c.Tile.Z >= originZ + PageSide
        );
        int limit = _distant
            ? SlotsPerSide * SlotsPerSide
            : Mathf.Clamp(budget / 8, 64, SlotsPerSide * SlotsPerSide);
        if (_candidates.Count > limit)
        {
            _candidates.RemoveRange(limit, _candidates.Count - limit);
        }

        foreach (var candidate in _candidates)
        {
            _selected.Add(candidate.Tile);
        }

        _evict.Clear();
        foreach (var entry in _slots)
        {
            if (!_selected.Contains(entry.Key))
            {
                _evict.Add(entry.Key);
            }
        }

        foreach (var tile in _evict)
        {
            _free.Add(_slots[tile].Index);
            _slots.Remove(tile);
        }

        _candidates.Sort(Candidate.ByHeight);
        _uploadSelection.Clear();
        if (_distant)
        {
            foreach (var candidate in _candidates)
                candidate.Tile.ObserveSnapshot(
                    _tileDepth(candidate.Tile),
                    snowing,
                    snapshotVersion,
                    snowfallTotal
                );
            int countToVisit = _candidates.Count;
            for (int visited = 0; visited < countToVisit && _uploadSelection.Count < 24; visited++)
            {
                if (_uploadCursor >= countToVisit)
                    _uploadCursor = 0;
                var tile = _candidates[_uploadCursor++].Tile;
                if (
                    !tile.Empty
                    && (
                        !_slots.TryGetValue(tile, out var cached)
                        || cached.Revision != tile.Revision
                    )
                )
                    _uploadSelection.Add(tile);
            }
        }
        // Most 4 MiB distant page-table entries never change. Track occupied
        // entries so vehicle motion does not clear and upload the entire table.
        foreach (int occupied in _occupiedPages)
            _pageLayers[occupied % (PageSide * PageSide)] = 0;
        _nextOccupiedPages.Clear();
        bool pagesChanged = false;
        Drawn = 0;
        ProbeExpected = 0;
        bool uploaded = false;
        foreach (var candidate in _candidates)
        {
            var tile = candidate.Tile;
            tile.ObserveSnapshot(_tileDepth(tile), snowing, snapshotVersion, snowfallTotal);
            if (tile.Empty)
            {
                continue;
            }

            int page = (tile.Z - originZ) * PageSide + tile.X - originX;
            int layer = _pageLayers[page];
            if (layer >= PageLayers)
            {
                continue;
            }

            bool resident = _slots.TryGetValue(tile, out var slot);
            bool canUpload = !_distant || _uploadSelection.Contains(tile);
            if (!canUpload && (!resident || slot.Revision != tile.Revision))
            {
                _drawRevision = -1;
                // A deferred refresh must keep the previous image visible. Removing its
                // page entry made moving traffic punch temporary holes in the far atlas.
                if (!resident)
                    continue;
            }
            _pageLayers[page]++;
            if (!resident)
            {
                int last = _free.Count - 1;
                slot = new Slot(_free[last], -1);
                _free.RemoveAt(last);
            }

            int x = (slot.Index % SlotsPerSide) * TileResolution;
            int y = (slot.Index / SlotsPerSide) * TileResolution;
            if (slot.Revision != tile.Revision && canUpload)
            {
                var pixels = tile.Pixels;
                int scale = WearHistory.Size / TileResolution;
                if (scale == 1)
                {
                    // The fine atlas uses the same RGBA layout as history. Copy rows
                    // directly instead of decoding and rebuilding every unchanged channel.
                    CopyFineTile(pixels, x, y);
                }
                else
                {
                    for (int i = 0; i < _upload.Length; i++)
                    {
                        int sum = 0,
                            best = 0,
                            heightAt = 0;
                        int sx = (i % TileResolution) * scale,
                            sz = (i / TileResolution) * scale;
                        for (int dz = 0; dz < scale; dz++)
                        for (int dx = 0; dx < scale; dx++)
                        {
                            int at = ((sz + dz) * WearHistory.Size + sx + dx) * 4;
                            sum += pixels[at];
                            if (pixels[at] > best)
                            {
                                best = pixels[at];
                                heightAt = at;
                            }
                        }
                        _upload[i] = new Color32(
                            (byte)(sum / (scale * scale)),
                            pixels[heightAt + 1],
                            pixels[heightAt + 2],
                            255
                        );
                    }
                    _texture.SetPixels32(x, y, TileResolution, TileResolution, _upload);
                }
                slot.Revision = tile.Revision;
                uploaded = true;
            }

            _slots[tile] = slot;
            if (!_distant && ProbeExpected == 0)
            {
                var pixels = tile.Pixels;
                for (int k = 0; k < pixels.Length; k += 4)
                {
                    if (pixels[k] > 16)
                    {
                        int cell = k / 4;
                        ProbePoint = new Vector3(
                            tile.X * WearHistory.TileMeters
                                + (cell % WearHistory.Size + .5f) * WearHistory.Cell,
                            (pixels[k + 1] + pixels[k + 2] * 256) * .1f - 1024,
                            tile.Z * WearHistory.TileMeters
                                + (cell / WearHistory.Size + .5f) * WearHistory.Cell
                        );
                        ProbeExpected = pixels[k] / 255f * candidate.Fade;
                        break;
                    }
                }
            }

            int pageIndex = page + layer * PageSide * PageSide;
            var entry = new Color32(
                (byte)(slot.Index % SlotsPerSide),
                (byte)(slot.Index / SlotsPerSide),
                (byte)Mathf.RoundToInt(candidate.Fade * 255),
                255
            );
            var previous = _pagePixels[pageIndex];
            pagesChanged |=
                previous.r != entry.r
                || previous.g != entry.g
                || previous.b != entry.b
                || previous.a != entry.a;
            _pagePixels[pageIndex] = entry;
            _nextOccupiedPages.Add(pageIndex);
            Drawn++;
        }

        if (uploaded)
        {
            _texture.Apply(false, false);
        }

        foreach (int occupied in _occupiedPages)
        {
            if (_nextOccupiedPages.Contains(occupied))
                continue;
            _pagePixels[occupied] = default;
            pagesChanged = true;
        }
        _occupiedPages.Clear();
        _occupiedPages.AddRange(_nextOccupiedPages);
        if (pagesChanged)
        {
            _pages.SetPixels32(_pagePixels);
            _pages.Apply(false, false);
        }
        Bind();
        if (!_distant)
            _history.CompressBackground(_selected);
    }

    private unsafe void CopyFineTile(byte[] pixels, int x, int y)
    {
        int rowBytes = TileResolution * 4;
        var destination = _texture.GetRawTextureData<byte>();
        // Cities disables native collection safety checks. Validate both spans before
        // pinning; the pointer is used only during this synchronous copy.
        if (
            pixels.Length != rowBytes * TileResolution
            || x < 0
            || y < 0
            || x + TileResolution > AtlasSize
            || y + TileResolution > AtlasSize
            || destination.Length != AtlasSize * AtlasSize * 4
        )
            throw new InvalidOperationException("Invalid snow atlas copy bounds.");
        fixed (byte* source = pixels)
        {
            byte* target = (byte*)NativeArrayUnsafeUtility.GetUnsafePtr(destination);
            UnsafeUtility.MemCpyStride(
                target + (y * AtlasSize + x) * 4,
                AtlasSize * 4,
                source,
                rowBytes,
                rowBytes,
                TileResolution
            );
        }
    }

    private static Vector3 Center(WearTile tile) => new(tile.CenterX, tile.CenterY, tile.CenterZ);

    public void ProbeGpu(Action<string> report)
    {
        if (
            _probePending
            || _probe == null
            || ProbeExpected == 0
            || !SystemInfo.supportsAsyncGPUReadback
        )
        {
            return;
        }

        Vector3 point = ProbePoint;
        float expected = ProbeExpected;
        _probe.SetVector("_RoadSnowTrackArea", _area);
        _probe.SetVector("_ProbePosition", new Vector4(point.x, point.y, point.z, 1));
        _probe.SetTexture(0, "_RoadSnowTrackAtlas", _texture);
        _probe.SetTexture(0, "_RoadSnowTrackPages", _pages);
        _probe.SetTexture(
            0,
            "_RoadSnowDistantTrackAtlas",
            Shader.GetGlobalTexture("_RoadSnowDistantTrackAtlas")
        );
        _probe.SetTexture(
            0,
            "_RoadSnowDistantTrackPages",
            Shader.GetGlobalTexture("_RoadSnowDistantTrackPages")
        );
        _probe.SetVector(
            "_RoadSnowDistantTrackArea",
            Shader.GetGlobalVector("_RoadSnowDistantTrackArea")
        );
        _probe.SetBuffer(0, "_Result", _probeResult);
        _probe.Dispatch(0, 1, 1, 1);
        _probePending = true;
        AsyncGPUReadback.Request(
            _probeResult,
            request =>
            {
                if (_disposed)
                {
                    return;
                }

                _probePending = false;
                report(
                    request.hasError
                        ? "impression readback failed"
                        : $"individual impression GPU={request.GetData<Vector4>()[0].x:F3}, CPU={Mathf.Min(.995f, expected):F3}, world={point}, pages={Drawn}"
                );
            }
        );
    }

    public void Dispose()
    {
        _disposed = true;
        Shader.SetGlobalVector(_binding + "Area", Vector4.zero);
        Shader.SetGlobalTexture(_binding + "Atlas", Texture2D.blackTexture);
        Shader.SetGlobalTexture(_binding + "Pages", Texture2D.blackTexture);
        Object.Destroy(_texture);
        Object.Destroy(_pages);
        _probeResult?.Dispose();
        if (_probe != null)
        {
            Object.Destroy(_probe);
        }
    }

    private struct Slot
    {
        public int Index;
        public int Revision;

        public Slot(int index, int revision)
        {
            Index = index;
            Revision = revision;
        }
    }

    private readonly struct Candidate
    {
        public readonly WearTile Tile;
        public readonly float Distance;
        public readonly float Fade;

        public Candidate(WearTile tile, float distance, float fade)
        {
            Tile = tile;
            Distance = distance;
            Fade = fade;
        }

        public static readonly Comparison<Candidate> ByDistance = (a, b) =>
            a.Distance.CompareTo(b.Distance);
        public static readonly Comparison<Candidate> ByHeight = (a, b) =>
            a.Tile.Layer.CompareTo(b.Tile.Layer);
    }
}
