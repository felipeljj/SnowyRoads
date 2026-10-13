using System;
using System.Collections.Generic;
using System.Diagnostics;
using Game.Common;
using Game.Creatures;
using Game.Prefabs;
using Game.Rendering;
using Game.Simulation;
using Game.Vehicles;
using RoadSnow.Core;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using MaintenanceVehicle = Game.Vehicles.MaintenanceVehicle;

namespace RoadSnow.Mod;

public partial class RoadSnowSystem
{
    private Camera _trafficCamera;
    private int _trafficCameraFrame = -1;
    private WearView.Snapshot _trafficView;
    private readonly Dictionary<Entity, bool> _singleTrackPrefabs = new();

    private bool HasInlineWheels(Entity prefab)
    {
        if (_singleTrackPrefabs.TryGetValue(prefab, out bool single))
            return single;
        float3 minimum = new(float.MaxValue),
            maximum = new(float.MinValue);
        int wheels = 0;
        void ReadWheels(Entity mesh, float3 offset, quaternion rotation)
        {
            if (!EntityManager.HasBuffer<ProceduralBone>(mesh))
                return;
            foreach (var bone in EntityManager.GetBuffer<ProceduralBone>(mesh, true))
            {
                if (
                    bone.m_Type != BoneType.RollingTire
                    && bone.m_Type != BoneType.SteeringTire
                    && bone.m_Type != BoneType.FixedTire
                )
                    continue;
                float3 point = offset + math.rotate(rotation, bone.m_ObjectPosition);
                minimum = math.min(minimum, point);
                maximum = math.max(maximum, point);
                wheels++;
            }
        }
        ReadWheels(prefab, 0, quaternion.identity);
        if (EntityManager.HasBuffer<SubMesh>(prefab))
            foreach (var mesh in EntityManager.GetBuffer<SubMesh>(prefab, true))
                ReadWheels(mesh.m_SubMesh, mesh.m_Position, mesh.m_Rotation);
        single = wheels >= 2 && maximum.x - minimum.x < .18f && maximum.z - minimum.z > .35f;
        if (wheels < 2)
        {
            // Some custom motorcycle meshes have no animated wheel bones.
            var prefabs = World.GetExistingSystemManaged<PrefabSystem>();
            if (prefabs != null && prefabs.TryGetPrefab<PrefabBase>(prefab, out var asset))
                single =
                    asset.name.IndexOf("motorcycle", StringComparison.OrdinalIgnoreCase) >= 0
                    || asset.name.IndexOf("motorbike", StringComparison.OrdinalIgnoreCase) >= 0;
        }
        return _singleTrackPrefabs[prefab] = single;
    }

    // these are individual tire marks and footsteps.long-terms lane wear is handled
    // separately by LaneWearMask. changing one should not reset the other
    private void DiscoverTraffic()
    {
        if (_vehicleHistory.Count < 512)
        {
            using var cars = _vehicleQuery.ToEntityArray(Allocator.Temp);
            int count = Math.Min(cars.Length, 4096);
            for (int i = 0; i < count && _vehicleHistory.Count < 512; i++)
            {
                Entity car = cars[(_scanOffset + i) % cars.Length];
                if (_vehicleHistory.ContainsKey(car) || IsSnowplow(car))
                {
                    continue;
                }

                var transform = EntityManager.GetComponentData<Game.Objects.Transform>(car);
                if (!NearTraffic(transform.m_Position))
                    continue;

                Entity road = FindTrafficOwner(
                    EntityManager.GetComponentData<CarCurrentLane>(car).m_Lane
                );
                if (
                    !HasTrafficSurface(road, false)
                    && !TryTerrainSurface(RenderedPosition(car), out _)
                )
                {
                    continue;
                }

                _vehicleHistory.Add(car, WheelPosition(car, road, transform));
            }

            if (cars.Length > 0)
            {
                _scanOffset = (_scanOffset + count) % cars.Length;
            }
        }

        if (_pedestrianHistory.Count < 128)
        {
            using var people = _pedestrianQuery.ToEntityArray(Allocator.Temp);
            int count = Math.Min(people.Length, 2048);
            for (int i = 0; i < count && _pedestrianHistory.Count < 128; i++)
            {
                Entity person = people[(_peopleScanOffset + i) % people.Length];
                if (_pedestrianHistory.ContainsKey(person))
                {
                    continue;
                }

                var position = EntityManager
                    .GetComponentData<Game.Objects.Transform>(person)
                    .m_Position;
                if (!NearTraffic(position))
                    continue;

                Entity lane = EntityManager.GetComponentData<HumanCurrentLane>(person).m_Lane;
                if (
                    !EntityManager.HasComponent<Game.Net.PedestrianLane>(lane)
                    && !EntityManager.HasComponent<Game.Net.ConnectionLane>(lane)
                )
                {
                    continue;
                }

                Entity road = FindTrafficOwner(lane);
                if (
                    !HasTrafficSurface(road, true)
                    && !TryTerrainSurface(RenderedPosition(person), out _)
                )
                {
                    continue;
                }

                _pedestrianHistory.Add(person, new PedestrianSample(road, position));
            }

            if (people.Length > 0)
            {
                _peopleScanOffset = (_peopleScanOffset + count) % people.Length;
            }
        }
    }

