using System;
using System.Collections.Generic;
using Colossal.Mathematics;
using Game.Common;
using Game.Net;
using Game.Pathfind;
using Game.Prefabs;
using RoadSnow.Core;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using CarLane = Game.Net.CarLane;
using SubLane = Game.Net.SubLane;
using TrackLane = Game.Net.TrackLane;

namespace RoadSnow.Mod;

internal sealed class LaneWearMask : IDisposable
{
    // follow carLane curves. a fixed pattern across the road width drifts onto
    // lane dividers and breaks at intersections, roundabouts and wider roads.
    private readonly Dictionary<Entity, LaneState> _states = new();
    private readonly List<LaneState> _lanes = new();
    private readonly Dictionary<PathNode, List<LaneState>> _ends = new();
    private readonly Dictionary<Vector2Int, List<LaneState>> _cells = new();
    private readonly HashSet<LaneState> _drawn = new();
    private readonly AreaCache _nearCache = new();
    private readonly AreaCache _farCache = new();
    private readonly AreaCache _detailCache = new();
    private int _topologyRevision;
    private readonly LaneRaster _raster;
    private readonly LaneRaster _distantRaster;
    private readonly LaneRaster _detailRaster;
    private float _nextDistantDraw;
    private float _nearSize;
    private float _farSize;
    private uint _lastUpdateFrame;
    private bool _hasUpdated;
    private int _cursor;
    private readonly Plane[] _frustum = new Plane[6];
    public int Drawn => _detailCache.Visible.Count;
    public int PreparedAreas { get; private set; }
    public int ReusedAreas { get; private set; }
    public long RetainedQuads { get; private set; }
    public long PreparedQuads { get; private set; }
    public int Registered => _lanes.Count;
    public int RestoredOnly => _restored.Count;
    public int Rasterizations =>
        _raster.Rasterizations + _distantRaster.Rasterizations + _detailRaster.Rasterizations;
    public int ReusedRasterizations =>
        _raster.ReusedRasterizations
        + _distantRaster.ReusedRasterizations
        + _detailRaster.ReusedRasterizations;
    public float PeakPressure { get; private set; }

    private readonly Dictionary<Bezier4x3, LaneSnow> _history;
    private readonly Dictionary<Bezier4x3, (float Width, bool Rail)> _layout;
    private readonly Dictionary<Bezier4x3, LaneState> _restored = new();

    public LaneWearMask(
        ComputeShader shader,
        Dictionary<Bezier4x3, LaneSnow> history,
        Dictionary<Bezier4x3, (float Width, bool Rail)> layout
    )
    {
        _raster = new LaneRaster(shader);
        _distantRaster = new LaneRaster(shader, true);
        _detailRaster = new LaneRaster(shader, detail: true);
        _history = history;
        _layout = layout;
        foreach (var saved in history)
        {
            var shape = layout.TryGetValue(saved.Key, out var value) ? value : (3f, false);
            var state = new LaneState
            {
                Curve = saved.Key,
                Snow = saved.Value,
                Width = shape.Item1,
                Rail = shape.Item2,
                HistoryOnly = true,
                Length = Mathf.Max(1, MathUtils.Length(saved.Key)),
                Center = MathUtils.Position(saved.Key, .5f),
            };
            _lanes.Add(state);
            _restored[saved.Key] = state;
            AddCells(state);
        }
    }

