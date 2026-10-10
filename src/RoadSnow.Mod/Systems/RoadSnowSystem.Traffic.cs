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
    // these are individual tire marks and footsteps.long-terms lane wear is handled
    // separately by LaneWearMask. changing one should not reset the other
    private void DiscoverTraffic()
    {
        using (var plows = _snowplowQuery.ToEntityArray(Allocator.Temp))
        {
            foreach (var car in plows)
            {
                if (_vehicleHistory.ContainsKey(car) || !IsWorkingSnowplow(car))
                {
                    continue;
                }

                Entity road = FindTrafficOwner(
                    EntityManager.GetComponentData<CarCurrentLane>(car).m_Lane
                );
                if (!HasTrafficSurface(road, false))
                {
                    continue;
                }

                _vehicleHistory.Add(
                    car,
                    WheelPosition(
                        car,
                        road,
                        EntityManager.GetComponentData<Game.Objects.Transform>(car)
                    )
                );
            }
        }

        if (_vehicleHistory.Count < 512)
        {
            using var cars = _vehicleQuery.ToEntityArray(Allocator.Temp);
            int count = Math.Min(cars.Length, 4096);
            for (int i = 0; i < count && _vehicleHistory.Count < 512; i++)
            {
                Entity car = cars[(_scanOffset + i) % cars.Length];
                if (_vehicleHistory.ContainsKey(car))
                {
                    continue;
                }

                Entity road = FindTrafficOwner(
                    EntityManager.GetComponentData<CarCurrentLane>(car).m_Lane
                );
                if (!HasTrafficSurface(road, false))
                {
                    continue;
                }

                var transform = EntityManager.GetComponentData<Game.Objects.Transform>(car);
                if (!NearTraffic(transform.m_Position))
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

                Entity lane = EntityManager.GetComponentData<HumanCurrentLane>(person).m_Lane;
                if (!EntityManager.HasComponent<Game.Net.PedestrianLane>(lane))
                {
                    continue;
                }

                Entity road = FindTrafficOwner(lane);
                if (!HasTrafficSurface(road, true))
                {
                    continue;
                }

                var position = EntityManager
                    .GetComponentData<Game.Objects.Transform>(person)
                    .m_Position;
                if (!NearTraffic(position))
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
        if (EntityManager.HasComponent<PrefabRef>(car))
        {
            Entity prefab = EntityManager.GetComponentData<PrefabRef>(car).m_Prefab;
            if (EntityManager.HasComponent<ObjectGeometryData>(prefab))
            {
                halfTrack = Mathf.Clamp(
                    EntityManager.GetComponentData<ObjectGeometryData>(prefab).m_Size.x * 0.35f,
                    0.45f,
                    1.2f
                );
            }
        }

        if (EntityManager.HasComponent<Game.Vehicles.Bicycle>(car))
        {
            halfTrack = 0;
        }

        float3 right = math.rotate(transform.m_Rotation, new float3(halfTrack, 0, 0));
        return new WheelSample(road, transform.m_Position - right, transform.m_Position + right);
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
            if (!HasTrafficSurface(road, false))
            {
                _vehicleHistory.Remove(car);
                continue;
            }

            var transform = EntityManager.GetComponentData<Game.Objects.Transform>(car);
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
                    bool bicycle = EntityManager.HasComponent<Game.Vehicles.Bicycle>(car);
                    if (IsWorkingSnowplow(car))
                    {
                        Vector3 from = (old.Left + old.Right) * 0.5f;
                        Vector3 to = (sample.Left + sample.Right) * 0.5f;
                        float bladeWidth = Mathf.Clamp(
                            Vector3.Distance(sample.Left, sample.Right) / 0.7f + 0.4f,
                            2.4f,
                            4
                        );
                        _carsStamped += StampWheelImpressions(road, from, to, bladeWidth, true);
                        if (old.Road != road && HasTrafficSurface(old.Road, false))
                        {
                            _carsStamped += StampWheelImpressions(
                                old.Road,
                                from,
                                to,
                                bladeWidth,
                                true
                            );
                        }
                    }

                    _carsStamped += StampWheelImpressions(
                        road,
                        old.Left,
                        sample.Left,
                        bicycle ? 0.075f : 0.38f
                    );
                    if (!bicycle)
                    {
                        _carsStamped += StampWheelImpressions(road, old.Right, sample.Right);
                    }

                    if (old.Road != road && HasTrafficSurface(old.Road, false))
                    {
                        _carsStamped += StampWheelImpressions(
                            old.Road,
                            old.Left,
                            sample.Left,
                            bicycle ? 0.075f : 0.38f
                        );
                        if (!bicycle)
                        {
                            _carsStamped += StampWheelImpressions(
                                old.Road,
                                old.Right,
                                sample.Right
                            );
                        }
                    }
                }
            }

            _vehicleHistory[car] = sample;
        }
    }

    private void StampPedestrians()
    {
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
            if (!EntityManager.HasComponent<Game.Net.PedestrianLane>(lane))
            {
                _pedestrianHistory.Remove(person);
                continue;
            }

            Entity road = FindTrafficOwner(lane);
            if (!HasTrafficSurface(road, true))
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
                    if (ProjectMark(road, world, out var surface))
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

        Vector3 viewport = camera.WorldToViewportPoint(position);
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

        float distance = Vector3.Distance(position, camera.transform.position);
        float mpp = camera.orthographic
            ? camera.orthographicSize * 2 / Math.Max(1, camera.pixelHeight)
            : distance
                * 2
                * Mathf.Tan(camera.fieldOfView * Mathf.Deg2Rad * 0.5f)
                / Math.Max(1, camera.pixelHeight);
        return distance < 550 && mpp < 0.36f;
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
        if (
            !EntityManager.HasComponent<MaintenanceVehicle>(car)
            || !EntityManager.HasComponent<PrefabRef>(car)
        )
        {
            return false;
        }

        var state = EntityManager.GetComponentData<MaintenanceVehicle>(car).m_State;
        if (
            (state & MaintenanceVehicleFlags.Working) == 0
            || (state & MaintenanceVehicleFlags.Disabled) != 0
        )
        {
            return false;
        }

        var prefab = EntityManager.GetComponentData<PrefabRef>(car).m_Prefab;
        return EntityManager.HasComponent<MaintenanceVehicleData>(prefab)
            && (
                EntityManager.GetComponentData<MaintenanceVehicleData>(prefab).m_MaintenanceType
                & MaintenanceType.Snow
            ) != 0;
    }

    private int StampWheelImpressions(
        Entity owner,
        Vector3 from,
        Vector3 to,
        float width = 0.38f,
        bool plow = false
    )
    {
        float distance = Vector3.Distance(from, to);
        int steps = Math.Min(32, Math.Max(1, Mathf.CeilToInt(distance / 1.5f)));
        int added = 0;
        for (int step = 0; step < steps; step++)
        {
            Vector3 a = Vector3.Lerp(from, to, step / (float)steps);
            Vector3 b = Vector3.Lerp(from, to, (step + 1f) / steps);
            if (!ProjectMark(owner, (a + b) * 0.5f, out var middle))
            {
                continue;
            }

            if (ProjectMark(owner, a, out var projectedA))
            {
                a = projectedA;
            }
            else
            {
                a.y = middle.y;
            }

            if (ProjectMark(owner, b, out var projectedB))
            {
                b = projectedB;
            }
            else
            {
                b.y = middle.y;
            }

            _trafficMarks.Add(
                a,
                b,
                false,
                _isPreviewActive ? 15 : SampleSnow(middle),
                widthMeters: width,
                plow: plow
            );
            added++;
        }

        return added;
    }

    private readonly struct WheelSample
    {
        public readonly Entity Road;
        public readonly Vector3 Left;
        public readonly Vector3 Right;

        public WheelSample(Entity road, Vector3 left, Vector3 right)
        {
            Road = road;
            Left = left;
            Right = right;
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