    private static bool HasActiveSnow(List<RoadPatch> patches, bool pedestrian)
    {
        foreach (var patch in patches)
        {
            if (patch.Active && patch.HasSnow && (pedestrian || !patch.Pedestrian))
            {
                return true;
            }
        }

        return false;
    }

    private bool HasTrafficSurface(Entity owner, bool pedestrian) =>
        (_patchesByRoad.TryGetValue(owner, out var patches) && HasActiveSnow(patches, pedestrian))
        || (_facilities?.HasOwner(owner) ?? false);

    private WheelSample WheelPosition(Entity car, Entity road, Game.Objects.Transform transform)
    {
        float halfTrack = 0.7f;
        bool bicycle = EntityManager.HasComponent<Game.Vehicles.Bicycle>(car);
        bool single = bicycle;
        if (EntityManager.HasComponent<PrefabRef>(car))
        {
            Entity prefab = EntityManager.GetComponentData<PrefabRef>(car).m_Prefab;
            single |= HasInlineWheels(prefab);
            if (EntityManager.HasComponent<ObjectGeometryData>(prefab))
            {
                halfTrack = Mathf.Clamp(
                    EntityManager.GetComponentData<ObjectGeometryData>(prefab).m_Size.x * 0.35f,
                    0.45f,
                    1.2f
                );
            }
        }

        if (single)
        {
            halfTrack = 0;
        }

        float3 right = math.rotate(transform.m_Rotation, new float3(halfTrack, 0, 0));
        return new WheelSample(
            road,
            transform.m_Position - right,
            transform.m_Position + right,
            math.rotate(transform.m_Rotation, new float3(0, 0, 1)),
            single,
            bicycle ? .075f
                : single ? .16f
                : .28f
        );
    }

    private void StampTraffic()
    {
        _carsStamped = 0;
        _trafficKeys.Clear();
        _trafficKeys.AddRange(_vehicleHistory.Keys);
        long started = Stopwatch.GetTimestamp();
        for (
            int examined = 0;
            examined < _trafficKeys.Count && (examined == 0 || Milliseconds(started) < 4);
            examined++
        )
        {
            if (_carCursor >= _trafficKeys.Count)
            {
                _carCursor = 0;
            }

            Entity car = _trafficKeys[_carCursor++];
            if (IsSnowplow(car))
                continue;
            if (
                !EntityManager.Exists(car)
                || !EntityManager.HasComponent<CarCurrentLane>(car)
                || !EntityManager.HasComponent<Game.Objects.Transform>(car)
            )
            {
                _vehicleHistory.Remove(car);
                continue;
            }

            var lane = EntityManager.GetComponentData<CarCurrentLane>(car);
            Entity road = FindTrafficOwner(lane.m_Lane);
            if (!HasTrafficSurface(road, false) && !TryTerrainSurface(RenderedPosition(car), out _))
            {
                _vehicleHistory.Remove(car);
                continue;
            }

            var transform = EntityManager.GetComponentData<Game.Objects.Transform>(car);
            if (EntityManager.HasComponent<InterpolatedTransform>(car))
            {
                var rendered = EntityManager.GetComponentData<InterpolatedTransform>(car);
                transform.m_Position = rendered.m_Position;
                transform.m_Rotation = rendered.m_Rotation;
            }
            if (!NearTraffic(transform.m_Position) && !IsWorkingSnowplow(car))
            {
                _vehicleHistory.Remove(car);
                continue;
            }

            var sample = WheelPosition(car, road, transform);
            if (_vehicleHistory.TryGetValue(car, out var old))
            {
                float distance = (sample.Left - old.Left).sqrMagnitude;
                if (distance > 0.01f && distance < 25 * 25)
                {
                    if (IsWorkingSnowplow(car))
                    {
                        float bladeWidth = Mathf.Clamp(
                            Vector3.Distance(sample.Left, sample.Right) / .7f + .4f,
                            2.4f,
                            4
                        );
                        StampCurvedPath(
                            old,
                            sample,
                            (old.Left + old.Right) * .5f,
                            (sample.Left + sample.Right) * .5f,
                            bladeWidth,
                            true
                        );
                    }
                    StampCurvedPath(old, sample, old.Left, sample.Left, sample.TireWidth, false);
                    if (!sample.SingleTrack)
                        StampCurvedPath(
                            old,
                            sample,
                            old.Right,
                            sample.Right,
                            sample.TireWidth,
                            false
                        );
                }
            }

            _vehicleHistory[car] = sample;
        }
    }

