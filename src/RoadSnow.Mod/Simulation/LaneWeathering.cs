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

internal sealed class LaneWeathering
{
    private readonly Entity[] _lanes;
    private readonly short[] _laneAt;
    private readonly float[] _weight;
    private readonly float[] _flow;
    private readonly float[] _strength;

    private LaneWeathering(Entity[] lanes, int cells)
    {
        _lanes = lanes;
        _flow = new float[lanes.Length];
        _laneAt = new short[cells];
        _weight = new float[cells];
        _strength = new float[cells];
        for (int i = 0; i < cells; i++)
        {
            _laneAt[i] = -1;
        }
    }

    public static LaneWeathering Create(EntityManager manager, Entity owner, Vector3[] points)
    {
        if (!manager.HasBuffer<SubLane>(owner))
        {
            return null;
        }

        var owners = new HashSet<Entity> { owner };
        if (manager.HasComponent<Edge>(owner))
        {
            var edge = manager.GetComponentData<Edge>(owner);
            owners.Add(edge.m_Start);
            owners.Add(edge.m_End);
        }

        if (manager.HasBuffer<ConnectedEdge>(owner))
        {
            foreach (var edge in manager.GetBuffer<ConnectedEdge>(owner, true))
            {
                owners.Add(edge.m_Edge);
            }
        }

        var entities = new List<Entity>();
        var curves = new List<Bezier4x3>();
        var widths = new List<float>();
        foreach (var laneOwner in owners)
        {
            if (!manager.HasBuffer<SubLane>(laneOwner))
            {
                continue;
            }

            foreach (var item in manager.GetBuffer<SubLane>(laneOwner, true))
            {
                var lane = item.m_SubLane;
                if (entities.Contains(lane))
                {
                    continue;
                }

                if (
                    !manager.HasComponent<CarLane>(lane)
                    || !manager.HasComponent<LaneFlow>(lane)
                    || !manager.HasComponent<Curve>(lane)
                    || manager.HasComponent<MasterLane>(lane)
                    || manager.HasComponent<Deleted>(lane)
                )
                {
                    continue;
                }

                float width = 3;
                if (manager.HasComponent<PrefabRef>(lane))
                {
                    var prefab = manager.GetComponentData<PrefabRef>(lane).m_Prefab;
                    if (manager.HasComponent<NetLaneData>(prefab))
                    {
                        width = manager.GetComponentData<NetLaneData>(prefab).m_Width;
                    }
                }

                if (width < 1.8f || width > 6)
                {
                    continue;
                }

                entities.Add(lane);
                curves.Add(manager.GetComponentData<Curve>(lane).m_Bezier);
                widths.Add(width);
                if (entities.Count >= 256)
                {
                    break;
                }
            }

            if (entities.Count >= 256)
            {
                break;
            }
        }

        if (entities.Count == 0)
        {
            return null;
        }

        var result = new LaneWeathering(entities.ToArray(), points.Length);
        for (int lane = 0; lane < curves.Count; lane++)
        {
            float3 a = curves[lane].a;
            for (int segment = 1; segment <= 12; segment++)
            {
                float3 b = MathUtils.Position(curves[lane], segment / 12f);
                float3 ab = b - a;
                float denominator = math.lengthsq(ab.xz);
                if (denominator < 0.0001f)
                {
                    a = b;
                    continue;
                }

                for (int i = 0; i < points.Length; i++)
                {
                    float3 p = points[i];
                    float t = math.saturate(math.dot((p - a).xz, ab.xz) / denominator);
                    float3 nearest = a + t * ab;
                    if (math.abs(p.y - nearest.y) > 0.8f)
                    {
                        continue;
                    }

                    float distance = math.distance(p.xz, nearest.xz);
                    float halfWidth = widths[lane] * 0.5f;
                    if (distance >= halfWidth)
                    {
                        continue;
                    }

                    float edge = math.saturate(
                        (halfWidth - distance) / math.max(0.6f, halfWidth * 0.65f)
                    );
                    edge = edge * edge * (3 - 2 * edge);
                    float noise = SnowAppearance.Grain(p.x + 43, p.z - 19);
                    float weight = edge * (0.6f + noise * 0.4f);
                    if (weight <= result._weight[i])
                    {
                        continue;
                    }

                    result._weight[i] = weight;
                    result._laneAt[i] = (short)lane;
                }

                a = b;
            }
        }

        return result;
    }

    public bool Advance(
        EntityManager manager,
        RoadSnowField field,
        float hours,
        float temperature,
        float influence
    )
    {
        if (hours <= 0 || influence <= 0)
        {
            return false;
        }

        for (int i = 0; i < _lanes.Length; i++)
        {
            _flow[i] = 0;
            if (!manager.HasComponent<LaneFlow>(_lanes[i]))
            {
                continue;
            }

            var flow = manager.GetComponentData<LaneFlow>(_lanes[i]);
            float volume = math.max(0, math.cmax(flow.m_Distance));
            _flow[i] = volume / (volume + 12);
        }

        for (int i = 0; i < _strength.Length; i++)
        {
            _strength[i] = _laneAt[i] < 0 ? 0 : _flow[_laneAt[i]] * _weight[i] * influence;
        }

        return field.ApplyTraffic(_strength, hours, temperature);
    }

    public float Compaction(int cell, byte coverage) =>
        _laneAt[cell] < 0 ? 0 : _weight[cell] * (1 - coverage / 255f);
}
