using System.Collections.Generic;
using Game.Vehicles;
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

    private sealed class PlowSample
    {
        public WheelSample Wheels;
        public float LastWorking;
    }

    private void StampSnowplows(float now)
    {
        _plowsSeen.Clear();
        using var vehicles = _snowplowQuery.ToEntityArray(Allocator.Temp);
        foreach (var car in vehicles)
        {
            if (!IsSnowplow(car))
                continue;
            _plowsSeen.Add(car);
            Entity road = FindTrafficOwner(
                EntityManager.GetComponentData<CarCurrentLane>(car).m_Lane
            );
            var transform = EntityManager.GetComponentData<Game.Objects.Transform>(car);
            transform.m_Position = RenderedPosition(car);
            if (EntityManager.HasComponent<Game.Rendering.InterpolatedTransform>(car))
                transform.m_Rotation = EntityManager
                    .GetComponentData<Game.Rendering.InterpolatedTransform>(car)
                    .m_Rotation;
            var wheels = WheelPosition(car, road, transform);
            bool working = IsWorkingSnowplow(car);
            if (!_plowHistory.TryGetValue(car, out var previous))
            {
                _plowHistory.Add(
                    car,
                    new PlowSample { Wheels = wheels, LastWorking = working ? now : -10 }
                );
                continue;
            }
            if (working)
                previous.LastWorking = now;
            var state = EntityManager.GetComponentData<MaintenanceVehicle>(car).m_State;
            bool clearing =
                working
                || (
                    now - previous.LastWorking < .75f
                    && (
                        state
                        & (MaintenanceVehicleFlags.Disabled | MaintenanceVehicleFlags.Returning)
                    ) == 0
                );
            if (clearing && road != Entity.Null)
            {
                float width = Mathf.Clamp(
                    Vector3.Distance(wheels.Left, wheels.Right) / .7f + .4f,
                    2.4f,
                    4
                );
                StampCurvedPath(
                    previous.Wheels,
                    wheels,
                    (previous.Wheels.Left + previous.Wheels.Right) * .5f,
                    (wheels.Left + wheels.Right) * .5f,
                    width,
                    true
                );
            }
            previous.Wheels = wheels;
        }
        _plowsGone.Clear();
        foreach (var car in _plowHistory.Keys)
            if (!_plowsSeen.Contains(car))
                _plowsGone.Add(car);
        foreach (var car in _plowsGone)
            _plowHistory.Remove(car);
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
        // Only transfer the snow still present, so repeated passes cannot manufacture piles.
        float removed =
            Mathf.Clamp(depth, 0, 80)
            * .001f
            * width
            * length
            * (1 - _wearHistory.Sample(center.x, center.y, center.z));
        Vector3 right = Vector3.Cross(Vector3.up, direction).normalized;
        Vector3 target = center + right * (width * .5f + .5f);
        if (ProjectMark(owner, target, out var surface))
            _plowedSnow.Add(surface, direction, removed);
    }
}
