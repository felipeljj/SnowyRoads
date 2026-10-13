using System;
using System.Collections.Generic;
using System.Diagnostics;
using RoadSnow.Core;
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
    private readonly LruCache<(RoadPatch, int), SnowBankMesh> _cache = new(768);
    private readonly Dictionary<RoadPatch, int> _lods = new();
    private readonly List<(RoadPatch Patch, float Distance)> _near = new();
    private readonly List<Group> _groups = new();
    private readonly ExactSequence<BankInput> _inputs = new();
    private bool _inputsReady;
    private int _assemble;
    public long GroupReuses { get; private set; }

    private readonly struct BankInput : IEquatable<BankInput>
    {
        public readonly RoadPatch Patch;
        public readonly SnowBankMesh Mesh;
        private readonly int _metadata;

        public BankInput(RoadPatch patch, SnowBankMesh mesh)
        {
            Patch = patch;
            Mesh = mesh;
            _metadata = patch.BankMetadataRevision;
        }

        public bool Equals(BankInput other) =>
            ReferenceEquals(Patch, other.Patch)
            && ReferenceEquals(Mesh, other.Mesh)
            && _metadata == other._metadata;
    }

    private int _front;
    private int _scan;
    private int _build;
    private int _upload;
    private int _stage;
    private int _quality;
    private bool _realistic;
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
        _inputsReady = false;
        _lods.Clear();
        _stage = 0;
        _next = 0;
        Triangles = Batches = 0;
    }

    public void Retire(RoadPatch patch)
    {
        // A road changing elsewhere must not blank every visible snow bank.
        for (int lod = 0; lod < 3; lod++)
            _cache.Remove((patch, lod));
        _lods.Remove(patch);
        _next = 0;
    }

    public void Update(
        Camera camera,
        List<RoadPatch> patches,
        float now,
        bool enabled,
        int quality,
        Vector3? pivot = null,
        bool realistic = false
    )
    {
        long start = Stopwatch.GetTimestamp();
        quality = Mathf.Clamp(quality, 1, 3);
        float range =
            quality == 1 ? 240
            : quality == 2 ? 360
            : 480;
        Vector3 eye = camera.transform.position;
        var focus = pivot ?? eye;
        var offset = Vector2.ClampMagnitude(
            new Vector2(focus.x - eye.x, focus.z - eye.z),
            range * .35f
        );
        eye.x += offset.x;
        eye.z += offset.y;
        // Impression shading also uses this position when raised banks are off.
        Shader.SetGlobalVector("_RoadSnowBankCamera", new Vector4(eye.x, eye.y, eye.z, range));
        if (!enabled)
        {
            if (Batches > 0 || _stage != 0)
            {
                Invalidate();
            }

            return;
        }

        if (_quality != quality || _realistic != realistic)
        {
            Invalidate();
            _quality = quality;
            _realistic = realistic;
        }

        if (_stage == 0)
        {
            if (now < _next)
            {
                return;
            }

            _eye = eye;
            _scan = _build = _upload = _triangles = 0;
            _near.Clear();
            _groups.Clear();
            _inputs.Begin();
            _assemble = 0;
            _stage = 1;
        }

        while (_stage == 1 && _scan < patches.Count && Elapsed(start) < .3)
        {
            var patch = patches[_scan++];
            if (!patch.Active || patch.BankBands.Length == 0 && !(realistic && patch.HasRoadBanks))
            {
                continue;
            }

            // Select around the ground pivot. Camera height/rotation must not erase
            // the same curb while the player inspects it. Renderer bounds handle culling.
            var ground = new Vector3(_eye.x, patch.Bounds.center.y, _eye.z);
            float distance = patch.Bounds.SqrDistance(ground);
            if (distance > range * range)
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
        while (_stage == 2 && _build < _near.Count && built++ < 2 && Elapsed(start) < .4)
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
            if (_lods.TryGetValue(patch, out int previous))
            {
                if (previous == 0 && item.Distance < 30 * 30)
                    lod = 0;
                if (previous == 1 && item.Distance > 20 * 20 && item.Distance < 60 * 60)
                    lod = 1;
                if (previous == 2 && item.Distance > 40 * 40)
                    lod = 2;
            }
            var key = (patch, lod);
            if (!_cache.TryGetValue(key, out var mesh))
            {
                _cache.Set(key, mesh = patch.BuildBankMesh(lod, realistic));
            }

            int budget = (realistic ? 24000 : 12000) * quality;
            // A wide, close road should become less detailed before its banks
            // disappear altogether when the shared triangle budget is full.
            while (lod < 2 && _triangles + mesh.TriangleCount > budget)
            {
                key = (patch, ++lod);
                if (!_cache.TryGetValue(key, out mesh))
                    _cache.Set(key, mesh = patch.BuildBankMesh(lod, realistic));
            }
            _lods[patch] = lod;
            if (_triangles + mesh.TriangleCount > budget)
            {
                continue;
            }

            // Per-vertex retention removes atlas ownership from this renderer.
            // A texture-group cap used to drop valid banks when the camera moved.
            _inputs.Add(new BankInput(patch, mesh));
            _triangles += mesh.TriangleCount;
        }

        if (_stage == 2 && _build >= _near.Count)
        {
            bool changed = _inputs.End();
            if (_inputsReady && !changed)
            {
                GroupReuses++;
                _stage = 0;
                _next = now + 1;
                UpdateMs = Elapsed(start);
                return;
            }
            _inputsReady = true;
            _stage = 3;
        }

        if (_stage == 3)
        {
            while (_assemble < _inputs.Items.Count && Elapsed(start) < .4)
            {
                var input = _inputs.Items[_assemble++];
                if (_groups.Count == 0)
                    _groups.Add(new Group());
                _groups[0].Append(input.Patch, input.Mesh);
            }
            if (_assemble < _inputs.Items.Count)
            {
                UpdateMs = Elapsed(start);
                return;
            }
            _stage = 4;
        }
        if (_stage == 4)
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
        public readonly List<Vector2> Metadata = new();
        public readonly List<Vector3> Vertices = new();
        public readonly List<Vector2> UV = new();
        public readonly List<Vector2> BaseRise = new();
        public readonly List<int> Indices = new();

        public void Append(RoadPatch patch, SnowBankMesh mesh)
        {
            int offset = Vertices.Count;
            Vertices.AddRange(mesh.Vertices);
            BaseRise.AddRange(mesh.BaseAndRise);
            for (int i = 0; i < mesh.UV.Count; i++)
            {
                var uv = mesh.UV[i];
                UV.Add(uv);
                var metadata = patch.BankMetadata(uv);
                metadata.w = mesh.Interior[i] ? -1 : 1;
                Metadata.Add(
                    new Vector2(metadata.x * metadata.y, mesh.Interior[i] ? -1 - metadata.z : 1)
                );
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
        private Group _uploaded;

        private static bool Same<T>(List<T> a, List<T> b)
            where T : IEquatable<T>
        {
            if (a.Count != b.Count)
                return false;
            for (int i = 0; i < a.Count; i++)
                if (!a[i].Equals(b[i]))
                    return false;
            return true;
        }

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
            // Groups are immutable after submission. Compare exact values, including
            // weather metadata, so reuse never freezes melting or changed retention.
            bool sameGeometry =
                _uploaded != null
                && Same(_uploaded.Vertices, group.Vertices)
                && Same(_uploaded.UV, group.UV)
                && Same(_uploaded.BaseRise, group.BaseRise)
                && Same(_uploaded.Indices, group.Indices);
            if (sameGeometry)
            {
                if (!Same(_uploaded.Metadata, group.Metadata))
                    _mesh.SetUVs(3, group.Metadata);
                _uploaded = group;
                return;
            }
            _uploaded = group;
            _renderer.enabled = false;
            _mesh.Clear();
            _mesh.SetVertices(group.Vertices);
            _mesh.SetUVs(0, group.UV);
            _mesh.SetUVs(2, group.BaseRise);
            _mesh.SetUVs(3, group.Metadata);
            _mesh.SetTriangles(group.Indices, 0);
            _mesh.RecalculateNormals();
            _mesh.RecalculateBounds();
            _properties.SetTexture("_BaseColorMap", Texture2D.whiteTexture);
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
