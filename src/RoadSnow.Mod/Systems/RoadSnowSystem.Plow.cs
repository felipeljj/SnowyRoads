using System.Collections.Generic;
using Game.Vehicles;
using RoadSnow.Core;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;

namespace RoadSnow.Mod;

public partial class RoadSnowSystem
{
    private readonly PlowedSnow _plowedSnow = new();
    private Shader _plowShader;
    private readonly Dictionary<Entity, PlowSample> _plowHistory = new();
    private readonly HashSet<Entity> _plowsSeen = new();
    private readonly List<Entity> _plowsGone = new();
    private readonly List<WearTile> _plowTileChanges = new();
    private int _activePlows,
        _plowSegments,
        _plowDiscontinuities;
    private double _plowMeters;
    private readonly PlowSurfaceClip _plowClip = new();
    private int _plowRoadTriangles,
        _plowFallbackSegments;
    private readonly PlowRoute _plowRoute = new();
    private readonly HashSet<Entity> _plowRouteOwners = new();
    private readonly HashSet<Entity> _plowRouteNodes = new();
    private readonly HashSet<Entity> _sectionOwners = new();
    private readonly Dictionary<Vector3, double> _sectionPlowHistory = new();
    private readonly Dictionary<Entity, double> _completedSectionOwners = new();
    private int _sectionPlows;

    private bool ClearSectionOwner(Entity owner, float now)
    {
        if (owner == Entity.Null || !_sectionOwners.Add(owner))
            return false;
        EnsurePlowGeometry(owner, now);
        bool changed = false;
        if (_patchesByRoad.TryGetValue(owner, out var patches))
            foreach (var patch in patches)
            {
                patch.DepositSectionSnow(
                    _isPreviewActive ? 15 : SampleSnow(patch.Center),
                    _snowfall.TotalMm,
                    _plowedSnow.Add
                );
                if (patch.CompleteSectionPlow(_snowfall.TotalMm))
                {
                    changed = true;
                    _sectionPlows++;
                    _plowSegments++;
                    _plowMeters += patch.Field.LengthMeters;
                }
                if (patch.PlowedAt >= 0)
                {
                    _sectionPlowHistory[CachedCoverage.Key(patch.Center)] = patch.PlowedAt;
                    _completedSectionOwners[owner] = patch.PlowedAt;
                }
            }
        return changed;
    }

    private void StampSnowplows(float now)
    {
        _activePlows = 0;
        if (Mod.Settings?.SnowplowClearing != true)
            return;
        // Preserve state at a pause. Completion does not depend on joining
        // individual movement samples, so resume cannot punch a path-sized gap.
        if (_simulationSystem.selectedSpeed <= 0)
            return;
        _sectionOwners.Clear();
        _plowsSeen.Clear();
        bool changed = false;
        int before = _patches.Count;
        using var vehicles = _snowplowQuery.ToEntityArray(Allocator.Temp);
        foreach (var car in vehicles)
        {
            if (!IsSnowplow(car))
                continue;
            _plowsSeen.Add(car);
            var state = EntityManager.GetComponentData<MaintenanceVehicle>(car).m_State;
            Entity lane = EntityManager.GetComponentData<CarCurrentLane>(car).m_Lane;
            if (
                (state & MaintenanceVehicleFlags.Disabled) != 0
                || !EntityManager.HasComponent<Game.Net.CarLane>(lane)
            )
                continue;
            Entity road = FindPlowOwner(lane);
            if (road == Entity.Null)
                continue;
            _activePlows++;
            changed |= ClearSectionOwner(road, now);
            if (EntityManager.HasComponent<Game.Net.Edge>(road))
            {
                var edge = EntityManager.GetComponentData<Game.Net.Edge>(road);
                // Complete all faces of each connected junction, including
                // approaches owned by the other edges, without their road bodies.
                changed |= ClearSectionOwner(edge.m_Start, now);
                changed |= ClearSectionOwner(edge.m_End, now);
            }
        }
        if (changed || before != _patches.Count)
        {
            _atlas.Flush();
            CompleteCoverageReplacements();
        }
    }

