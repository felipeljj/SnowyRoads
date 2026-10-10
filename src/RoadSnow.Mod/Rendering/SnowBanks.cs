using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace RoadSnow.Mod;

internal sealed class SnowBanks : IDisposable
{
    // Build nearby banks in stages and swap the batch pools when the next set is ready.
    // Keep the previous set visible while building to avoid flicker and frame spikes.
    private readonly Material _material;
    private readonly List<Batch>[] _pools = { new(), new() };
    private readonly Dictionary<(RoadPatch, int), SnowBankMesh> _cache = new();
    private readonly List<(RoadPatch Patch, float Distance)> _near = new();
    private readonly List<Group> _groups = new();
    private readonly Plane[] _planes = new Plane[6];
    private int _front;
    private int _scan;
    private int _build;
    private int _upload;
    private int _stage;
    private int _quality;
    private int _triangles;
    private float _next;
    private Vector3 _eye;
    public int Triangles { get; private set; }
    public int Batches { get; private set; }
    public double UpdateMs { get; private set; }

    public SnowBanks(Material source, Shader shader)
    {
        _material = new Material(source)
        {
            shader = shader,
            name = "RoadSnow nearby banks",
            renderQueue = source.renderQueue + 1,
        };
    }

    public void SetColor(Color color) => _material.SetColor("_BaseColor", color);

    public void Invalidate()
    {
        foreach (var pool in _pools)
        {
            foreach (var batch in pool)
            {
                batch.Show(false);
            }
        }

        _cache.Clear();
        _stage = 0;
        _next = 0;
        Triangles = Batches = 0;
    }

    public void Update(Camera camera, List<RoadPatch> patches, float now, bool enabled, int quality)
    {
        long start = Stopwatch.GetTimestamp();
        if (!enabled)
        {
            if (Batches > 0 || _stage != 0)
            {
                Invalidate();
            }

            return;
        }

        quality = Mathf.Clamp(quality, 1, 3);
        float range =
            quality == 1 ? 65
            : quality == 2 ? 90
            : 115;
        Vector3 eye = camera.transform.position;
        Shader.SetGlobalVector("_RoadSnowBankCamera", new Vector4(eye.x, eye.y, eye.z, range));
        if (_quality != quality)
        {
            Invalidate();
            _quality = quality;
        }

        if (_stage == 0)
        {
            if (now < _next)
            {
                return;
            }

            _eye = eye;
            GeometryUtility.CalculateFrustumPlanes(camera, _planes);
            _scan = _build = _upload = _triangles = 0;
            _near.Clear();
            _groups.Clear();
            _stage = 1;
        }

        while (_stage == 1 && _scan < patches.Count && Elapsed(start) < .65)
        {
            var patch = patches[_scan++];
            if (!patch.Active || patch.BankBands.Length == 0)
            {
                continue;
            }

            float distance = patch.Bounds.SqrDistance(_eye);
            if (distance > range * range || !GeometryUtility.TestPlanesAABB(_planes, patch.Bounds))
            {
                continue;
            }

            _near.Add((patch, distance));
            if (_near.Count > 256)
            {
                _near.Sort((a, b) => a.Distance.CompareTo(b.Distance));
                _near.RemoveRange(128, _near.Count - 128);
            }
        }

        if (_stage == 1 && _scan >= patches.Count)
        {
            _near.Sort((a, b) => a.Distance.CompareTo(b.Distance));
            _stage = 2;
        }

        int built = 0;
        while (_stage == 2 && _build < _near.Count && built++ < 2 && Elapsed(start) < .9)
        {
            var item = _near[_build++];
            var patch = item.Patch;
            if (!patch.Active)
            {
                continue;
            }

            int lod =
                item.Distance < 25 * 25 ? 0
                : item.Distance < 50 * 50 ? 1
                : 2;
            var key = (patch, lod);
            if (!_cache.TryGetValue(key, out var mesh))
            {
                if (_cache.Count >= 128)
                {
                    _cache.Clear();
                }

                _cache.Add(key, mesh = patch.BuildBankMesh(lod));
            }

            if (_triangles + mesh.TriangleCount > 6000 * quality)
            {
                continue;
            }

            var texture = patch.BankTexture;
            Group group = _groups.Find(g => g.Texture == texture);
            if (group == null)
            {
                if (_groups.Count >= 4 * quality)
                {
                    continue;
                }

                _groups.Add(group = new Group(texture));
            }

            group.Append(patch, mesh);
            _triangles += mesh.TriangleCount;
        }

        if (_stage == 2 && _build >= _near.Count)
        {
            _stage = 3;
        }

        if (_stage == 3)
        {
            var back = _pools[1 - _front];
            if (_upload < _groups.Count)
            {
                if (back.Count <= _upload)
                {
                    back.Add(new Batch(_material));
                }

                back[_upload].Upload(_groups[_upload]);
                _upload++;
            }
            else
            {
                foreach (var batch in _pools[_front])
                {
                    batch.Show(false);
                }

                for (int i = 0; i < back.Count; i++)
                {
                    back[i].Show(i < _groups.Count);
                }

                _front = 1 - _front;
                Triangles = _triangles;
                Batches = _groups.Count;
                _stage = 0;
                _next = now + 1;
            }
        }

        UpdateMs = Elapsed(start);
    }