    public void Register(EntityManager manager, Entity owner, uint frame)
    {
        if (!manager.HasBuffer<SubLane>(owner))
        {
            return;
        }

        foreach (var sub in manager.GetBuffer<SubLane>(owner, true))
        {
            Entity lane = sub.m_SubLane;
            if (
                (!manager.HasComponent<CarLane>(lane) && !manager.HasComponent<TrackLane>(lane))
                || !manager.HasComponent<Lane>(lane)
                || manager.HasComponent<Game.Tools.Temp>(lane)
                || !manager.HasComponent<Curve>(lane)
                || (!manager.HasComponent<LaneFlow>(lane) && !manager.HasComponent<TrackLane>(lane))
                || manager.HasComponent<MasterLane>(lane)
                || manager.HasComponent<Deleted>(lane)
            )
            {
                continue;
            }

            float width = 3;
            if (manager.HasComponent<PrefabRef>(lane))
            {
                Entity prefab = manager.GetComponentData<PrefabRef>(lane).m_Prefab;
                if (manager.HasComponent<NetLaneData>(prefab))
                {
                    width = manager.GetComponentData<NetLaneData>(prefab).m_Width;
                }
            }

            bool rail = manager.HasComponent<TrackLane>(lane);
            if (rail)
                width = Mathf.Max(1.8f, width);
            if (width < 1.8f || width > (rail ? 12 : 6))
            {
                continue;
            }

            var curve = manager.GetComponentData<Curve>(lane);
            var topology = manager.GetComponentData<Lane>(lane);
            if (_restored.TryGetValue(curve.m_Bezier, out var restored))
            {
                Remove(restored);
                _restored.Remove(curve.m_Bezier);
            }
            if (_states.TryGetValue(lane, out var existing))
            {
                if (
                    existing.Curve.Equals(curve.m_Bezier)
                    && existing.Start.Equals(topology.m_StartNode)
                    && existing.End.Equals(topology.m_EndNode)
                    && existing.Width == width
                    && existing.Length == Mathf.Max(1, curve.m_Length)
                    && existing.Rail == rail
                )
                    continue;
                Remove(existing);
                if (!existing.Curve.Equals(curve.m_Bezier))
                {
                    _history.Remove(existing.Curve);
                    _layout.Remove(existing.Curve);
                }
            }
            if (!_history.TryGetValue(curve.m_Bezier, out var snow))
                _history.Add(curve.m_Bezier, snow = new LaneSnow());
            _layout[curve.m_Bezier] = (width, manager.HasComponent<TrackLane>(lane));
            var state = new LaneState
            {
                Entity = lane,
                Start = topology.m_StartNode,
                End = topology.m_EndNode,
                Snow = snow,
                Curve = curve.m_Bezier,
                Width = width,
                Rail = manager.HasComponent<TrackLane>(lane),
                Frame = frame,
                Length = Mathf.Max(1, curve.m_Length),
                Center = MathUtils.Position(curve.m_Bezier, .5f),
            };
            _states.Add(lane, state);
            _lanes.Add(state);
            AddEndpoint(state, state.Start);
            AddEndpoint(state, state.End);
            AddCells(state);
        }
    }

    private void AddCells(LaneState state)
    {
        _topologyRevision++;
        float3 min = math.min(
            math.min(state.Curve.a, state.Curve.b),
            math.min(state.Curve.c, state.Curve.d)
        );
        float3 max = math.max(
            math.max(state.Curve.a, state.Curve.b),
            math.max(state.Curve.c, state.Curve.d)
        );
        for (int z = Mathf.FloorToInt(min.z / 128); z <= Mathf.FloorToInt(max.z / 128); z++)
        {
            for (int x = Mathf.FloorToInt(min.x / 128); x <= Mathf.FloorToInt(max.x / 128); x++)
            {
                var key = new Vector2Int(x, z);
                if (!_cells.TryGetValue(key, out var list))
                {
                    _cells.Add(key, list = new List<LaneState>());
                }

                list.Add(state);
            }
        }
    }

    private void AddEndpoint(LaneState lane, PathNode key)
    {
        if (!_ends.TryGetValue(key, out var list))
        {
            _ends.Add(key, list = new List<LaneState>());
        }

        list.Add(lane);
    }

    private void Remove(LaneState lane)
    {
        _topologyRevision++;
        lane.Deleted = true;
        _states.Remove(lane.Entity);
        _lanes.Remove(lane);
        if (_ends.TryGetValue(lane.Start, out var start))
            start.Remove(lane);
        if (_ends.TryGetValue(lane.End, out var end))
            end.Remove(lane);
        float3 min = math.min(
            math.min(lane.Curve.a, lane.Curve.b),
            math.min(lane.Curve.c, lane.Curve.d)
        );
        float3 max = math.max(
            math.max(lane.Curve.a, lane.Curve.b),
            math.max(lane.Curve.c, lane.Curve.d)
        );
        for (int z = Mathf.FloorToInt(min.z / 128); z <= Mathf.FloorToInt(max.z / 128); z++)
        for (int x = Mathf.FloorToInt(min.x / 128); x <= Mathf.FloorToInt(max.x / 128); x++)
            if (_cells.TryGetValue(new Vector2Int(x, z), out var cell))
                cell.Remove(lane);
        _cursor = Math.Min(_cursor, _lanes.Count);
    }

