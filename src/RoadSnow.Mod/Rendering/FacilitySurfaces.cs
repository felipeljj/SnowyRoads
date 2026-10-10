using System;
using System.Collections.Generic;
using System.Diagnostics;
using Colossal.Mathematics;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;
using UnityEngine.Rendering;
using AreaGeometry = Game.Areas.Geometry;
using AreaNode = Game.Areas.Node;
using AreaTriangle = Game.Areas.Triangle;
using CarLane = Game.Net.CarLane;
using Object = UnityEngine.Object;
using PedestrianLane = Game.Net.PedestrianLane;
using SubLane = Game.Net.SubLane;

namespace RoadSnow.Mod;

internal sealed class FacilitySurfaces : IDisposable
{
    // placeable assets can have their own ground areas outside the road network
    // these overlays cover those floors, such as the entrance around a metro station
    private readonly EntityQuery _query;
    private readonly EntityQuery _objects;
    private readonly EntityManager _manager;
    private readonly Material _material;
    private readonly GameObject _object;
    private readonly Mesh _mesh;
    private readonly MeshRenderer _renderer;
    private Dictionary<Entity, List<Patch>> _owners = new();
    private Dictionary<Entity, List<Patch>> _nextOwners = new();
    private readonly List<(Entity Entity, float Distance, bool Lanes)> _candidates = new();
    private readonly List<Vector3> _vertices = new();
    private readonly List<int> _indices = new();
    private readonly List<Vector2> _uv = new();
    private readonly Plane[] _frustum = new Plane[6];
    private int _cursor = -1;
    private int _count;
    private int _scan;
    private NativeArray<Entity> _scanEntities;
    private NativeArray<AreaGeometry> _scanGeometry;
    private NativeArray<Entity> _scanObjects;
    private NativeArray<Game.Objects.Transform> _scanTransforms;
    private int _objectScan;
    private Vector3 _eye;
    private float _nextRefresh;
    public int Count { get; private set; }
    public int Triangles => _indices.Count / 3;
    public double UpdateMs { get; private set; }

