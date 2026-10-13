using System;
using System.Collections.Generic;
using Game.Simulation;
using RoadSnow.Core;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace RoadSnow.Mod;

internal sealed class TerrainMarks : IDisposable
{
    private readonly TerrainSystem _terrain;
    private readonly WearHistory _history;
    private readonly Material _material;
    private readonly Mesh _mesh;
    private readonly List<WearTile> _near = new();
    private readonly List<Vector3> _vertices = new();
    private readonly List<Vector2> _uv = new();
    private readonly List<int> _indices = new();
    private readonly Plane[] _planes = new Plane[6];
    private float _next;

    public TerrainMarks(TerrainSystem terrain, WearHistory history, Material source, Shader shader)
    {
        _terrain = terrain;
        _history = history;
        _material = new Material(source) { shader = shader, name = "RoadSnow terrain impressions" };
        _material.SetTexture("_BaseColorMap", Texture2D.blackTexture);
        _mesh = new Mesh
        {
            name = "RoadSnow terrain impression tiles",
            indexFormat = IndexFormat.UInt32,
        };
        _mesh.MarkDynamic();
    }

    public void Draw(Camera camera, Material source, float now)
    {
        if (camera == null)
            return;
        if (now >= _next)
        {
            _next = now + .5f;
            var terrain = _terrain.GetHeightData();
            if (!terrain.isCreated)
                return;
            _near.Clear();
            _vertices.Clear();
            _uv.Clear();
            _indices.Clear();
            GeometryUtility.CalculateFrustumPlanes(camera, _planes);
            foreach (var tile in _history.Tiles)
            {
                if (tile.Empty)
                    continue;
                Vector3 center = new(tile.CenterX, tile.CenterY, tile.CenterZ);
                if (
                    (center - camera.transform.position).sqrMagnitude > 500 * 500
                    || !GeometryUtility.TestPlanesAABB(
                        _planes,
                        new Bounds(center, new Vector3(9, 6, 9))
                    )
                )
                    continue;
                float ground = TerrainUtils.SampleHeight(ref terrain, center);
                // A road on a bridge belongs to a different height layer.
                if (Mathf.Abs(ground - tile.CenterY) > 3)
                    continue;
                _near.Add(tile);
            }
            _near.Sort((a, b) => Distance(a, camera).CompareTo(Distance(b, camera)));
            for (int n = 0; n < Math.Min(128, _near.Count); n++)
            {
                var tile = _near[n];
                int first = _vertices.Count;
                for (int z = 0; z <= 8; z++)
                for (int x = 0; x <= 8; x++)
                {
                    var p = new Vector3(tile.X * 8 + x, 0, tile.Z * 8 + z);
                    p.y = TerrainUtils.SampleHeight(ref terrain, p) + .035f;
                    _vertices.Add(p);
                    _uv.Add(Vector2.zero);
                    if (x == 8 || z == 8)
                        continue;
                    int a = first + z * 9 + x;
                    _indices.Add(a);
                    _indices.Add(a + 9);
                    _indices.Add(a + 1);
                    _indices.Add(a + 1);
                    _indices.Add(a + 9);
                    _indices.Add(a + 10);
                }
            }
            _mesh.Clear();
            _mesh.SetVertices(_vertices);
            _mesh.SetUVs(0, _uv);
            _mesh.SetTriangles(_indices, 0);
            _mesh.RecalculateNormals();
            _mesh.RecalculateBounds();
        }
        _material.SetColor("_BaseColor", source.GetColor("_BaseColor"));
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

    private static float Distance(WearTile tile, Camera camera) =>
        (
            new Vector3(tile.CenterX, tile.CenterY, tile.CenterZ) - camera.transform.position
        ).sqrMagnitude;

    public void Dispose()
    {
        Object.Destroy(_mesh);
        Object.Destroy(_material);
    }
}