    private static double Elapsed(long start) =>
        (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;

    public void Dispose()
    {
        foreach (var pool in _pools)
        {
            foreach (var batch in pool)
            {
                batch.Dispose();
            }
        }

        _cache.Clear();
        Object.Destroy(_material);
    }

    private sealed class Group
    {
        public readonly Texture2D Texture;
        public readonly List<Vector3> Vertices = new();
        public readonly List<Vector2> UV = new();
        public readonly List<Vector2> BaseRise = new();
        public readonly List<int> Indices = new();

        public Group(Texture2D texture) => Texture = texture;

        public void Append(RoadPatch patch, SnowBankMesh mesh)
        {
            int offset = Vertices.Count;
            Vertices.AddRange(mesh.Vertices);
            BaseRise.AddRange(mesh.BaseAndRise);
            foreach (var uv in mesh.UV)
            {
                UV.Add(patch.BankUV(uv));
            }

            foreach (int index in mesh.Triangles)
            {
                Indices.Add(offset + index);
            }
        }
    }

    private sealed class Batch : IDisposable
    {
        private readonly GameObject _object;
        private readonly Mesh _mesh;
        private readonly MeshRenderer _renderer;
        private readonly MaterialPropertyBlock _properties = new();

        public Batch(Material material)
        {
            _object = new GameObject("RoadSnow bank batch")
            {
                hideFlags = HideFlags.HideAndDontSave,
            };
            _mesh = new Mesh { name = "RoadSnow bounded banks", indexFormat = IndexFormat.UInt32 };
            _mesh.MarkDynamic();
            _object.AddComponent<MeshFilter>().sharedMesh = _mesh;
            _renderer = _object.AddComponent<MeshRenderer>();
            _renderer.sharedMaterial = material;
            _renderer.shadowCastingMode = ShadowCastingMode.Off;
            _renderer.receiveShadows = true;
            _renderer.lightProbeUsage = LightProbeUsage.BlendProbes;
            _renderer.enabled = false;
        }

        public void Upload(Group group)
        {
            _renderer.enabled = false;
            _mesh.Clear();
            _mesh.SetVertices(group.Vertices);
            _mesh.SetUVs(0, group.UV);
            _mesh.SetUVs(2, group.BaseRise);
            _mesh.SetTriangles(group.Indices, 0);
            _mesh.RecalculateNormals();
            _mesh.RecalculateBounds();
            _properties.SetTexture("_BaseColorMap", group.Texture);
            _renderer.SetPropertyBlock(_properties);
        }

        public void Show(bool visible) => _renderer.enabled = visible;

        public void Dispose()
        {
            Object.Destroy(_object);
            Object.Destroy(_mesh);
        }
    }
}