    private float Endpoint(LaneState lane, PathNode point)
    {
        if (lane.HistoryOnly)
            return lane.Snow.Pressure;
        float sum = 0;
        int count = 0;
        if (_ends.TryGetValue(point, out var list))
        {
            foreach (var neighbour in list)
            {
                if (!neighbour.Deleted)
                {
                    sum += neighbour.Snow.Pressure;
                    count++;
                }
            }
        }

        return count > 0 ? sum / count : lane.Snow.Pressure;
    }

    public void Update(
        EntityManager manager,
        uint frame,
        float hoursPerFrame,
        bool snowing,
        float influence,
        Func<Vector3, float> depth,
        double snowfallTotal = -1
    )
    {
        if (_hasUpdated && frame == _lastUpdateFrame)
            return;
        _hasUpdated = true;
        _lastUpdateFrame = frame;
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        for (int i = 0; i < Math.Min(512, _lanes.Count); i++)
        {
            if (
                (System.Diagnostics.Stopwatch.GetTimestamp() - started)
                    * 1000.0
                    / System.Diagnostics.Stopwatch.Frequency
                > .4
            )
            {
                break;
            }

            if (_cursor >= _lanes.Count)
            {
                _cursor = 0;
            }

            var lane = _lanes[_cursor++];
            if (lane.HistoryOnly)
                continue;
            if (!manager.Exists(lane.Entity) || manager.HasComponent<Deleted>(lane.Entity))
            {
                Remove(lane);
                _history.Remove(lane.Curve);
                _layout.Remove(lane.Curve);
                _cursor = Math.Max(0, _cursor - 1);
                continue;
            }

            uint elapsed = unchecked(frame - lane.Frame);
            lane.Frame = frame;
            float hours = elapsed * hoursPerFrame;
            if (hours > 24)
            {
                hours = 0;
            }

            var flow = manager.HasComponent<LaneFlow>(lane.Entity)
                ? manager.GetComponentData<LaneFlow>(lane.Entity)
                : default;
            lane.Snow.Advance(
                depth(lane.Center),
                math.cmax(flow.m_Distance),
                hours,
                snowing,
                influence,
                snowfallTotal
            );
        }
    }

    public void Draw(Camera camera, float influence, Vector3? cameraPivot = null, int phase = -1)
    {
        Ray ray = camera.ViewportPointToRay(new Vector3(.5f, .5f, 0));
        float distance = Mathf.Abs(ray.direction.y) > .1f ? -ray.origin.y / ray.direction.y : 500;
        Vector3 focus = cameraPivot ?? ray.GetPoint(Mathf.Clamp(distance, 0, 3000));
        if ((phase < 0 || phase == 2) && Time.realtimeSinceStartup >= _nextDistantDraw)
        {
            var farArea = WearView.DistantLaneArea(camera, focus, _farSize);
            _farSize = 1 / farArea.z;
            DrawArea(camera, influence, farArea, _distantRaster, _farCache, true);
            _nextDistantDraw = Time.realtimeSinceStartup + .5f;
        }
        if (phase < 0 || phase == 1)
        {
            var nearArea = WearView.LaneArea(camera, focus, _nearSize);
            _nearSize = 1 / nearArea.z;
            DrawArea(camera, influence, nearArea, _raster, _nearCache, false);
        }
        if (phase < 0 || phase == 0)
            DrawArea(
                camera,
                influence,
                WearView.DetailLaneArea(camera, focus),
                _detailRaster,
                _detailCache,
                false
            );
    }

