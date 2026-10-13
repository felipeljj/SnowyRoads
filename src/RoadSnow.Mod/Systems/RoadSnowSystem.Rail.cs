using System.Collections.Generic;
using Game.Vehicles;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace RoadSnow.Mod;

public partial class RoadSnowSystem
{
    private readonly Dictionary<Entity, WheelSample> _trainHistory = new();
    private readonly HashSet<Entity> _trainsSeen = new();
    private readonly List<Entity> _trainsGone = new();

    private void StampTrains()
    {
        _trainsSeen.Clear();
        using var trains = _trainQuery.ToEntityArray(Allocator.Temp);
        foreach (var train in trains)
        {
            var transform = EntityManager.GetComponentData<Game.Objects.Transform>(train);
            if (!NearTraffic(transform.m_Position))
                continue;
            Entity lane = EntityManager.GetComponentData<TrainCurrentLane>(train).m_Front.m_Lane;
            Entity owner = FindTrafficOwner(lane);
            if (!HasTrafficSurface(owner, false))
                continue;
            _trainsSeen.Add(train);
            Vector3 right = math.rotate(transform.m_Rotation, new float3(.7175f, 0, 0));
            Vector3 position = transform.m_Position;
            var sample = new WheelSample(
                owner,
                position - right,
                position + right,
                math.rotate(transform.m_Rotation, new float3(0, 0, 1))
            );
            if (_trainHistory.TryGetValue(train, out var previous))
            {
                StampCurvedPath(
                    previous,
                    sample,
                    (previous.Left + previous.Right) * .5f,
                    (sample.Left + sample.Right) * .5f,
                    2.1f,
                    false
                );
                StampCurvedPath(previous, sample, previous.Left, sample.Left, .13f, false);
                StampCurvedPath(previous, sample, previous.Right, sample.Right, .13f, false);
            }
            _trainHistory[train] = sample;
        }
        _trainsGone.Clear();
        foreach (var train in _trainHistory.Keys)
            if (!_trainsSeen.Contains(train))
                _trainsGone.Add(train);
        foreach (var train in _trainsGone)
            _trainHistory.Remove(train);
    }
}
