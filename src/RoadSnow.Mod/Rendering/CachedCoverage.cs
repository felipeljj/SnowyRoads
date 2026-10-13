using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace RoadSnow.Mod;

// Exact saved coverage is displayed while simulation helpers reconnect to ECS.
// Save decoding contains no EntityManager or Unity rendering calls.
internal sealed partial class CachedCoverage : IDisposable
{
    internal sealed class Surface
    {
        public Vector3 Center;
        public Vector3[] Vertices;
        public Vector2[] UV;
        public int[] Indices;
        public Color32[] Pixels;
        public byte[] RoadVertexMask;
        public double PlowedAt = -1;
        public int Columns,
            Rows;
        public bool Detailed,
            Hidden;
        public SnowAtlas.Slot Slot;
    }

    private readonly Dictionary<Vector3, Surface> _surfaces = new();
    private SnowAtlas _atlas;
    private Material _material;
    private int _remaining;
    private bool _dirty;
    private bool _exact;
    public int Count => _surfaces.Count;
    public int Remaining => _remaining;

    public bool TrySectionPlow(Vector3 center, out double atMm)
    {
        atMm = -1;
        if (!Find(_surfaces, center, out var surface, false) || surface.PlowedAt < 0)
            return false;
        atMm = surface.PlowedAt;
        return true;
    }

    public void DiscardLegacyPlowChannel()
    {
        foreach (var surface in _surfaces.Values)
            for (int i = 0; i < surface.Pixels.Length; i++)
                surface.Pixels[i].b = 0;
    }

    public void ResetSectionPlows()
    {
        foreach (var surface in _surfaces.Values)
            if (surface.PlowedAt >= 0)
            {
                surface.PlowedAt = -1;
                for (int i = 0; i < surface.Pixels.Length; i++)
                    surface.Pixels[i].b = 0;
                if (!surface.Hidden && surface.Slot != null)
                {
                    var pixels = (Color32[])surface.Pixels.Clone();
                    for (int i = 0; i < pixels.Length; i++)
                        pixels[i].a = 255;
                    surface.Slot.Write(pixels, surface.Columns, surface.Rows);
                    _dirty = true;
                }
            }
        _lastPlowWeather = -1;
    }

    private int _lastPlowWeather = -1;

    public void ObservePlowSnowfall(double totalMm)
    {
        int weather = (int)Math.Min(int.MaxValue, Math.Floor(totalMm * 17));
        if (_lastPlowWeather == weather)
            return;
        _lastPlowWeather = weather;
        var state = new RoadSnow.Core.SectionPlowState();
        foreach (var surface in _surfaces.Values)
        {
            if (surface.Hidden || surface.PlowedAt < 0)
                continue;
            state.Restore(surface.PlowedAt, totalMm);
            byte strength = (byte)Mathf.RoundToInt(state.Strength * 255);
            bool changed = false;
            for (int i = 0; i < surface.Pixels.Length; i++)
            {
                changed |= surface.Pixels[i].b != strength;
                surface.Pixels[i].b = strength;
            }
            if (changed && surface.Slot != null)
            {
                var pixels = (Color32[])surface.Pixels.Clone();
                for (int i = 0; i < pixels.Length; i++)
                    pixels[i].a = 255;
                surface.Slot.Write(pixels, surface.Columns, surface.Rows);
                _dirty = true;
            }
        }
    }

    // Identity only: the saved geometry stays at its original precision. Mono
    // and repeated profile arithmetic can differ by a few float ULPs at world scale.
    internal static Vector3 Key(Vector3 center) =>
        new(
            Mathf.Round(center.x * 1000) / 1000,
            Mathf.Round(center.y * 1000) / 1000,
            Mathf.Round(center.z * 1000) / 1000
        );

    public bool CoversAll(HashSet<Vector3> required)
    {
        if (!_exact || required.Count == 0)
            return false;
        foreach (var center in required)
            if (!Find(_surfaces, center, out _))
                return false;
        return true;
    }

    public void Hide(Vector3 center)
    {
        if (!Find(_surfaces, center, out var surface))
            return;
        surface.Hidden = true;
        _remaining--;
        if (surface.Slot != null)
        {
            var pixels = (Color32[])surface.Pixels.Clone();
            for (int i = 0; i < pixels.Length; i++)
                pixels[i].a = 0;
            surface.Slot.Write(pixels, surface.Columns, surface.Rows);
        }
        _dirty = true;
    }

