using System;
using System.Collections.Generic;
using RoadSnow.Core;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace RoadSnow.Mod;

internal sealed class TrafficMarks : IDisposable
{
    // This atlas is a GPU cache of WearHistory, not the history itself. The page table
    // maps world tiles to reusable slots, including separate layers for stacked roads.
    private const int AtlasSize = 2048;
    private const int SlotsPerSide = AtlasSize / WearHistory.Size;
    private readonly WearHistory _history;
    private const int PageSide = 16;
    private const int PageLayers = 4;
    private readonly Texture2D _pages;
    private readonly Color32[] _pagePixels = new Color32[PageSide * PageSide * PageLayers];
    private readonly int[] _pageLayers = new int[PageSide * PageSide];
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
    private readonly Color32[] _upload = new Color32[WearHistory.Size * WearHistory.Size];
    private int _weatherCursor;
    private int _drawRevision = -1;
    private int _drawSnapshot = -1;
    private int _drawBudget;
    private Matrix4x4 _drawView;
    private bool _drawSnowing;
    public int Drawn { get; private set; }
    public int Stored => _history.Tiles.Count;

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

    public TrafficMarks(ComputeShader probe, WearHistory history)
    {
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
        Shader.SetGlobalVector("_RoadSnowTrackArea", _area);
        Shader.SetGlobalTexture("_RoadSnowTrackAtlas", _texture);
        Shader.SetGlobalTexture("_RoadSnowTrackPages", _pages);
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
        SetWeather(depth, snapshotVersion, snowing, snowfallTotal);
        Matrix4x4 view = camera.projectionMatrix * camera.worldToCameraMatrix;
        if (
            _drawRevision == _history.Revision
            && _drawSnapshot == snapshotVersion
            && _drawBudget == budget
            && _drawView == view
            && _drawSnowing == snowing
            && _drawSnowfallTotal == snowfallTotal
        )
        {
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
        Vector3 eye = camera.transform.position;
        float projection = camera.orthographic
            ? 0
            : 2
                * Mathf.Tan(camera.fieldOfView * Mathf.Deg2Rad * 0.5f)
                / Math.Max(1, camera.pixelHeight);
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
            if (distanceSquared > 500 * 500)
            {
                continue;
            }

            float mpp = camera.orthographic
                ? camera.orthographicSize * 2 / Math.Max(1, camera.pixelHeight)
                : Mathf.Sqrt(distanceSquared) * projection;
            if (mpp >= 0.32f)
            {
                continue;
            }

            Vector3 screen = camera.WorldToViewportPoint(center);
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

            float focusDistance = WearView.ImpressionPriority(camera, screen, distanceSquared);
            _candidates.Add(
                new Candidate(tile, focusDistance, Mathf.Clamp01((0.32f - mpp) / 0.12f))
            );
        }

        _candidates.Sort(Candidate.ByDistance);
        ViewCandidates = _candidates.Count;
        Vector3 focus = _candidates.Count > 0 ? Center(_candidates[0].Tile) : Vector3.zero;
        const float rasterMeters = AtlasSize * WearHistory.Cell;
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
        int limit = Mathf.Clamp(budget / 8, 64, SlotsPerSide * SlotsPerSide);
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
        Array.Clear(_pagePixels, 0, _pagePixels.Length);
        Array.Clear(_pageLayers, 0, _pageLayers.Length);
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

            _pageLayers[page]++;
            if (!_slots.TryGetValue(tile, out var slot))
            {
                int last = _free.Count - 1;
                slot = new Slot(_free[last], -1);
                _free.RemoveAt(last);
            }

            int x = (slot.Index % SlotsPerSide) * WearHistory.Size;
            int y = (slot.Index / SlotsPerSide) * WearHistory.Size;
            if (slot.Revision != tile.Revision)
            {
                var pixels = tile.Pixels;
                for (int i = 0; i < _upload.Length; i++)
                {
                    _upload[i] = new Color32(
                        pixels[i * 4],
                        pixels[i * 4 + 1],
                        pixels[i * 4 + 2],
                        pixels[i * 4 + 3]
                    );
                }

                _texture.SetPixels32(x, y, WearHistory.Size, WearHistory.Size, _upload);
                slot.Revision = tile.Revision;
                uploaded = true;
            }

            _slots[tile] = slot;
            if (ProbeExpected == 0)
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

            _pagePixels[page + layer * PageSide * PageSide] = new Color32(
                (byte)(slot.Index % SlotsPerSide),
                (byte)(slot.Index / SlotsPerSide),
                (byte)Mathf.RoundToInt(candidate.Fade * 255),
                255
            );
            Drawn++;
        }

        if (uploaded)
        {
            _texture.Apply(false, false);
        }

        _pages.SetPixels32(_pagePixels);
        _pages.Apply(false, false);
        Bind();
        _history.CompressBackground(_selected);
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
                        : $"individual impression GPU={request.GetData<Vector4>()[0].x:F3}, CPU={Mathf.Min(.95f, expected):F3}, world={point}, pages={Drawn}"
                );
            }
        );
    }

    public void Dispose()
    {
        _disposed = true;
        Shader.SetGlobalVector("_RoadSnowTrackArea", Vector4.zero);
        Shader.SetGlobalTexture("_RoadSnowTrackAtlas", Texture2D.blackTexture);
        Shader.SetGlobalTexture("_RoadSnowTrackPages", Texture2D.blackTexture);
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