    private void DrawArea(
        Camera camera,
        float influence,
        Vector4 area,
        LaneRaster raster,
        AreaCache cache,
        bool distant
    )
    {
        float size = 1 / area.z;
        PeakPressure = 0;
        Matrix4x4 culling = camera.cullingMatrix;
        Matrix4x4 view = camera.projectionMatrix * camera.worldToCameraMatrix;
        Vector3 eye = camera.transform.position;
        bool viewChanged =
            !cache.Ready
            || cache.Topology != _topologyRevision
            || !cache.Area.Equals(area)
            || !cache.Culling.Equals(culling)
            || !cache.View.Equals(view)
            || !cache.Eye.Equals(eye);
        if (viewChanged)
        {
            cache.Candidates.Clear();
            _drawn.Clear();
            GeometryUtility.CalculateFrustumPlanes(camera, _frustum);
            for (
                int z = Mathf.FloorToInt(area.y / 128);
                z <= Mathf.FloorToInt((area.y + size) / 128);
                z++
            )
            {
                for (
                    int x = Mathf.FloorToInt(area.x / 128);
                    x <= Mathf.FloorToInt((area.x + size) / 128);
                    x++
                )
                {
                    if (_cells.TryGetValue(new Vector2Int(x, z), out var list))
                    {
                        foreach (var lane in list)
                        {
                            if (
                                !lane.Deleted
                                && _drawn.Add(lane)
                                && GeometryUtility.TestPlanesAABB(
                                    _frustum,
                                    new Bounds(
                                        lane.Center,
                                        Vector3.one * (lane.Length + lane.Width)
                                    )
                                )
                            )
                            {
                                cache.Candidates.Add(lane);
                            }
                        }
                    }
                }
            }

            cache.Area = area;
            cache.Culling = culling;
            cache.View = view;
            cache.Eye = eye;
            cache.Topology = _topologyRevision;
        }

        if (PrepareRibbons(influence, eye, viewChanged, raster, cache, distant))
        {
            raster.Draw(area);
            cache.Ready = true;
        }
        else
            raster.Reuse(area);
    }

    // CPU preparation is separate from GPU submission so exact output can be
    // checked against a full ribbon rebuild without needing a graphics device.
    private bool PrepareRibbons(
        float influence,
        Vector3 eye,
        bool viewChanged,
        LaneRaster raster,
        AreaCache cache,
        bool distant
    )
    {
        PeakPressure = 0;
        // Keep inactive lanes in the spatial cache so the first car reactivates
        // them without requiring a camera move or a topology refresh.
        cache.Active.Begin();
        foreach (var lane in cache.Candidates)
            if (influence > 0 && (lane.Snow.Pressure > .001f || lane.Rail))
                cache.Active.Add(new LaneInput(lane, 0, 0, 0, 0));
        bool selectionChanged = cache.Active.End();
        if (viewChanged || selectionChanged)
        {
            cache.Visible.Clear();
            foreach (var input in cache.Active.Items)
                cache.Visible.Add(input.Lane);
            if (!distant)
                cache.Visible.Sort(
                    (a, b) => (a.Center - eye).sqrMagnitude.CompareTo((b.Center - eye).sqrMagnitude)
                );
        }

        cache.Inputs.Begin();
        cache.Retain.Clear();
        int quads = 0;
        int previousQuads = 0;
        foreach (var lane in cache.Visible)
        {
            if (quads >= LaneRaster.MaximumQuads)
            {
                break;
            }

            PeakPressure = Mathf.Max(PeakPressure, lane.Snow.Pressure);
            float segmentLength =
                distant ? 8
                : raster == _detailRaster ? 2
                : 4;
            // Spend the far budget across the whole view. Stopping after the
            // nearest ribbons left a sharp wall of pristine snow in dense cities.
            int maximumSteps = distant
                ? Math.Max(1, LaneRaster.MaximumQuads / Math.Max(1, cache.Visible.Count))
                : 96;
            int steps = Mathf.Clamp(
                Mathf.CeilToInt(lane.Length / segmentLength),
                distant ? 1 : 4,
                Math.Min(96, maximumSteps)
            );
            float start = Endpoint(lane, lane.Start);
            float end = Endpoint(lane, lane.End);
            var input = new LaneInput(lane, steps, lane.Snow.Pressure, start, end);
            int index = cache.Inputs.Count;
            bool hasPrevious = index < cache.Inputs.Items.Count;
            LaneInput previous = hasPrevious ? cache.Inputs.Items[index] : default;
            cache.Retain.Add(
                cache.Ready && hasPrevious && quads == previousQuads && input.Equals(previous)
            );
            previousQuads += previous.Steps;
            cache.Inputs.Add(input);
            quads += steps;
        }
        bool inputsChanged = cache.Inputs.End();
        if (cache.Ready && !inputsChanged)
        {
            ReusedAreas++;
            RetainedQuads += Math.Min(quads, LaneRaster.MaximumQuads);
            return false;
        }
        cache.Ready = false;
        raster.Begin();
        for (int row = 0; row < cache.Inputs.Items.Count; row++)
        {
            var input = cache.Inputs.Items[row];
            var lane = input.Lane;
            int steps = input.Steps;
            if (cache.Retain[row])
            {
                RetainedQuads += Math.Min(steps, LaneRaster.MaximumQuads - raster.Count);
                raster.Retain(steps);
                continue;
            }
            PreparedQuads += Math.Min(steps, LaneRaster.MaximumQuads - raster.Count);
            var geometry = lane.GetGeometry(steps);
            Vector3 previousLeft = default;
            Vector3 previousRight = default;
            float previousPressure = 0;
            for (int i = 0; i <= steps; i++)
            {
                float pressure = Mathf.Lerp(input.Start, input.Pressure, geometry.StartBlend[i]);
                pressure = Mathf.Lerp(input.End, pressure, geometry.EndBlend[i]);
                Vector3 left = geometry.Left[i];
                Vector3 right = geometry.Right[i];
                if (i > 0)
                {
                    raster.Add(
                        previousLeft,
                        previousRight,
                        left,
                        right,
                        previousPressure,
                        pressure,
                        lane.Width,
                        lane.Rail
                    );
                }

                previousLeft = left;
                previousRight = right;
                previousPressure = pressure;
            }
        }

        PreparedAreas++;
        return true;
    }

