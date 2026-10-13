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
    private readonly List<WearTile> _nearby = new();
    private readonly TerrainMeshData _geometry = new();
    private Vector3 _eye;
    private readonly Plane[] _planes = new Plane[6];
    private float _next;
    public int GeometryBuilds { get; private set; }
    public int GeometryReuses { get; private set; }

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

    public void Draw(
        Camera camera,
        Material source,
        float now,
        bool prepare = true,
        bool submit = true
    )
    {
        if (camera == null)
            return;
        if (prepare && now >= _next)
        {
            _next = now + .5f;
            var terrain = _terrain.GetHeightData();
            if (!terrain.isCreated)
                return;
            _near.Clear();
            _eye = camera.transform.position;
            _history.QueryTiles(
                Mathf.FloorToInt((_eye.x - 504) / 8),
                Mathf.FloorToInt((_eye.z - 504) / 8),
                Mathf.FloorToInt((_eye.x + 500) / 8),
                Mathf.FloorToInt((_eye.z + 500) / 8),
                _nearby
            );
            GeometryUtility.CalculateFrustumPlanes(camera, _planes);
            foreach (var tile in _nearby)
            {
                if (tile.Empty)
                    continue;
                Vector3 center = new(tile.CenterX, tile.CenterY, tile.CenterZ);
                if (
                    (center - _eye).sqrMagnitude > 500 * 500
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
            _near.Sort(CompareDistance);
            _geometry.Vertices.Begin();
            for (int n = 0; n < Math.Min(128, _near.Count); n++)
            {
                var tile = _near[n];
                for (int z = 0; z <= 8; z++)
                for (int x = 0; x <= 8; x++)
                {
                    var p = new Vector3(tile.X * 8 + x, 0, tile.Z * 8 + z);
                    p.y = TerrainUtils.SampleHeight(ref terrain, p) + .035f;
                    _geometry.Vertices.Add(p);
                }
            }
            if (_geometry.End(out bool topologyChanged))
            {
                if (topologyChanged)
                    _mesh.Clear();
                _mesh.SetVertices(_geometry.Vertices.Items);
                if (topologyChanged)
                {
                    _mesh.SetUVs(0, _geometry.UV);
                    _mesh.SetTriangles(_geometry.Indices, 0);
                }
                _mesh.RecalculateNormals();
                _mesh.RecalculateBounds();
                GeometryBuilds++;
            }
            else
                GeometryReuses++;
        }
        _material.SetColor("_BaseColor", source.GetColor("_BaseColor"));
        if (submit && _mesh.vertexCount > 0)
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

    private int CompareDistance(WearTile a, WearTile b) => Distance(a).CompareTo(Distance(b));

    private float Distance(WearTile tile) =>
        (new Vector3(tile.CenterX, tile.CenterY, tile.CenterZ) - _eye).sqrMagnitude;

    public void Dispose()
    {
        Object.Destroy(_mesh);
        Object.Destroy(_material);
    }
}
