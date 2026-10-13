using System;
using System.Collections.Generic;
using System.Diagnostics;
using RoadSnow.Core;
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
    private readonly TileTextureAtlas _atlas;
    private Texture2D _texture => _atlas.Texture;
    private readonly List<Candidate> _candidates = new();
    private readonly List<Candidate> _probeCandidates = new();
    private readonly List<Candidate> _geometryCandidates = new();
    private readonly List<WearTile> _nearbyTiles = new();
    private readonly ExactSequence<Candidate> _activeCandidates = new();
    private WearView.Snapshot _selectionView;
    private int _viewTopology = -1;
    private int _viewHistoryCount;
    private bool _viewReady;
    private bool _selectionReady;
    private int _selectionBudget;
    private int _originX,
        _originZ;
    public int SelectionBuilds { get; private set; }
    public int SelectionReuses { get; private set; }
    public int UploadedTiles { get; private set; }
    public int WeatherOnlyReuses { get; private set; }
    public long UploadedBytes => _atlas.UploadedBytes;
    public bool PartialUploads => _atlas.PartialUploads;
    private readonly HashSet<WearTile> _selected = new();
    private readonly Dictionary<WearTile, Slot> _slots = new();
    private readonly List<WearTile> _evict = new();
    private readonly List<int> _free = new();
    private readonly WearOverviewCache _overviews = new();
    private readonly WeatherOverviewCache _weatherOverviews = new();
    public long WeatherOverviewReuses => _weatherOverviews.WeatherReuses;
    public long HistoryReadTicks { get; private set; }
    public long ReductionTicks { get; private set; }
    public long TextureCopyTicks { get; private set; }
    public long AtlasApplyTicks { get; private set; }
    public long PageUploadTicks { get; private set; }
    public int OverviewReuses { get; private set; }
    public int OverviewBuilds { get; private set; }
    public long OverviewBlocksBuilt => _overviews.BlocksBuilt;
    public long OverviewBlocksRetained => _overviews.BlocksRetained;

    public void ResetTimings()
    {
        HistoryReadTicks =
            ReductionTicks =
            TextureCopyTicks =
            AtlasApplyTicks =
            PageUploadTicks =
                0;
    }

    private int _weatherCursor;
    private int _uploadCursor;
    private readonly HashSet<WearTile> _uploadSelection = new();
    private readonly List<WearTile> _urgentPlowUploads = new();
    public int PendingUploadTiles { get; private set; }
    private int _drawRevision = -1;
    private int _drawSnapshot = -1;
    private int _drawBudget;
    private WearView.Snapshot _drawView;
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
    public int LiveTiles
    {
        get
        {
            int count = 0;
            foreach (var tile in _history.Tiles)
                if (!tile.Empty)
                    count++;
            return count;
        }
    }
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
        _history = history;
        _tileDepth = tile => _depth(new Vector3(tile.WeatherX, tile.CenterY, tile.WeatherZ));
        _atlas = new TileTextureAtlas(AtlasSize, TileResolution);
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
            _snowfallTotal,
            roundCaps: plow
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

    public PlowFeather PlowFeather { get; set; }

    public void ConfigurePlowFeather(PlowFeather feather) => PlowFeather = feather;

    public int ImmediatePlowUploads { get; private set; }

    // Existing visible pages must not wait behind the distant background queue.
    // Reuse the exact same image preparation as normal drawing, then upload both
    // atlases before this frame renders. New/offscreen pages keep normal discovery.
    public void RefreshPlowTiles(List<WearTile> changed)
    {
        if (changed.Count == 0)
            return;
        _drawRevision = -1;
        bool uploaded = false,
            pagesChanged = false;
        foreach (var tile in changed)
        {
            if (tile.Empty)
                continue;
            bool resident = _slots.TryGetValue(tile, out var slot);
            int newPage = -1;
            if (!resident && !AllocatePlowPage(tile, out slot, out newPage))
                continue;
            if (resident && tile.PlowSequence <= slot.PlowSequence)
                continue;
            UploadImage(tile, ref slot);
            _slots[tile] = slot;
            uploaded = true;
            ImmediatePlowUploads++;
            if (newPage >= 0)
            {
                var entry = _pagePixels[newPage];
                entry.r = (byte)(slot.Index % SlotsPerSide);
                entry.g = (byte)(slot.Index / SlotsPerSide);
                entry.a = 255;
                _pagePixels[newPage] = entry;
                _occupiedPages.Add(newPage);
                _nextOccupiedPages.Add(newPage);
                _selected.Add(tile);
                pagesChanged = true;
            }
            if (!_distant)
            {
                int x = tile.X - _originX,
                    z = tile.Z - _originZ;
                if ((uint)x >= PageSide || (uint)z >= PageSide)
                    continue;
                for (int layer = 0; layer < PageLayers; layer++)
                {
                    int at = z * PageSide + x + layer * PageSide * PageSide;
                    var entry = _pagePixels[at];
                    if (entry.a == 0)
                        break;
                    if (
                        entry.r == slot.Index % SlotsPerSide
                        && entry.g == slot.Index / SlotsPerSide
                        && entry.a != 255
                    )
                    {
                        entry.a = 255;
                        _pagePixels[at] = entry;
                        pagesChanged = true;
                    }
                }
            }
        }
        if (uploaded)
        {
            long started = Stopwatch.GetTimestamp();
            _atlas.Flush();
            AtlasApplyTicks += Stopwatch.GetTimestamp() - started;
        }
        if (pagesChanged)
        {
            long started = Stopwatch.GetTimestamp();
            _pages.SetPixels32(_pagePixels);
            _pages.Apply(false, false);
            PageUploadTicks += Stopwatch.GetTimestamp() - started;
        }
    }

    private bool AllocatePlowPage(WearTile tile, out Slot slot, out int pageIndex)
    {
        slot = default;
        pageIndex = -1;
        int x = tile.X - _originX,
            z = tile.Z - _originZ;
        if (!_viewReady || _free.Count == 0 || (uint)x >= PageSide || (uint)z >= PageSide)
            return false;
        var center = Center(tile);
        var screen = _selectionView.Viewport(center);
        if (
            screen.z <= 0
            || screen.x < -.15f
            || screen.x > 1.15f
            || screen.y < -.15f
            || screen.y > 1.15f
        )
            return false;
        float mpp = _selectionView.PixelSize((center - _selectionView.Eye).sqrMagnitude);
        float fade = _distant ? 1 : 1 - Mathf.SmoothStep(0, 1, Mathf.InverseLerp(.25f, .65f, mpp));
        int page = z * PageSide + x,
            layer = _pageLayers[page];
        if (fade <= 0 || layer >= PageLayers)
            return false;
        int last = _free.Count - 1;
        slot = new Slot(_free[last], -1);
        _free.RemoveAt(last);
        pageIndex = page + layer * PageSide * PageSide;
        _pageLayers[page]++;
        _pagePixels[pageIndex] = new Color32(0, 0, (byte)Mathf.RoundToInt(fade * 255), 255);
        return true;
    }

    public void AddPlowTriangle(Vector3 a, Vector3 b, Vector3 c) =>
        _history.StampPlowTriangle(
            a.x,
            a.y,
            a.z,
            b.x,
            b.y,
            b.z,
            c.x,
            c.y,
            c.z,
            _depth((a + b + c) / 3),
            _tileDepth,
            _weatherVersion,
            _snowing,
            _snowfallTotal,
            PlowFeather
        );

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
        if (
            _drawRevision == _history.Revision
            && _drawSnapshot == snapshotVersion
            && _drawBudget == budget
            && _drawView.Equals(cameraView)
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
        _drawView = cameraView;
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
            ObserveWeather(tile);
        }

        SelectCandidates(cameraView, budget);
        int originX = _originX,
            originZ = _originZ;

        _uploadSelection.Clear();
        if (_distant)
        {
            foreach (var candidate in _candidates)
                ObserveWeather(candidate.Tile);
            SelectUrgentPlowUploads();
            int countToVisit = _candidates.Count;
            for (int visited = 0; visited < countToVisit && _uploadSelection.Count < 32; visited++)
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
        DrawSelected(originX, originZ);
    }

    private void SelectUrgentPlowUploads()
    {
        _urgentPlowUploads.Clear();
        foreach (var candidate in _candidates)
        {
            var tile = candidate.Tile;
            if (
                !tile.Empty
                && tile.PlowSequence > 0
                && (
                    !_slots.TryGetValue(tile, out var slot) || tile.PlowSequence > slot.PlowSequence
                )
            )
                _urgentPlowUploads.Add(tile);
        }
        _urgentPlowUploads.Sort((a, b) => b.PlowSequence.CompareTo(a.PlowSequence));
        for (int i = 0; i < Math.Min(8, _urgentPlowUploads.Count); i++)
            _uploadSelection.Add(_urgentPlowUploads[i]);
    }

    private void ObserveWeather(WearTile tile)
    {
        // Checking here avoids sampling native snow before ObserveSnapshot's own
        // early-out, including the second far/fine visit to the same history tile.
        if (tile.WeatherVersion != _weatherVersion || tile.ObservedSnowfall != _snowfallTotal)
            tile.ObserveSnapshot(_tileDepth(tile), _snowing, _weatherVersion, _snowfallTotal);
    }

    private void SelectCandidates(WearView.Snapshot cameraView, int budget)
    {
        bool sameView = _viewReady && _selectionView.Equals(cameraView);
        if (!sameView || _viewTopology != _history.TopologyRevision)
        {
            bool appendOnly =
                sameView
                && _history.Tiles.Count >= _viewHistoryCount
                && _history.TopologyRevision - _viewTopology
                    == _history.Tiles.Count - _viewHistoryCount;
            if (appendOnly)
            {
                for (int i = _viewHistoryCount; i < _history.Tiles.Count; i++)
                    AddGeometryCandidate(_history.Tiles[i], cameraView);
            }
            else
            {
                _geometryCandidates.Clear();
                float radius = _distant ? 5000 : 1600;
                _history.QueryTiles(
                    Mathf.FloorToInt((cameraView.Eye.x - radius) / WearHistory.TileMeters),
                    Mathf.FloorToInt((cameraView.Eye.z - radius) / WearHistory.TileMeters),
                    Mathf.FloorToInt((cameraView.Eye.x + radius) / WearHistory.TileMeters),
                    Mathf.FloorToInt((cameraView.Eye.z + radius) / WearHistory.TileMeters),
                    _nearbyTiles
                );
                foreach (var tile in _nearbyTiles)
                    AddGeometryCandidate(tile, cameraView);
            }
            _selectionView = cameraView;
            _viewTopology = _history.TopologyRevision;
            _viewHistoryCount = _history.Tiles.Count;
            _viewReady = true;
        }

        _activeCandidates.Begin();
        foreach (var candidate in _geometryCandidates)
            if (!candidate.Tile.Empty)
                _activeCandidates.Add(candidate);
        bool changed = _activeCandidates.End();
        if (_selectionReady && !changed && _selectionBudget == budget)
        {
            SelectionReuses++;
            return;
        }
        _selectionReady = false;
        _candidates.Clear();
        _candidates.AddRange(_activeCandidates.Items);
        _selected.Clear();
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
            _candidates.RemoveRange(limit, _candidates.Count - limit);
        foreach (var candidate in _candidates)
            _selected.Add(candidate.Tile);
        _evict.Clear();
        foreach (var entry in _slots)
            if (!_selected.Contains(entry.Key))
                _evict.Add(entry.Key);
        foreach (var tile in _evict)
        {
            _free.Add(_slots[tile].Index);
            _slots.Remove(tile);
        }
        _candidates.Sort(Candidate.ByHeight);
        _originX = originX;
        _originZ = originZ;
        _selectionBudget = budget;
        _selectionReady = true;
        SelectionBuilds++;
    }

    private void AddGeometryCandidate(WearTile tile, WearView.Snapshot cameraView)
    {
        Vector3 eye = cameraView.Eye;
        Vector3 center = Center(tile);
        float distanceSquared = (center - eye).sqrMagnitude;
        if (distanceSquared > (_distant ? 5000 * 5000 : 1600 * 1600))
        {
            return;
        }

        float mpp = cameraView.PixelSize(distanceSquared);
        if (!_distant && mpp >= 0.65f)
        {
            return;
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
            return;
        }

        float focusDistance = cameraView.Priority(screen, distanceSquared);
        _geometryCandidates.Add(
            new Candidate(
                tile,
                focusDistance,
                _distant ? 1 : 1 - Mathf.SmoothStep(0, 1, Mathf.InverseLerp(.25f, .65f, mpp))
            )
        );
    }

    private void DrawSelected(int originX, int originZ)
    {
        // Most 4 MiB distant page-table entries never change. Track occupied
        // entries so vehicle motion does not clear and upload the entire table.
        foreach (int occupied in _occupiedPages)
            _pageLayers[occupied % (PageSide * PageSide)] = 0;
        _nextOccupiedPages.Clear();
        bool pagesChanged = false;
        Drawn = 0;
        ProbeExpected = 0;
        _probeCandidates.Clear();
        bool uploaded = false;
        PendingUploadTiles = 0;
        foreach (var candidate in _candidates)
        {
            var tile = candidate.Tile;
            ObserveWeather(tile);
            if (!_distant)
                tile.ResolveVisibleWeather();
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
            int imageRevision = _distant ? tile.Revision : tile.ContentRevision;
            if (!canUpload && (!resident || slot.Revision != imageRevision))
            {
                PendingUploadTiles++;
                _drawRevision = -1;
                // A deferred refresh must keep the previous image visible. Removing its
                // page entry made moving traffic punch temporary holes in the far atlas.
                if (!resident)
                    continue;
            }
            if (!resident)
            {
                int last = _free.Count - 1;
                slot = new Slot(_free[last], -1);
                _free.RemoveAt(last);
            }

            if (slot.Revision != imageRevision && canUpload)
            {
                if (_distant)
                    tile.ResolveVisibleWeather();
                UploadImage(tile, ref slot);
                uploaded = true;
            }
            else if (!_distant && slot.Burial != tile.BurialTotal)
                WeatherOnlyReuses++;

            _slots[tile] = slot;
            // Alpha zero means no page. 1..255 encode exact 254..0 levels of
            // burial; 255 levels erase every possible 8-bit source pixel.
            int burial = _distant ? 0 : tile.BurialSince(slot.Burial);
            if (burial == 255)
                continue;
            _pageLayers[page]++;
            if (!_distant)
                _probeCandidates.Add(candidate);

            int pageIndex = page + layer * PageSide * PageSide;
            var entry = new Color32(
                (byte)(slot.Index % SlotsPerSide),
                (byte)(slot.Index / SlotsPerSide),
                (byte)Mathf.RoundToInt(candidate.Fade * 255),
                (byte)(255 - burial)
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
            long copyBefore = _atlas.CopyTicks,
                transferBefore = _atlas.TransferTicks;
            _atlas.Flush();
            TextureCopyTicks += _atlas.CopyTicks - copyBefore;
            AtlasApplyTicks += _atlas.TransferTicks - transferBefore;
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
            long partStarted = Stopwatch.GetTimestamp();
            _pages.SetPixels32(_pagePixels);
            _pages.Apply(false, false);
            PageUploadTicks += Stopwatch.GetTimestamp() - partStarted;
        }
        Bind();
        if (!_distant)
            _history.CompressBackground(_selected);
    }

    private static Vector3 Center(WearTile tile) => new(tile.CenterX, tile.CenterY, tile.CenterZ);

    private void PrepareProbe()
    {
        ProbeExpected = 0;
        foreach (var candidate in _probeCandidates)
        {
            var tile = candidate.Tile;
            if (
                tile.Empty
                || candidate.Fade == 0
                || !_slots.TryGetValue(tile, out var slot)
                || slot.Revision != tile.ContentRevision
            )
                continue;
            var pixels = tile.Pixels;
            for (int k = 0; k < pixels.Length; k += 4)
            {
                if (pixels[k] <= 16)
                    continue;
                int cell = k / 4;
                ProbePoint = new Vector3(
                    tile.X * WearHistory.TileMeters
                        + (cell % WearHistory.Size + .5f) * WearHistory.Cell,
                    (pixels[k + 1] + pixels[k + 2] * 256) * .1f - 1024,
                    tile.Z * WearHistory.TileMeters
                        + (cell / WearHistory.Size + .5f) * WearHistory.Cell
                );
                ProbeExpected = pixels[k] / 255f * candidate.Fade;
                return;
            }
        }
    }

    public void ProbeGpu(Action<string> report)
    {
        if (_probePending || _probe == null || !SystemInfo.supportsAsyncGPUReadback)
        {
            return;
        }

        PrepareProbe();
        if (ProbeExpected == 0)
            return;

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
        _atlas.Dispose();
        Object.Destroy(_pages);
        _probeResult?.Dispose();
        if (_probe != null)
        {
            Object.Destroy(_probe);
        }
    }

    private void UploadImage(WearTile tile, ref Slot slot)
    {
        byte[] pixels;
        long started = Stopwatch.GetTimestamp();
        if (
            _distant
            && (
                _snowing
                    ? _weatherOverviews.TryGet(tile, out pixels)
                    : _overviews.TryGet(tile, out pixels)
            )
        )
            OverviewReuses++;
        else
        {
            pixels = tile.Pixels;
            HistoryReadTicks += Stopwatch.GetTimestamp() - started;
            if (_distant)
            {
                started = Stopwatch.GetTimestamp();
                pixels = _snowing
                    ? _weatherOverviews.Build(tile, pixels)
                    : _overviews.Build(tile, pixels);
                ReductionTicks += Stopwatch.GetTimestamp() - started;
                OverviewBuilds++;
            }
        }
        started = Stopwatch.GetTimestamp();
        _atlas.Queue(pixels, slot.Index);
        TextureCopyTicks += Stopwatch.GetTimestamp() - started;
        slot.Revision = _distant ? tile.Revision : tile.ContentRevision;
        slot.Burial = tile.BurialTotal;
        slot.PlowSequence = tile.PlowSequence;
        UploadedTiles++;
    }

    private struct Slot
    {
        public int Index;
        public int Revision;
        public long Burial;
        public long PlowSequence;

        public Slot(int index, int revision)
        {
            Index = index;
            Revision = revision;
            Burial = 0;
            PlowSequence = 0;
        }
    }

    private readonly struct Candidate : IEquatable<Candidate>
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

        public bool Equals(Candidate other) =>
            ReferenceEquals(Tile, other.Tile)
            && Distance.Equals(other.Distance)
            && Fade.Equals(other.Fade);

        public static readonly Comparison<Candidate> ByDistance = (a, b) =>
            a.Distance.CompareTo(b.Distance);
        public static readonly Comparison<Candidate> ByHeight = (a, b) =>
            a.Tile.Layer.CompareTo(b.Tile.Layer);
    }
}