    private void StampPedestrians()
    {
        _terrainHeights = _terrainSystem.GetHeightData();
        _footprintsStamped = 0;
        _trafficKeys.Clear();
        _trafficKeys.AddRange(_pedestrianHistory.Keys);
        foreach (Entity person in _trafficKeys)
        {
            if (
                !EntityManager.Exists(person)
                || !EntityManager.HasComponent<HumanCurrentLane>(person)
                || !EntityManager.HasComponent<Game.Objects.Transform>(person)
            )
            {
                _pedestrianHistory.Remove(person);
                continue;
            }

            Entity lane = EntityManager.GetComponentData<HumanCurrentLane>(person).m_Lane;
            if (
                !EntityManager.HasComponent<Game.Net.PedestrianLane>(lane)
                && !EntityManager.HasComponent<Game.Net.ConnectionLane>(lane)
            )
            {
                _pedestrianHistory.Remove(person);
                continue;
            }

            Entity road = FindTrafficOwner(lane);
            if (
                !HasTrafficSurface(road, true)
                && !TryTerrainSurface(RenderedPosition(person), out _)
            )
            {
                _pedestrianHistory.Remove(person);
                continue;
            }

            Vector3 position = RenderedPosition(person);
            if (!NearTraffic(position))
            {
                _pedestrianHistory.Remove(person);
                continue;
            }

            if (!_pedestrianHistory.TryGetValue(person, out var old))
            {
                _pedestrianHistory[person] = new PedestrianSample(road, position);
                continue;
            }

            Vector3 previous = old.Position;
            float distance = new Vector2(
                position.x - previous.x,
                position.z - previous.z
            ).sqrMagnitude;
            if (distance > 9 || Mathf.Abs(position.y - previous.y) > 2)
            {
                _pedestrianHistory[person] = new PedestrianSample(road, position);
                continue;
            }

            old.Path.Advance(
                previous.x,
                previous.z,
                position.x,
                position.z,
                footprint =>
                {
                    var world = new Vector3(
                        footprint.X,
                        Mathf.Lerp(previous.y, position.y, footprint.IntervalFraction),
                        footprint.Z
                    );
                    var direction = new Vector3(footprint.ForwardX, 0, footprint.ForwardZ);
                    if (
                        ProjectMark(road, world, out var surface)
                        || TryTerrainSurface(world, out surface)
                    )
                    {
                        _trafficMarks.Add(
                            surface - direction * 0.145f,
                            surface + direction * 0.145f,
                            true,
                            _isPreviewActive ? 15 : SampleSnow(surface),
                            footprint.LeftFoot,
                            widthMeters: 0.16f
                        );
                        _footprintsStamped++;
                    }
                }
            );
            old.Position = position;
            old.Road = road;
        }
    }

    private Entity FindTrafficOwner(Entity lane)
    {
        Entity current = lane;
        for (int i = 0; i < 8; i++)
        {
            if (_patchesByRoad.ContainsKey(current) || (_facilities?.HasOwner(current) ?? false))
            {
                return current;
            }

            if (!EntityManager.HasComponent<Owner>(current))
            {
                break;
            }

            current = EntityManager.GetComponentData<Owner>(current).m_Owner;
        }

        return Entity.Null;
    }

    private Vector3 RenderedPosition(Entity entity) =>
        EntityManager.HasComponent<InterpolatedTransform>(entity)
            ? (Vector3)EntityManager.GetComponentData<InterpolatedTransform>(entity).m_Position
            : (Vector3)EntityManager.GetComponentData<Game.Objects.Transform>(entity).m_Position;

    private bool NearTraffic(Vector3 position)
    {
        Camera camera = _cameraSystem.activeCamera;
        if (camera == null)
        {
            return false;
        }

        if (_trafficCamera != camera || _trafficCameraFrame != UnityEngine.Time.frameCount)
        {
            _trafficView = new WearView.Snapshot(camera);
            _trafficCamera = camera;
            _trafficCameraFrame = UnityEngine.Time.frameCount;
        }
        Vector3 viewport = _trafficView.Viewport(position);
        if (
            viewport.z <= 0
            || viewport.x < -0.08f
            || viewport.x > 1.08f
            || viewport.y < -0.08f
            || viewport.y > 1.08f
        )
        {
            return false;
        }

        float distanceSquared = (position - _trafficView.Eye).sqrMagnitude;
        return distanceSquared < 1600 * 1600 && _trafficView.PixelSize(distanceSquared) < 1.2f;
    }