    public void Retain(IEnumerable<Surface> current)
    {
        var merged = Merge(current);
        Dispose();
        _surfaces.Clear();
        foreach (var surface in merged.Values)
        {
            surface.Hidden = false;
            surface.Slot = null;
            _surfaces[Key(surface.Center)] = surface;
        }
        _remaining = _surfaces.Count;
    }

    public void Clear()
    {
        // SetDefaults/Deserialize cannot call Unity object destruction.
        _surfaces.Clear();
        _remaining = 0;
        _exact = false;
        _lastPlowWeather = -1;
    }

    public void RetireUnseen(HashSet<Vector3> current)
    {
        foreach (var surface in _surfaces.Values)
            if (!ContainsNear(current, surface.Center))
                Hide(surface.Center);
    }

    // Neighbour cells cover tiny differences crossing a rounding boundary.
    // This runs only on a miss; ordinary identity lookups remain O(1).
    private static bool Find(
        Dictionary<Vector3, Surface> surfaces,
        Vector3 center,
        out Surface surface,
        bool visibleOnly = true
    )
    {
        var key = Key(center);
        if (surfaces.TryGetValue(key, out surface) && (!visibleOnly || !surface.Hidden))
            return true;
        int x = Mathf.RoundToInt(key.x * 1000),
            y = Mathf.RoundToInt(key.y * 1000),
            z = Mathf.RoundToInt(key.z * 1000);
        for (int dz = -1; dz <= 1; dz++)
        for (int dy = -1; dy <= 1; dy++)
        for (int dx = -1; dx <= 1; dx++)
            if (
                surfaces.TryGetValue(
                    new Vector3((x + dx) / 1000f, (y + dy) / 1000f, (z + dz) / 1000f),
                    out surface
                ) && (!visibleOnly || !surface.Hidden)
            )
                return true;
        surface = null;
        return false;
    }

    private static bool ContainsNear(HashSet<Vector3> centers, Vector3 center)
    {
        var key = Key(center);
        if (centers.Contains(key))
            return true;
        int x = Mathf.RoundToInt(key.x * 1000),
            y = Mathf.RoundToInt(key.y * 1000),
            z = Mathf.RoundToInt(key.z * 1000);
        for (int dz = -1; dz <= 1; dz++)
        for (int dy = -1; dy <= 1; dy++)
        for (int dx = -1; dx <= 1; dx++)
            if (centers.Contains(new Vector3((x + dx) / 1000f, (y + dy) / 1000f, (z + dz) / 1000f)))
                return true;
        return false;
    }

    public void Prepare(Material source, Shader shader)
    {
        if (_remaining == 0 || _atlas != null || shader == null || source == null)
            return;
        _material = new Material(source) { shader = shader, name = "RoadSnow saved coverage" };
        _atlas = new SnowAtlas(_material);
        foreach (var surface in _surfaces.Values)
        {
            if (surface.Hidden)
                continue;
            surface.Slot = _atlas.Allocate(
                surface.Detailed,
                surface.Vertices,
                surface.UV,
                surface.Indices,
                surface.RoadVertexMask
            );
            var pixels = (Color32[])surface.Pixels.Clone();
            // CachedSurface alpha is a visibility switch. Preserve RGB metadata.
            for (int i = 0; i < pixels.Length; i++)
                pixels[i].a = 255;
            surface.Slot.Write(pixels, surface.Columns, surface.Rows);
        }
        _atlas.Flush();
        _dirty = false;
    }

    public void SetVisible(bool visible) => _atlas?.SetVisible(visible);

    public void Draw(Camera camera, Material source, Shader shader)
    {
        if (camera == null || source == null)
            return;
        if (_remaining == 0)
        {
            if (_atlas != null)
                Dispose();
            return;
        }
        Prepare(source, shader);
        if (_material == null)
            return;
        _material.SetColor("_BaseColor", source.GetColor("_BaseColor"));
        _atlas.SetVisible(true);
        if (_dirty)
        {
            _atlas.Flush();
            _dirty = false;
        }
    }

    private Dictionary<Vector3, Surface> Merge(IEnumerable<Surface> current)
    {
        var surfaces = new Dictionary<Vector3, Surface>();
        foreach (var surface in current)
            surfaces[Key(surface.Center)] = surface;
        foreach (var saved in _surfaces.Values)
            if (!saved.Hidden && !surfaces.ContainsKey(Key(saved.Center)))
                surfaces[Key(saved.Center)] = saved;
        return surfaces;
    }

    public void Dispose()
    {
        _atlas?.Dispose();
        _atlas = null;
        if (_material != null)
            Object.Destroy(_material);
        _material = null;
        foreach (var surface in _surfaces.Values)
            surface.Slot = null;
    }
}