    private Entity FindPlowOwner(Entity lane)
    {
        // Saved coverage can be visible before live helper meshes exist. The
        // lane's native owner remains valid independently of those helpers.
        for (int i = 0; i < 8 && lane != Entity.Null; i++)
        {
            if (
                EntityManager.HasComponent<Game.Net.Edge>(lane)
                || EntityManager.HasBuffer<Game.Net.ConnectedEdge>(lane)
            )
                return lane;
            if (!EntityManager.HasComponent<Game.Common.Owner>(lane))
                break;
            lane = EntityManager.GetComponentData<Game.Common.Owner>(lane).m_Owner;
        }
        return Entity.Null;
    }

    private void PreparePlowRoute(Entity oldOwner, Entity owner, float now)
    {
        _plowRoute.Clear();
        _plowRouteOwners.Clear();
        _plowRouteNodes.Clear();
        CollectPlowOwner(oldOwner, now, true);
        CollectPlowOwner(owner, now, true);
    }

    private void CollectPlowOwner(Entity owner, float now, bool includeNodes)
    {
        if (owner == Entity.Null)
            return;
        if (_plowRouteOwners.Add(owner))
        {
            EnsurePlowGeometry(owner, now);
            if (_patchesByRoad.TryGetValue(owner, out var patches))
                _plowRoute.Add(patches);
        }
        if (includeNodes && EntityManager.HasComponent<Game.Net.Edge>(owner))
        {
            var edge = EntityManager.GetComponentData<Game.Net.Edge>(owner);
            CollectPlowNode(edge.m_Start, now);
            CollectPlowNode(edge.m_End, now);
        }
        else if (includeNodes)
            CollectPlowNode(owner, now);
    }

    private void CollectPlowNode(Entity node, float now)
    {
        if (node == Entity.Null || !_plowRouteNodes.Add(node))
            return;
        if (EntityManager.HasBuffer<Game.Net.ConnectedEdge>(node))
            foreach (var connected in EntityManager.GetBuffer<Game.Net.ConnectedEdge>(node, true))
                CollectPlowOwner(connected.m_Edge, now, false);
        // Creation of connected plans fills this shared bucket with all the
        // junction pieces, including those on the opposite side of the lane.
        if (_patchesByRoad.TryGetValue(node, out var patches))
            _plowRoute.Add(patches);
    }

    private bool ProjectPlowSurface(Vector3 position, out Vector3 surface)
    {
        surface = default;
        float best = 2.5f;
        bool found = false;
        foreach (var patch in _plowRoute.Patches)
            if (
                patch.Active
                && patch.HasSnow
                && patch.TryGetSurface(position, out var point, out _)
            )
            {
                float difference = Mathf.Abs(point.y - position.y);
                if (difference >= best)
                    continue;
                best = difference;
                surface = point;
                found = true;
            }
        return found;
    }

    private bool PlowEntireRoad(Vector3 from, Vector3 to)
    {
        int triangles = 0;
        Vector3 direction = to - from;
        direction.y = 0;
        direction.Normalize();
        foreach (var patch in _plowRoute.Patches)
        {
            _trafficMarks.PlowFeather = new PlowFeather(to.x, to.z, direction.x, direction.z);
            triangles += patch.PlowRoad(
                from,
                to,
                _plowClip,
                _trafficMarks.AddPlowTriangle,
                _trafficMarks.ConfigurePlowFeather
            );
        }
        _plowRoadTriangles += triangles;
        return triangles > 0;
    }

    private sealed class PlowSample
    {
        public WheelSample Wheels;
        public Entity Lane;
        public float Time;
    }

