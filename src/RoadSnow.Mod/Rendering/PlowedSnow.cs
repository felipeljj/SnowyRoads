using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace RoadSnow.Mod;

internal sealed class PlowedSnow : IDisposable
{
    internal sealed class Deposit
    {
        public Vector3 Position,
            Direction;
        public float Volume;
    }

    // Two-metre cells bound geometry growth independently of vehicle sampling frequency.
    public readonly Dictionary<Vector3Int, Deposit> Deposits = new();
    private Material _material;
    private Mesh _mesh;
    private float _nextBuild;
    private readonly List<Vector3> _vertices = new();
    private readonly List<Vector2> _uv = new();
    private readonly List<Vector2> _rise = new();
    private readonly List<int> _indices = new();
    private readonly List<Vector3Int> _remove = new();
    private readonly List<Deposit> _visible = new();

    public void Add(Vector3 position, Vector3 direction, float volume)
    {
        if (
            float.IsNaN(volume)
            || float.IsInfinity(volume)
            || volume <= 0
            || !float.IsFinite(position.sqrMagnitude)
            || !float.IsFinite(direction.sqrMagnitude)
        )
            return;
        var key = new Vector3Int(
            Mathf.FloorToInt(position.x / 2),
            Mathf.FloorToInt(position.y / 2),
            Mathf.FloorToInt(position.z / 2)
        );
        if (!Deposits.TryGetValue(key, out var deposit))
        {
            if (Deposits.Count >= 16384)
                return;
            Deposits.Add(key, deposit = new Deposit { Position = position, Direction = direction });
        }
        float total = deposit.Volume + volume;
        if (total <= 0)
            return;
        float weight = volume / total;
        deposit.Position = Vector3.Lerp(deposit.Position, position, weight);
        deposit.Direction = Vector3.Lerp(deposit.Direction, direction, weight).normalized;
        deposit.Volume = Mathf.Min(.65f, total);
    }

    public void Melt(float hours, float temperature, float retentionHours)
    {
        if (hours <= 0 || temperature <= 0)
            return;
        _remove.Clear();
        float loss = hours * .10f * Mathf.Max(1, temperature / 5) / Mathf.Max(.25f, retentionHours);
        foreach (var pair in Deposits)
        {
            pair.Value.Volume = Mathf.Max(0, pair.Value.Volume - loss);
            if (pair.Value.Volume < .0001f)
                _remove.Add(pair.Key);
        }
        foreach (var key in _remove)
            Deposits.Remove(key);
    }

    public void Draw(Camera camera, Material source, Shader shader, bool enabled, float now)
    {
        if (!enabled || shader == null)
            return;
        if (_material == null)
        {
            _material = new Material(source) { shader = shader, name = "RoadSnow plowed windrows" };
            _mesh = new Mesh { name = "RoadSnow plowed windrows" };
            _mesh.MarkDynamic();
        }
        _material.SetColor("_BaseColor", source.GetColor("_BaseColor"));
        if (now >= _nextBuild)
        {
            _nextBuild = now + .35f;
            _vertices.Clear();
            _uv.Clear();
            _rise.Clear();
            _indices.Clear();
            _visible.Clear();
            foreach (var deposit in Deposits.Values)
                if ((deposit.Position - camera.transform.position).sqrMagnitude < 260 * 260)
                    _visible.Add(deposit);
            _visible.Sort(
                (a, b) =>
                    (a.Position - camera.transform.position).sqrMagnitude.CompareTo(
                        (b.Position - camera.transform.position).sqrMagnitude
                    )
            );
            for (int n = 0; n < Math.Min(512, _visible.Count); n++)
            {
                var deposit = _visible[n];
                float fade =
                    1
                    - Mathf.SmoothStep(
                        0,
                        1,
                        Mathf.InverseLerp(
                            180,
                            260,
                            Vector3.Distance(deposit.Position, camera.transform.position)
                        )
                    );
                float height = Mathf.Min(.55f, deposit.Volume / .9f) * fade;
                Vector3 side = Vector3.Cross(Vector3.up, deposit.Direction).normalized;
                int first = _vertices.Count;
                for (int row = 0; row <= 4; row++)
                for (int col = 0; col <= 4; col++)
                {
                    float u = col / 4f,
                        t = row / 4f;
                    float rise = height * Mathf.Sin(Mathf.PI * u) * Mathf.Sin(Mathf.PI * t);
                    Vector3 p =
                        deposit.Position
                        + deposit.Direction * ((t - .5f) * 2.8f)
                        + side * ((u - .5f) * 1.1f);
                    _vertices.Add(p + Vector3.up * rise);
                    _uv.Add(new Vector2(u, t));
                    _rise.Add(new Vector2(p.y, rise));
                    if (row == 4 || col == 4)
                        continue;
                    int a = first + row * 5 + col;
                    _indices.Add(a);
                    _indices.Add(a + 5);
                    _indices.Add(a + 1);
                    _indices.Add(a + 1);
                    _indices.Add(a + 5);
                    _indices.Add(a + 6);
                }
            }
            _mesh.Clear();
            _mesh.SetVertices(_vertices);
            _mesh.SetUVs(0, _uv);
            _mesh.SetUVs(2, _rise);
            _mesh.SetTriangles(_indices, 0);
            _mesh.RecalculateNormals();
            _mesh.RecalculateBounds();
        }
        if (_mesh.vertexCount > 0)
            Graphics.DrawMesh(
                _mesh,
                Matrix4x4.identity,
                _material,
                0,
                camera,
                0,
                null,
                ShadowCastingMode.Off,
                true
            );
    }

    public void Write(BinaryWriter writer)
    {
        writer.Write(Deposits.Count);
        foreach (var d in Deposits.Values)
        {
            writer.Write(d.Position.x);
            writer.Write(d.Position.y);
            writer.Write(d.Position.z);
            writer.Write(d.Direction.x);
            writer.Write(d.Direction.y);
            writer.Write(d.Direction.z);
            writer.Write(d.Volume);
        }
    }

    public void Read(BinaryReader reader)
    {
        Deposits.Clear();
        int count = reader.ReadInt32();
        if (count < 0 || count > 16384)
            throw new InvalidDataException("Invalid plowed snow count.");
        for (int i = 0; i < count; i++)
        {
            var p = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            var d = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            Add(p, d, reader.ReadSingle());
        }
        _nextBuild = 0;
    }

    public void Dispose()
    {
        if (_material != null)
            Object.Destroy(_material);
        if (_mesh != null)
            Object.Destroy(_mesh);
        _material = null;
        _mesh = null;
        _nextBuild = 0;
    }
}