    public void Dispose()
    {
        _raster.Dispose();
        _distantRaster.Dispose();
        _detailRaster.Dispose();
    }

    public void ProbeGpu(Action<string> report) => _raster.ProbeGpu(report);

    private sealed class AreaCache
    {
        public bool Ready;
        public int Topology;
        public Vector4 Area;
        public Matrix4x4 Culling;
        public Matrix4x4 View;
        public Vector3 Eye;
        public readonly List<LaneState> Candidates = new();
        public readonly List<LaneState> Visible = new();
        public readonly ExactSequence<LaneInput> Active = new();
        public readonly ExactSequence<LaneInput> Inputs = new();
        public readonly List<bool> Retain = new();
    }

    private readonly struct LaneInput : IEquatable<LaneInput>
    {
        public readonly LaneState Lane;
        public readonly int Steps;
        public readonly float Pressure,
            Start,
            End;

        public LaneInput(LaneState lane, int steps, float pressure, float start, float end)
        {
            Lane = lane;
            Steps = steps;
            Pressure = pressure;
            Start = start;
            End = end;
        }

        public bool Equals(LaneInput other) =>
            ReferenceEquals(Lane, other.Lane)
            && Steps == other.Steps
            && Pressure.Equals(other.Pressure)
            && Start.Equals(other.Start)
            && End.Equals(other.End);
    }

    private sealed class RibbonGeometry
    {
        public readonly Vector3[] Left,
            Right;
        public readonly float[] StartBlend,
            EndBlend;

        public RibbonGeometry(int steps)
        {
            Left = new Vector3[steps + 1];
            Right = new Vector3[steps + 1];
            StartBlend = new float[steps + 1];
            EndBlend = new float[steps + 1];
        }
    }

    private sealed class LaneState
    {
        // Bound per-lane storage even when the distant budget changes step counts.
        private readonly LruCache<int, RibbonGeometry> _geometry = new(4);

        public RibbonGeometry GetGeometry(int steps)
        {
            if (_geometry.TryGetValue(steps, out var cached))
                return cached;
            var geometry = new RibbonGeometry(steps);
            for (int i = 0; i <= steps; i++)
            {
                float t = i / (float)steps;
                float3 p = MathUtils.Position(Curve, t);
                float3 tangent = MathUtils.Tangent(Curve, t);
                float3 side =
                    math.normalizesafe(new float3(tangent.z, 0, -tangent.x)) * Width * .5f;
                geometry.Left[i] = p - side;
                geometry.Right[i] = p + side;
                geometry.StartBlend[i] = Mathf.SmoothStep(0, 1, t * Length / 5);
                geometry.EndBlend[i] = Mathf.SmoothStep(0, 1, (1 - t) * Length / 5);
            }
            _geometry.Set(steps, geometry);
            return geometry;
        }

        public Entity Entity;
        public Bezier4x3 Curve;
        public float Width;
        public float Length;
        public uint Frame;
        public Vector3 Center;
        public bool Deleted;
        public LaneSnow Snow;
        public PathNode Start,
            End;
        public bool Rail;
        public bool HistoryOnly;
    }
}
