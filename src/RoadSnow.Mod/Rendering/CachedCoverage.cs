using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace RoadSnow.Mod;

internal sealed class CachedCoverage : IDisposable
{
    private const int MaximumSurfaceVertices = 16384;
    private const int MaximumSurfaceIndices = 98304;

    internal sealed class Surface
    {
        public Vector3 Center;
        public Vector3[] Vertices;
        public int[] Indices;
        public int Slot;
        public bool Hidden;
    }

    private readonly Dictionary<Vector3, Surface> _surfaces = new();
    private Material _material;
    private Mesh _mesh;
    private Texture2D _visibility;
    private Color32[] _pixels;
    private bool _dirty;
    private int _remaining;

    public void Hide(Vector3 center)
    {
        if (!_surfaces.TryGetValue(center, out var surface) || surface.Hidden)
            return;
        surface.Hidden = true;
        _remaining--;
        if (_pixels != null)
            _pixels[surface.Slot] = new Color32(0, 0, 0, 0);
        _dirty = true;
    }

    public void Clear()
    {
        _surfaces.Clear();
        _remaining = 0;
    }

    public void Draw(Camera camera, Material source, Shader shader)
    {
        if (_remaining == 0 || shader == null || camera == null || source == null)
            return;
        if (_mesh == null)
        {
            int side = Mathf.NextPowerOfTwo(Mathf.CeilToInt(Mathf.Sqrt(_surfaces.Count)));
            _pixels = new Color32[side * side];
            var vertices = new List<Vector3>();
            var uv = new List<Vector2>();
            var indices = new List<int>();
            int slot = 0;
            foreach (var surface in _surfaces.Values)
            {
                surface.Slot = slot;
                _pixels[slot] = new Color32(0, 0, 0, surface.Hidden ? (byte)0 : (byte)255);
                var coordinate = new Vector2(
                    (slot % side + .5f) / side,
                    (slot / side + .5f) / side
                );
                int first = vertices.Count;
                vertices.AddRange(surface.Vertices);
                foreach (var vertex in surface.Vertices)
                    uv.Add(coordinate);
                foreach (int index in surface.Indices)
                    indices.Add(first + index);
                slot++;
            }
            _visibility = new Texture2D(side, side, TextureFormat.RGBA32, false, true)
            {
                filterMode = FilterMode.Point,
            };
            _mesh = new Mesh { name = "RoadSnow saved coverage", indexFormat = IndexFormat.UInt32 };
            _mesh.SetVertices(vertices);
            _mesh.SetUVs(0, uv);
            _mesh.SetTriangles(indices, 0);
            _mesh.RecalculateNormals();
            _mesh.RecalculateBounds();
            _material = new Material(source)
            {
                shader = shader,
                name = "RoadSnow loading coverage",
            };
            _material.SetTexture("_BaseColorMap", _visibility);
            _dirty = true;
        }
        if (_dirty)
        {
            _visibility.SetPixels32(_pixels);
            _visibility.Apply(false, false);
            _dirty = false;
        }
        _material.SetColor("_BaseColor", source.GetColor("_BaseColor"));
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

    public void Write(BinaryWriter writer, IEnumerable<Surface> current)
    {
        var surfaces = new Dictionary<Vector3, Surface>();
        foreach (var saved in _surfaces.Values)
            if (!saved.Hidden)
                surfaces[saved.Center] = saved;
        foreach (var surface in current)
            surfaces[surface.Center] = surface;
        var bounded = new List<Surface>();
        int vertices = 0;
        foreach (var surface in surfaces.Values)
        {
            if (
                surface.Vertices.Length > MaximumSurfaceVertices
                || surface.Indices.Length > MaximumSurfaceIndices
            )
                continue;
            if (bounded.Count >= 100000 || vertices + surface.Vertices.Length > 2000000)
                break;
            vertices += surface.Vertices.Length;
            bounded.Add(surface);
        }
        writer.Write(bounded.Count);
        foreach (var surface in bounded)
        {
            WritePoint(writer, surface.Center);
            writer.Write(surface.Vertices.Length);
            foreach (var point in surface.Vertices)
                WritePoint(writer, point);
            writer.Write(surface.Indices.Length);
            foreach (int index in surface.Indices)
                writer.Write(index);
        }
    }

    public void Read(BinaryReader reader)
    {
        Clear();
        int count = Count(reader, 100000),
            total = 0;
        for (int n = 0; n < count; n++)
        {
            var surface = new Surface { Center = ReadPoint(reader) };
            int vertices = Count(reader, MaximumSurfaceVertices);
            total += vertices;
            if (total > 2000000)
                throw new InvalidDataException("Snow coverage cache too large.");
            surface.Vertices = new Vector3[vertices];
            for (int i = 0; i < vertices; i++)
                surface.Vertices[i] = ReadPoint(reader);
            int indices = Count(reader, MaximumSurfaceIndices);
            if (indices % 3 != 0)
                throw new InvalidDataException("Invalid snow triangles.");
            surface.Indices = new int[indices];
            for (int i = 0; i < indices; i++)
            {
                int index = reader.ReadInt32();
                if (index < 0 || index >= vertices)
                    throw new InvalidDataException("Invalid snow vertex.");
                surface.Indices[i] = index;
            }
            _surfaces[surface.Center] = surface;
        }
        _remaining = _surfaces.Count;
    }

    private static int Count(BinaryReader reader, int maximum)
    {
        int count = reader.ReadInt32();
        if (count < 0 || count > maximum)
            throw new InvalidDataException("Invalid snow coverage count.");
        return count;
    }

    private static void WritePoint(BinaryWriter writer, Vector3 p)
    {
        writer.Write(p.x);
        writer.Write(p.y);
        writer.Write(p.z);
    }

    private static Vector3 ReadPoint(BinaryReader reader)
    {
        var p = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        if (!float.IsFinite(p.sqrMagnitude))
            throw new InvalidDataException("Invalid snow coverage position.");
        return p;
    }

    public void Dispose()
    {
        if (_mesh != null)
            Object.Destroy(_mesh);
        if (_material != null)
            Object.Destroy(_material);
        if (_visibility != null)
            Object.Destroy(_visibility);
        _mesh = null;
        _material = null;
        _visibility = null;
        _pixels = null;
    }
}
