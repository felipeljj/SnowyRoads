using System;
using System.Collections.Generic;
using Colossal.Mathematics;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using RoadSnow.Core;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using CarLane = Game.Net.CarLane;
using SubLane = Game.Net.SubLane;

namespace RoadSnow.Mod;

internal sealed class LaneWearMask : IDisposable
{
    // follow carLane curves. a fixed pattern across the road width drifts onto
    // lane dividers and breaks at intersections, roundabouts and wider roads.
    private readonly Dictionary<Entity, LaneState> _states = new();
    private readonly List<LaneState> _lanes = new();
    private readonly Dictionary<Vector3Int, List<LaneState>> _ends = new();
    private readonly Dictionary<Vector2Int, List<LaneState>> _cells = new();
    private readonly HashSet<Entity> _drawn = new();
    private readonly List<LaneState> _visible = new();
    private readonly LaneRaster _raster;
    private int _cursor;
    private readonly Plane[] _frustum = new Plane[6];
    public int Drawn => _visible.Count;
    public int Registered => _lanes.Count;
    public float PeakPressure { get; private set; }

    public LaneWearMask(ComputeShader shader) => _raster = new LaneRaster(shader);

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
                _states.ContainsKey(lane)
                || !manager.HasComponent<CarLane>(lane)
                || !manager.HasComponent<Curve>(lane)
                || !manager.HasComponent<LaneFlow>(lane)
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

            if (width < 1.8f || width > 6)
            {
                continue;
            }

            var curve = manager.GetComponentData<Curve>(lane);
            var state = new LaneState
            {
                Entity = lane,
                Curve = curve.m_Bezier,
                Width = width,
                Frame = frame,
                Length = Mathf.Max(1, curve.m_Length),
                Center = MathUtils.Position(curve.m_Bezier, .5f),
            };
            _states.Add(lane, state);
            _lanes.Add(state);
            AddEndpoint(state, curve.m_Bezier.a);
            AddEndpoint(state, curve.m_Bezier.d);
            float3 min = math.min(
                math.min(curve.m_Bezier.a, curve.m_Bezier.b),
                math.min(curve.m_Bezier.c, curve.m_Bezier.d)
            );
            float3 max = math.max(
                math.max(curve.m_Bezier.a, curve.m_Bezier.b),
                math.max(curve.m_Bezier.c, curve.m_Bezier.d)
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
    }

    private static Vector3Int EndKey(float3 p) =>
        new(Mathf.RoundToInt(p.x * 2), Mathf.RoundToInt(p.y * 2), Mathf.RoundToInt(p.z * 2));

    private void AddEndpoint(LaneState lane, float3 point)
    {
        var key = EndKey(point);
        if (!_ends.TryGetValue(key, out var list))
        {
            _ends.Add(key, list = new List<LaneState>());
        }

        list.Add(lane);
    }

    private float Endpoint(LaneState lane, float3 point)
    {
        float sum = 0;
        int count = 0;
        if (_ends.TryGetValue(EndKey(point), out var list))
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
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        for (int i = 0; i < Math.Min(512, _lanes.Count); i++)
        {
            if (
                (System.Diagnostics.Stopwatch.GetTimestamp() - started)
                    * 1000.0
                    / System.Diagnostics.Stopwatch.Frequency
                > 1
            )
            {
                break;
            }

            if (_cursor >= _lanes.Count)
            {
                _cursor = 0;
            }

            var lane = _lanes[_cursor++];
            if (
                !manager.HasComponent<LaneFlow>(lane.Entity)
                || manager.HasComponent<Deleted>(lane.Entity)
            )
            {
                lane.Deleted = true;
                continue;
            }

            uint elapsed = unchecked(frame - lane.Frame);
            lane.Frame = frame;
            float hours = elapsed * hoursPerFrame;
            if (hours > 24)
            {
                hours = 0;
            }

            var flow = manager.GetComponentData<LaneFlow>(lane.Entity);
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

    public void Draw(Camera camera, float influence, Vector3? cameraPivot = null)
    {
        Ray ray = camera.ViewportPointToRay(new Vector3(.5f, .5f, 0));
        float distance = Mathf.Abs(ray.direction.y) > .1f ? -ray.origin.y / ray.direction.y : 500;
        Vector3 focus = cameraPivot ?? ray.GetPoint(Mathf.Clamp(distance, 0, 3000));
        Vector4 area = WearView.LaneArea(camera, focus);
        float size = 1 / area.z;
        _visible.Clear();
        _drawn.Clear();
        PeakPressure = 0;
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
                            && influence > 0
                            && lane.Snow.Pressure > .001f
                            && _drawn.Add(lane.Entity)
                            && GeometryUtility.TestPlanesAABB(
                                _frustum,
                                new Bounds(lane.Center, Vector3.one * (lane.Length + lane.Width))
                            )
                        )
                        {
                            _visible.Add(lane);
                        }
                    }
                }
            }
        }

        Vector3 eye = camera.transform.position;
        _visible.Sort(
            (a, b) => (a.Center - eye).sqrMagnitude.CompareTo((b.Center - eye).sqrMagnitude)
        );
        _raster.Begin();
        foreach (var lane in _visible)
        {
            if (_raster.Count >= LaneRaster.MaximumQuads)
            {
                break;
            }

            PeakPressure = Mathf.Max(PeakPressure, lane.Snow.Pressure);
            float segmentLength = Mathf.Clamp(
                Vector3.Distance(camera.transform.position, lane.Center) / 100,
                2,
                12
            );
            int steps = Mathf.Clamp(Mathf.CeilToInt(lane.Length / segmentLength), 4, 96);
            float start = Endpoint(lane, lane.Curve.a);
            float end = Endpoint(lane, lane.Curve.d);
            Vector3 previousLeft = default;
            Vector3 previousRight = default;
            float previousPressure = 0;
            for (int i = 0; i <= steps; i++)
            {
                float t = i / (float)steps;
                float3 p = MathUtils.Position(lane.Curve, t);
                float3 tangent = MathUtils.Tangent(lane.Curve, t);
                float3 side =
                    math.normalizesafe(new float3(tangent.z, 0, -tangent.x)) * lane.Width * .5f;
                float pressure = Mathf.Lerp(
                    start,
                    lane.Snow.Pressure,
                    Mathf.SmoothStep(0, 1, t * lane.Length / 5)
                );
                pressure = Mathf.Lerp(
                    end,
                    pressure,
                    Mathf.SmoothStep(0, 1, (1 - t) * lane.Length / 5)
                );
                Vector3 left = p - side;
                Vector3 right = p + side;
                if (i > 0)
                {
                    _raster.Add(
                        previousLeft,
                        previousRight,
                        left,
                        right,
                        previousPressure,
                        pressure,
                        lane.Width
                    );
                }

                previousLeft = left;
                previousRight = right;
                previousPressure = pressure;
            }
        }

        _raster.Draw(area);
    }

    public void Dispose() => _raster.Dispose();

    public void ProbeGpu(Action<string> report) => _raster.ProbeGpu(report);

    private sealed class LaneState
    {
        public Entity Entity;
        public Bezier4x3 Curve;
        public float Width;
        public float Length;
        public uint Frame;
        public Vector3 Center;
        public bool Deleted;
        public readonly LaneSnow Snow = new();
    }
}