    public FacilitySurfaces(EntityManager manager, Material source, Shader shader)
    {
        _manager = manager;
        _query = manager.CreateEntityQuery(
            new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Game.Areas.Surface>(),
                    ComponentType.ReadOnly<AreaGeometry>(),
                    ComponentType.ReadOnly<AreaNode>(),
                    ComponentType.ReadOnly<AreaTriangle>(),
                    ComponentType.ReadOnly<Owner>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Game.Tools.Temp>(),
                },
            }
        );
        _objects = manager.CreateEntityQuery(
            new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Game.Objects.Transform>(),
                    ComponentType.ReadOnly<SubLane>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Game.Tools.Temp>(),
                },
            }
        );
        _material = new Material(source)
        {
            shader = shader,
            name = "RoadSnow facility impressions",
            renderQueue = source.renderQueue + 2,
        };
        _material.SetTexture("_BaseColorMap", Texture2D.blackTexture);
        _object = new GameObject("RoadSnow facility floor batch")
        {
            hideFlags = HideFlags.HideAndDontSave,
        };
        _mesh = new Mesh
        {
            name = "RoadSnow native floor polygons",
            indexFormat = IndexFormat.UInt32,
        };
        _mesh.MarkDynamic();
        _object.AddComponent<MeshFilter>().sharedMesh = _mesh;
        _renderer = _object.AddComponent<MeshRenderer>();
        _renderer.sharedMaterial = _material;
        _renderer.shadowCastingMode = ShadowCastingMode.Off;
        _renderer.receiveShadows = true;
        _renderer.lightProbeUsage = LightProbeUsage.BlendProbes;
    }

    public void SetColor(Color color) => _material.SetColor("_BaseColor", color);

    public bool HasOwner(Entity owner) => _owners.ContainsKey(owner);

    public void Update(Camera camera, float now)
    {
        long start = Stopwatch.GetTimestamp();
        if (_cursor == -1)
        {
            if (now < _nextRefresh)
            {
                return;
            }

            _nextRefresh = now + 3;
            _candidates.Clear();
            GeometryUtility.CalculateFrustumPlanes(camera, _frustum);
            _scanEntities = _query.ToEntityArray(Allocator.Persistent);
            _scanGeometry = _query.ToComponentDataArray<AreaGeometry>(Allocator.Persistent);
            _scanObjects = _objects.ToEntityArray(Allocator.Persistent);
            _scanTransforms = _objects.ToComponentDataArray<Game.Objects.Transform>(
                Allocator.Persistent
            );
            _objectScan = 0;
            _eye = camera.transform.position;
            _scan = 0;
            _cursor = -2;
        }

        if (_cursor == -2)
        {
            while (_scan < _scanEntities.Length && Elapsed(start) < .65)
            {
                int i = _scan++;
                var native = _scanGeometry[i].m_Bounds;
                var bounds = new Bounds((native.min + native.max) * .5f, native.max - native.min);
                bounds.Expand(new Vector3(.1f, 2, .1f));
                float distance = bounds.SqrDistance(_eye);
                if (distance < 450 * 450 && GeometryUtility.TestPlanesAABB(_frustum, bounds))
                {
                    _candidates.Add((_scanEntities[i], distance, false));
                }

                if (_candidates.Count > 256)
                {
                    _candidates.Sort((a, b) => a.Distance.CompareTo(b.Distance));
                    _candidates.RemoveRange(128, _candidates.Count - 128);
                }
            }

            if (_scan < _scanEntities.Length)
            {
                UpdateMs = Elapsed(start);
                return;
            }

            while (_objectScan < _scanObjects.Length && Elapsed(start) < .9)
            {
                int i = _objectScan++;
                Vector3 position = _scanTransforms[i].m_Position;
                float distance = (position - _eye).sqrMagnitude;
                if (
                    distance < 450 * 450
                    && GeometryUtility.TestPlanesAABB(
                        _frustum,
                        new Bounds(position, Vector3.one * 80)
                    )
                )
                {
                    _candidates.Add((_scanObjects[i], distance, true));
                }

                if (_candidates.Count > 256)
                {
                    _candidates.Sort((a, b) => a.Distance.CompareTo(b.Distance));
                    _candidates.RemoveRange(128, _candidates.Count - 128);
                }
            }

            if (_objectScan < _scanObjects.Length)
            {
                UpdateMs = Elapsed(start);
                return;
            }

            _scanEntities.Dispose();
            _scanGeometry.Dispose();
            _scanObjects.Dispose();
            _scanTransforms.Dispose();
            _candidates.Sort((a, b) => a.Distance.CompareTo(b.Distance));
            if (_candidates.Count > 128)
            {
                _candidates.RemoveRange(128, _candidates.Count - 128);
            }

            _candidates.Sort(
                (a, b) =>
                    a.Lanes != b.Lanes
                        ? a.Lanes.CompareTo(b.Lanes)
                        : a.Distance.CompareTo(b.Distance)
            );
            _cursor = 0;
            _count = 0;
            _nextOwners.Clear();
            _vertices.Clear();
            _indices.Clear();
            _uv.Clear();
        }

        while (_cursor < _candidates.Count && Elapsed(start) < 1)
        {
            var candidate = _candidates[_cursor++];
            Entity entity = candidate.Entity;
            if (candidate.Lanes)
            {
                AddOwnedLanes(entity);
                continue;
            }

            if (
                !_manager.Exists(entity)
                || _manager.HasComponent<Deleted>(entity)
                || !_manager.HasBuffer<AreaNode>(entity)
                || !_manager.HasBuffer<AreaTriangle>(entity)
            )
            {
                continue;
            }

            var nodes = _manager.GetBuffer<AreaNode>(entity, true);
            var triangles = _manager.GetBuffer<AreaTriangle>(entity, true);
            if (
                nodes.Length < 3
                || triangles.Length == 0
                || _vertices.Count + nodes.Length > 32768
                || _indices.Count / 3 + triangles.Length > 12000
            )
            {
                continue;
            }

            var vertices = new Vector3[nodes.Length];
            for (int i = 0; i < nodes.Length; i++)
            {
                vertices[i] = (Vector3)nodes[i].m_Position + Vector3.up * .035f;
            }

            var indices = new List<int>();
            foreach (var triangle in triangles)
            {
                var t = triangle.m_Indices;
                if (
                    t.x < 0
                    || t.y < 0
                    || t.z < 0
                    || t.x >= vertices.Length
                    || t.y >= vertices.Length
                    || t.z >= vertices.Length
                )
                {
                    continue;
                }

                indices.Add(t.x);
                indices.Add(t.y);
                indices.Add(t.z);
            }

            if (indices.Count == 0)
            {
                continue;
            }

            AddPatch(new Patch(entity, vertices, indices.ToArray()));
        }

        if (_cursor >= _candidates.Count)
        {
            _mesh.Clear();
            _mesh.SetVertices(_vertices);
            _mesh.SetUVs(0, _uv);
            _mesh.SetTriangles(_indices, 0);
            _mesh.RecalculateNormals();
            _mesh.RecalculateBounds();
            var old = _owners;
            _owners = _nextOwners;
            _nextOwners = old;
            Count = _count;
            _cursor = -1;
        }

        UpdateMs = Elapsed(start);
    }

    private void AddPatch(Patch patch)
    {
        Entity owner = patch.Entity;
        for (int i = 0; i < 8 && owner != Entity.Null; i++)
        {
            if (!_nextOwners.TryGetValue(owner, out var list))
            {
                _nextOwners.Add(owner, list = new List<Patch>());
            }

            if (!list.Contains(patch))
            {
                list.Add(patch);
            }

            if (!_manager.HasComponent<Owner>(owner))
            {
                break;
            }

            Entity next = _manager.GetComponentData<Owner>(owner).m_Owner;
            if (next == owner)
            {
                break;
            }

            owner = next;
        }

        int offset = _vertices.Count;
        _vertices.AddRange(patch.Vertices);
        for (int i = 0; i < patch.Vertices.Length; i++)
        {
            _uv.Add(Vector2.zero);
        }

        foreach (int index in patch.Indices)
        {
            _indices.Add(offset + index);
        }

        _count++;
    }

    private void AddOwnedLanes(Entity entity)
    {
        if (
            !_manager.Exists(entity)
            || _manager.HasComponent<Deleted>(entity)
            || !_manager.HasBuffer<SubLane>(entity)
        )
        {
            return;
        }

        if (_nextOwners.ContainsKey(entity))
        {
            return;
        }

        var vertices = new List<Vector3>();
        var indices = new List<int>();
        foreach (var sub in _manager.GetBuffer<SubLane>(entity, true))
        {
            Entity lane = sub.m_SubLane;
            if (
                !_manager.HasComponent<Curve>(lane)
                || _manager.HasComponent<MasterLane>(lane)
                || _manager.HasComponent<Deleted>(lane)
                || (
                    !_manager.HasComponent<PedestrianLane>(lane)
                    && !_manager.HasComponent<CarLane>(lane)
                )
                || !_manager.HasComponent<PrefabRef>(lane)
            )
            {
                continue;
            }

            Entity prefab = _manager.GetComponentData<PrefabRef>(lane).m_Prefab;
            if (!_manager.HasComponent<NetLaneData>(prefab))
            {
                continue;
            }

            var data = _manager.GetComponentData<NetLaneData>(prefab);
            if ((data.m_Flags & (LaneFlags.Underground | LaneFlags.Virtual)) != 0)
            {
                continue;
            }

            float width = Mathf.Clamp(data.m_Width, .6f, 6);
            var curve = _manager.GetComponentData<Curve>(lane);
            int steps = Mathf.Clamp(Mathf.CeilToInt(curve.m_Length / 2), 2, 64);
            if (
                _indices.Count + indices.Count + steps * 6 > 36000
                || _vertices.Count + vertices.Count + (steps + 1) * 2 > 32768
            )
            {
                break;
            }

            int offset = vertices.Count;
            for (int i = 0; i <= steps; i++)
            {
                float t = i / (float)steps;
                Vector3 p = MathUtils.Position(curve.m_Bezier, t);
                Vector3 tangent = MathUtils.Tangent(curve.m_Bezier, t);
                Vector3 side = Vector3.Cross(Vector3.up, tangent).normalized * (width * .5f);
                vertices.Add(p - side + Vector3.up * .035f);
                vertices.Add(p + side + Vector3.up * .035f);
                if (i == steps)
                {
                    continue;
                }

                int a = offset + i * 2;
                indices.Add(a);
                indices.Add(a + 2);
                indices.Add(a + 1);
                indices.Add(a + 1);
                indices.Add(a + 2);
                indices.Add(a + 3);
            }
        }

        if (indices.Count > 0)
        {
            AddPatch(new Patch(entity, vertices.ToArray(), indices.ToArray()));
        }
    }

    public bool Project(Entity owner, Vector3 position, out Vector3 point)
    {
        point = default;
        float best = .8f;
        bool found = false;
        if (!_owners.TryGetValue(owner, out var patches))
        {
            return false;
        }

        foreach (var patch in patches)
        {
            if (!_manager.Exists(patch.Entity) || _manager.HasComponent<Deleted>(patch.Entity))
            {
                continue;
            }

            if (!patch.Bounds.Contains(position))
            {
                continue;
            }

            for (int i = 0; i < patch.Indices.Length; i += 3)
            {
                if (
                    FacilityGeometry.Project(
                        position,
                        patch.Vertices[patch.Indices[i]],
                        patch.Vertices[patch.Indices[i + 1]],
                        patch.Vertices[patch.Indices[i + 2]],
                        out var surface
                    )
                    && Mathf.Abs(surface.y - position.y) < best
                )
                {
                    point = surface;
                    best = Mathf.Abs(surface.y - position.y);
                    found = true;
                }
            }
        }

        return found;
    }

    private static double Elapsed(long start) =>
        (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;

    public void Dispose()
    {
        if (_scanEntities.IsCreated)
        {
            _scanEntities.Dispose();
        }

        if (_scanGeometry.IsCreated)
        {
            _scanGeometry.Dispose();
        }

        if (_scanObjects.IsCreated)
        {
            _scanObjects.Dispose();
        }

        if (_scanTransforms.IsCreated)
        {
            _scanTransforms.Dispose();
        }

        _query.Dispose();
        _objects.Dispose();
        Object.Destroy(_object);
        Object.Destroy(_mesh);
        Object.Destroy(_material);
    }

    private sealed class Patch
    {
        public readonly Entity Entity;
        public readonly Vector3[] Vertices;
        public readonly int[] Indices;
        public readonly Bounds Bounds;

        public Patch(Entity entity, Vector3[] vertices, int[] indices)
        {
            Entity = entity;
            Vertices = vertices;
            Indices = indices;
            Bounds = new Bounds(vertices[0], Vector3.zero);
            foreach (var point in vertices)
            {
                Bounds.Encapsulate(point);
            }

            Bounds.Expand(new Vector3(.02f, 1.6f, .02f));
        }
    }
}