    private void StampSweptSnowplows(float now)
    {
        _plowsSeen.Clear();
        _activePlows = 0;
        if (_simulationSystem.selectedSpeed <= 0)
        {
            // Interpolation jitter while paused is not a vehicle passage.
            return;
        }
        using var vehicles = _snowplowQuery.ToEntityArray(Allocator.Temp);
        foreach (var car in vehicles)
        {
            if (!IsSnowplow(car))
                continue;
            _plowsSeen.Add(car);
            Entity lane = EntityManager.GetComponentData<CarCurrentLane>(car).m_Lane;
            Entity road = FindPlowOwner(lane);
            EnsurePlowGeometry(road, now);
            var transform = EntityManager.GetComponentData<Game.Objects.Transform>(car);
            transform.m_Position = RenderedPosition(car);
            if (EntityManager.HasComponent<Game.Rendering.InterpolatedTransform>(car))
                transform.m_Rotation = EntityManager
                    .GetComponentData<Game.Rendering.InterpolatedTransform>(car)
                    .m_Rotation;
            var wheels = WheelPosition(car, road, transform);
            if (!_plowHistory.TryGetValue(car, out var previous))
            {
                _plowHistory.Add(
                    car,
                    new PlowSample
                    {
                        Wheels = wheels,
                        Lane = lane,
                        Time = now,
                    }
                );
                continue;
            }
            var state = EntityManager.GetComponentData<MaintenanceVehicle>(car).m_State;
            // Working denotes a native request/waypoint, not the complete road
            // traversed by the blade. Clear the driven path, including connectors
            // and the return trip, but never disabled vehicles or non-road lanes.
            bool clearing =
                (state & MaintenanceVehicleFlags.Disabled) == 0
                && EntityManager.HasComponent<Game.Net.CarLane>(lane)
                && EntityManager.HasComponent<Game.Net.Curve>(lane);
            if (clearing)
            {
                _activePlows++;
                Vector3 oldCenter = (previous.Wheels.Left + previous.Wheels.Right) * .5f;
                Vector3 center = (wheels.Left + wheels.Right) * .5f;
                if (PlowSweep.Accumulate((center - oldCenter).sqrMagnitude, lane == previous.Lane))
                    continue;
                float width = Mathf.Clamp(
                    Vector3.Distance(wheels.Left, wheels.Right) / .7f + .4f,
                    2.4f,
                    4
                );
                StampCurvedPath(
                    previous.Wheels,
                    wheels,
                    oldCenter,
                    center,
                    width,
                    true,
                    previous.Lane,
                    lane,
                    Mathf.Min(
                        100,
                        5
                            + 45
                                * Mathf.Max(1, _simulationSystem.selectedSpeed)
                                * Mathf.Clamp(now - previous.Time, 0, .5f)
                    )
                );
            }
            previous.Wheels = wheels;
            previous.Lane = lane;
            previous.Time = now;
        }
        _plowsGone.Clear();
        foreach (var car in _plowHistory.Keys)
            if (!_plowsSeen.Contains(car))
                _plowsGone.Add(car);
        foreach (var car in _plowsGone)
            _plowHistory.Remove(car);
    }

    private bool ProjectPlowLane(Entity lane, Vector3 position, float width, out Vector3 surface)
    {
        surface = default;
        return lane != Entity.Null
            && EntityManager.HasComponent<Game.Net.CarLane>(lane)
            && EntityManager.HasComponent<Game.Net.Curve>(lane)
            && PlowSweep.ProjectLane(
                EntityManager.GetComponentData<Game.Net.Curve>(lane).m_Bezier,
                position,
                width * .5f + .5f,
                out surface
            );
    }

    private void DepositPlowedSnow(Entity owner, Vector3 from, Vector3 to, float width)
    {
        float length = Vector3.Distance(from, to);
        if (length < .001f)
            return;
        Vector3 direction = (to - from) / length;
        Vector3 center = (from + to) * .5f;
        float depth = _isPreviewActive ? 15 : SampleSnow(center);
        if (depth < .2f)
            return;
        Vector3? roadEdge = null;
        if (_patchesByRoad.TryGetValue(owner, out var patches))
            foreach (var patch in patches)
                if (patch.TryPlowEdge(center, direction, out var edge, out float roadWidth))
                {
                    roadEdge = edge;
                    width = roadWidth;
                    break;
                }
        // Only transfer the snow still present, so repeated passes cannot manufacture piles.
        float removed =
            Mathf.Clamp(depth, 0, 80)
            * .001f
            * width
            * length
            * (1 - _wearHistory.Sample(center.x, center.y, center.z));
        Vector3 right = Vector3.Cross(Vector3.up, direction).normalized;
        Vector3 target = roadEdge ?? center + right * (width * .5f + .5f);
        if (ProjectMark(owner, target, out var surface))
            _plowedSnow.Add(surface, direction, removed);
    }
}