    private bool ProjectMark(Entity owner, Vector3 position, out Vector3 surface)
    {
        surface = default;
        float best = 2.5f;
        bool found = false;
        if (_patchesByRoad.TryGetValue(owner, out var patches))
        {
            foreach (var patch in patches)
            {
                if (
                    !patch.Active
                    || !patch.HasSnow
                    || !patch.TryGetSurface(position, out var point, out _)
                )
                {
                    continue;
                }

                float difference = Mathf.Abs(point.y - position.y);
                if (difference >= best)
                {
                    continue;
                }

                best = difference;
                surface = point;
                found = true;
            }
        }

        return found || (_facilities != null && _facilities.Project(owner, position, out surface));
    }

    private bool IsWorkingSnowplow(Entity car)
    {
        if (!IsSnowplow(car))
            return false;

        var state = EntityManager.GetComponentData<MaintenanceVehicle>(car).m_State;
        if (
            (
                (state & MaintenanceVehicleFlags.Working) == 0
                && (
                    !EntityManager.HasComponent<Car>(car)
                    || (EntityManager.GetComponentData<Car>(car).m_Flags & CarFlags.Working) == 0
                )
            )
            || (state & MaintenanceVehicleFlags.Disabled) != 0
        )
        {
            return false;
        }

        return true;
    }

    private bool IsSnowplow(Entity car)
    {
        if (
            !EntityManager.HasComponent<MaintenanceVehicle>(car)
            || !EntityManager.HasComponent<PrefabRef>(car)
        )
            return false;
        var prefab = EntityManager.GetComponentData<PrefabRef>(car).m_Prefab;
        return EntityManager.HasComponent<MaintenanceVehicleData>(prefab)
            && (
                EntityManager.GetComponentData<MaintenanceVehicleData>(prefab).m_MaintenanceType
                & MaintenanceType.Snow
            ) != 0;
    }

    private void StampCurvedPath(
        WheelSample old,
        WheelSample sample,
        Vector3 from,
        Vector3 to,
        float width,
        bool plow
    )
    {
        float distance = Vector3.Distance(from, to);
        if (distance < .01f || distance > 25)
            return;
        // Parking can rotate/reposition a vehicle between samples; do not join that teleport.
        if (distance > 2 && Vector3.Dot(old.Forward, sample.Forward) < -.5f)
            return;
        Vector3 chord = (to - from) / distance;
        Vector3 start = old.Forward;
        Vector3 end = sample.Forward;
        if (Vector3.Dot(start, chord) < 0)
            start = -start;
        if (Vector3.Dot(end, chord) < 0)
            end = -end;
        // Hermite-equivalent Bezier: vehicle headings supply the tangents, not a guessed lane.
        Vector3 c1 = from + start * (distance / 3);
        Vector3 c2 = to - end * (distance / 3);
        int steps = Math.Min(96, Math.Max(1, Mathf.CeilToInt(distance / .45f)));
        Vector3 a = from;
        for (int i = 1; i <= steps; i++)
        {
            float t = i / (float)steps,
                u = 1 - t;
            Vector3 b = u * u * u * from + 3 * u * u * t * c1 + 3 * u * t * t * c2 + t * t * t * to;
            Vector3 middle = (a + b) * .5f;
            Entity owner = sample.Road;
            if (!ProjectMark(owner, middle, out var surface))
            {
                owner = old.Road;
                if (
                    !ProjectMark(owner, middle, out surface)
                    && (plow || !TryTerrainSurface(middle, out surface))
                )
                {
                    a = b;
                    continue;
                }
            }
            Vector3 pa = ProjectMark(owner, a, out var projectedA)
                ? projectedA
                : new Vector3(a.x, surface.y, a.z);
            Vector3 pb = ProjectMark(owner, b, out var projectedB)
                ? projectedB
                : new Vector3(b.x, surface.y, b.z);
            if (plow)
                DepositPlowedSnow(owner, pa, pb, width);
            _trafficMarks.Add(
                pa,
                pb,
                false,
                _isPreviewActive ? 15 : SampleSnow(surface),
                widthMeters: width,
                plow: plow
            );
            _carsStamped++;
            a = b;
        }
    }

    private readonly struct WheelSample
    {
        public readonly Entity Road;
        public readonly Vector3 Left;
        public readonly Vector3 Right;
        public readonly Vector3 Forward;
        public readonly bool SingleTrack;
        public readonly float TireWidth;

        public WheelSample(
            Entity road,
            Vector3 left,
            Vector3 right,
            Vector3 forward,
            bool singleTrack = false,
            float tireWidth = .28f
        )
        {
            Road = road;
            Left = left;
            Right = right;
            Forward = forward;
            SingleTrack = singleTrack;
            TireWidth = tireWidth;
        }
    }

    private sealed class PedestrianSample
    {
        public Entity Road;
        public Vector3 Position;
        public readonly FootstepPath Path = new();

        public PedestrianSample(Entity road, Vector3 position)
        {
            Road = road;
            Position = position;
        }
    }
}
