using System;
using System.Collections.Generic;
using Game.Net;
using RoadSnow.Core;
using Unity.Mathematics;
using UnityEngine;

namespace RoadSnow.Mod;

internal sealed partial class RoadPatch : IDisposable
{
    private readonly SnowAtlas.Slot _slot;
    private readonly byte[] _mask;
    private readonly Color32[] _pixels;
    private readonly Segment _segment;
    private readonly EdgeNodeGeometry? _nodeGeometry;
    private readonly float _fromT;
    private readonly float _toT;
    private readonly float _fromAcross;
    private readonly float _toAcross;
    private readonly float _height;
    private readonly SurfaceBand[] _profile;
    private readonly Vector3[] _centers;
    private readonly Vector3[] _leftPoints;
    private readonly Vector3[] _rightPoints;
    private readonly byte[] _opacity;
    private readonly float[] _grain;
    private readonly Vector3[] _vertices;
    private readonly Vector2[] _uv;
    private readonly int[] _triangles;
    private readonly bool[] _junctionRoadTriangles;
    private readonly byte[] _roadVertexMask;
    private readonly bool _hasPlowRoad;
    private readonly SectionPlowState _sectionPlow = new();
    public double PlowedAt => _sectionPlow.AtSnowfallMm;
    private readonly Bounds _bounds;
    private readonly Bounds[] _rowBounds;
    private readonly int[] _rowStarts;
    private readonly SnowTransition _transition = new();
    private readonly ResidualSnow _residual = new();
    private readonly float[] _retention;
    public readonly SnowObservation Observation = new();
    private const int Steps = 12;
    private int _uploadedRevision = -1;
    public RoadSnowField Field { get; }
    public Vector3 Center { get; }
    public float ResidualDepth => _residual.DepthMm;
    public int BankMetadataRevision { get; private set; }

    public void RestoreResidual(float depth)
    {
        _residual.Restore(depth);
        Dirty = true;
    }

    public bool NeedsSeed { get; private set; } = true;
    public bool Dirty { get; set; } = true;
    public bool Pedestrian { get; }
    public bool Detailed { get; }
    public bool HasSnow { get; private set; }
    public bool Active { get; set; } = true;
    public float LastSeen { get; set; }
    public float LastSnowTime { get; set; }
    public uint LastSimulationFrame { get; set; }
    public LaneWeathering Weathering { get; set; }
    public Vector3[] SamplePositions { get; private set; }
    public SnowBankBand[] BankBands { get; }
    public Bounds Bounds => _bounds;

    public Vector4 BankMetadata(Vector2 uv)
    {
        int x = Mathf.Clamp(Mathf.RoundToInt(uv.x * (Field.Columns - 1)), 0, Field.Columns - 1);
        int y = Mathf.Clamp(Mathf.RoundToInt(uv.y * (Field.Rows - 1)), 0, Field.Rows - 1);
        return (Color)_pixels[y * Field.Columns + x];
    }

    public CachedCoverage.Surface CacheSurface()
    {
        return new CachedCoverage.Surface
        {
            Center = Center,
            Vertices = _vertices,
            UV = _uv,
            Indices = _triangles,
            Pixels = (Color32[])_pixels.Clone(),
            RoadVertexMask = _roadVertexMask,
            PlowedAt = PlowedAt,
            Columns = Field.Columns,
            Rows = Field.Rows,
            Detailed = Detailed,
        };
    }

    public bool HasRoadBanks => !_nodeGeometry.HasValue && _profile != null && !Pedestrian;

    public SnowBankMesh BuildBankMesh(int lod, bool realistic = false)
    {
        var mesh = SnowBankMesh.Build(
            BankBands,
            Field.WidthMeters,
            Field.LengthMeters,
            lod,
            (u, t, h) => SurfacePoint(u, t, h)
        );
        if (realistic && HasRoadBanks)
            mesh.AddRoadbeds(
                _profile,
                Field.WidthMeters,
                Field.LengthMeters,
                lod,
                (u, t, h) => SurfacePoint(u, t, h)
            );
        return mesh;
    }

    public RoadPatch(
        Segment segment,
        float fromT,
        float toT,
        float fromAcross,
        float toAcross,
        float width,
        float length,
        float height,
        SnowAtlas atlas,
        int seed,
        float patternOffset,
        bool pedestrian = false,
        bool detailed = true,
        EdgeNodeGeometry? nodeGeometry = null,
        float middleAcross = 0.5f,
        SurfaceBand[] profile = null,
        float4? syncLeft = null,
        float4? syncRight = null,
        JunctionSurface.MeshData nativeJunction = null
    )
    {
        _segment = segment;
        _nodeGeometry = nodeGeometry;
        _fromT = fromT;
        _toT = toT;
        _fromAcross = fromAcross;
        _toAcross = toAcross;
        _height = height;
        _profile = profile;
        BankBands = SnowBankProfile.Build(profile, width, nodeGeometry.HasValue);
        Pedestrian = pedestrian;
        Detailed = detailed;
        _middleAcross = Mathf.Clamp(middleAcross, 0.01f, 0.99f);
        float cellSize =
            CoveragePolicy.AtlasCellSize(width, length, pedestrian, detailed) * 1.000001f;
        Field = new RoadSnowField(
            width,
            length,
            cellSize,
            new SnowSettings(
                patternSeed: seed,
                patternOffsetMeters: patternOffset,
                curbRetentionWidthMeters: pedestrian ? 0.4f : 0.7f,
                tireWidthMeters: Mathf.Max(0.5f, cellSize * 1.5f),
                tireRemovalFraction: 0.9f,
                footprintWidthMeters: Mathf.Max(0.18f, cellSize * 1.8f),
                footprintLengthMeters: Mathf.Max(0.34f, cellSize * 2.2f),
                footprintRemovalFraction: 0.95f
            ),
            detailed ? 0 : Math.Min(14, Math.Max(2, Mathf.CeilToInt(width / 0.7f))),
            detailed ? 0 : Math.Min(14, Math.Max(2, Mathf.CeilToInt(length / 1.5f)))
        );
        _mask = new byte[Field.Columns * Field.Rows];
        _pixels = new Color32[_mask.Length];
        _opacity = new byte[_mask.Length];
        _grain = new float[_mask.Length];
        _retention = new float[_mask.Length];
        SamplePositions = new Vector3[_mask.Length];
        _centers = new Vector3[Steps + 1];
        _leftPoints = new Vector3[Steps + 1];
        _rightPoints = new Vector3[Steps + 1];
        var acrossPoints = new List<(float U, float Height)>();
        if (profile != null && !nodeGeometry.HasValue)
        {
            foreach (var band in profile)
            {
                float a = (band.Left + width / 2) / width;
                float b = (band.Right + width / 2) / width;
                acrossPoints.Add((a, band.Height));
                acrossPoints.Add((b, band.Height));
            }
        }
        else
        {
            acrossPoints.Add((0, 0));
            acrossPoints.Add((0.5f, 0));
            acrossPoints.Add((1, 0));
        }

        int acrossSteps = acrossPoints.Count - 1;
        int stride = acrossSteps + 1;
        var vertices = new Vector3[nodeGeometry.HasValue ? 0 : (Steps + 1) * stride];
        var uv = new Vector2[vertices.Length];
        var triangles = new int[nodeGeometry.HasValue ? 0 : Steps * acrossSteps * 6];
        for (int i = 0; i <= Steps; i++)
        {
            float fraction = i / (float)Steps;
            _centers[i] = SurfacePoint(0.5f, fraction);
            _leftPoints[i] = SurfacePoint(0, fraction);
            _rightPoints[i] = SurfacePoint(1, fraction);
            if (nodeGeometry.HasValue)
                continue;
            for (int x = 0; x <= acrossSteps; x++)
            {
                int v = i * stride + x;
                vertices[v] = SurfacePoint(acrossPoints[x].U, fraction, acrossPoints[x].Height);
                uv[v] = new Vector2(acrossPoints[x].U, fraction);
                if (i == Steps || x == acrossSteps)
                {
                    continue;
                }

                int index = (i * acrossSteps + x) * 6;
                triangles[index] = v;
                triangles[index + 1] = v + stride;
                triangles[index + 2] = v + 1;
                triangles[index + 3] = v + 1;
                triangles[index + 4] = v + stride;
                triangles[index + 5] = v + stride + 1;
            }
        }

        if (nodeGeometry.HasValue)
        {
            var junction =
                nativeJunction
                ?? JunctionSurface.Build(
                    nodeGeometry.Value,
                    width,
                    _middleAcross,
                    profile,
                    height,
                    syncLeft,
                    syncRight
                );
            if (nativeJunction == null)
                JunctionSurface.CloseLiftedEdges(junction, height);
            vertices = junction.Vertices.ConvertAll(p => (Vector3)p).ToArray();
            triangles = junction.Indices.ToArray();
            uv = new Vector2[vertices.Length];
            _rowStarts = junction.RowStarts.ToArray();
            if (junction.RoadHeights.Count > 0 && profile != null)
            {
                float roadHeight = float.MaxValue;
                foreach (var band in profile)
                    if (!band.Pedestrian)
                        roadHeight = Math.Min(roadHeight, band.Height);
                _junctionRoadTriangles = new bool[triangles.Length / 3];
                for (int i = 0; i < junction.RoadHeights.Count; i++)
                    _junctionRoadTriangles[i] = PlowSurfaceClassification.IsRoad(
                        i < junction.RoadKinds.Count
                            ? junction.RoadKinds[i]
                            : PlowSurfaceKind.Unknown,
                        junction.RoadHeights[i],
                        roadHeight,
                        profile,
                        i < junction.RoadRanges.Count ? junction.RoadRanges[i] : null
                    );
            }
        }
        else
        {
            _rowStarts = new int[Steps + 1];
            for (int i = 0; i <= Steps; i++)
                _rowStarts[i] = i * acrossSteps * 6;
        }
        var roadFaces = new bool[triangles.Length / 3];
        float lowestRoad = float.MaxValue;
        foreach (var band in profile ?? Array.Empty<SurfaceBand>())
            if (!band.Pedestrian)
                lowestRoad = Math.Min(lowestRoad, band.Height);
        for (int i = 0; i < roadFaces.Length; i++)
        {
            if (_junctionRoadTriangles != null)
                roadFaces[i] = _junctionRoadTriangles[i];
            else if (!nodeGeometry.HasValue && profile != null)
            {
                float x =
                    (
                        uv[triangles[i * 3]].x
                        + uv[triangles[i * 3 + 1]].x
                        + uv[triangles[i * 3 + 2]].x
                    )
                        / 3
                        * width
                    - width / 2;
                foreach (var band in profile)
                    if (
                        !band.Pedestrian
                        && band.Height <= lowestRoad + .08f
                        && x > band.Left
                        && x < band.Right
                    )
                    {
                        roadFaces[i] = true;
                        break;
                    }
            }
        }
        _roadVertexMask = SectionPlowGeometry.Separate(ref vertices, ref uv, triangles, roadFaces);
        _hasPlowRoad = Array.IndexOf(_roadVertexMask, (byte)255) >= 0;
        Center = _centers[Steps / 2];
        _vertices = vertices;
        _uv = uv;
        _triangles = triangles;
        _rowBounds = new Bounds[_rowStarts.Length - 1];
        for (int row = 0; row < _rowBounds.Length; row++)
        {
            int first = _rowStarts[row],
                end = _rowStarts[row + 1];
            var bounds = new Bounds(
                first < end ? vertices[triangles[first]] : Center,
                Vector3.zero
            );
            for (int index = first; index < end; index++)
            {
                bounds.Encapsulate(vertices[triangles[index]]);
            }

            bounds.Expand(new Vector3(0.02f, 5, 0.02f));
            _rowBounds[row] = bounds;
        }

        _bounds = new Bounds(Center, Vector3.zero);
        foreach (var vertex in vertices)
        {
            _bounds.Encapsulate(vertex);
        }

        Vector3 planeMin = _bounds.min;
        Vector3 planeSize = _bounds.size;
        if (_nodeGeometry.HasValue)
        {
            for (int i = 0; i < uv.Length; i++)
            {
                float2 mapped = SurfaceGeometry.PlanarUV(vertices[i], planeMin, planeSize);
                uv[i] = new Vector2(mapped.x, mapped.y);
            }
        }

        _bounds.Expand(new Vector3(0.1f, 5, 0.1f));
        _slot = atlas.Allocate(detailed, vertices, uv, triangles, _roadVertexMask);
        for (int z = 0; z < Field.Rows; z++)
        {
            for (int x = 0; x < Field.Columns; x++)
            {
                float fraction = z / (float)Math.Max(1, Field.Rows - 1);
                float step = fraction * Steps;
                int iz = Math.Min(Steps - 1, (int)step);
                Vector3 world = SurfacePoint(x / (float)Math.Max(1, Field.Columns - 1), fraction);
                if (_nodeGeometry.HasValue)
                {
                    world.x = planeMin.x + planeSize.x * x / Math.Max(1, Field.Columns - 1);
                    world.z = planeMin.z + planeSize.z * fraction;
                    if (TryGetSurface(world, out var projected, out _))
                    {
                        world = projected;
                    }
                }

                float grain = SnowAppearance.Grain(world.x, world.z);
                int index = z * Field.Columns + x;
                _grain[index] = grain;
                SamplePositions[index] = world;
                float px = (x + 0.5f) * Field.CellWidthMeters;
                float lateral = x / (float)Math.Max(1, Field.Columns - 1) * width - width / 2;
                bool onWalk = pedestrian;
                float curbDistance = Math.Min(lateral + width / 2, width / 2 - lateral);
                if (profile != null)
                {
                    for (int b = 0; b < profile.Length; b++)
                    {
                        if (lateral >= profile[b].Left && lateral <= profile[b].Right)
                        {
                            onWalk |= profile[b].Pedestrian;
                        }

                        if (b > 0 && Math.Abs(profile[b].Height - profile[b - 1].Height) > .04f)
                        {
                            curbDistance = Math.Min(
                                curbDistance,
                                Math.Abs(lateral - profile[b].Left)
                            );
                        }
                    }
                }

                _retention[index] = onWalk
                    ? 1
                    : 1 - Mathf.SmoothStep(0, 1, Mathf.Max(0, curbDistance) / .85f);
                _opacity[index] = SnowAppearance.Alpha(
                    255,
                    grain,
                    _nodeGeometry.HasValue ? 1 : Mathf.Min(px, Field.WidthMeters - px),
                    pedestrian
                );
                Field.ScaleMeltRate(index, 0.42f + grain * 0.58f);
                byte tint = (byte)(255 * (0.87f + grain * 0.13f));
                _pixels[index] = new Color32(tint, tint, 0, 0);
            }
        }
    }

    public void Seed(float depthMm)
    {
        if (!NeedsSeed)
        {
            return;
        }

        Field.InitializeDepth(depthMm);
        NeedsSeed = false;
        Dirty = true;
    }

    public void FollowSnow(
        float targetMm,
        float elapsedSeconds,
        bool snowing = true,
        float simulatedHours = 0,
        float retentionHours = 2,
        float temperature = 0
    )
    {
        if (NeedsSeed)
        {
            Seed(targetMm);
            _transition.Initialize(targetMm);
        }

        NeedsSeed = false;
        Dirty |= Field.SynchronizeObservedDepth(targetMm, snowing);
        float previous = _residual.DepthMm;
        _residual.Advance(targetMm, simulatedHours, retentionHours, temperature, snowing);
        Dirty |= previous != _residual.DepthMm;
    }

    public bool Upload(bool visible)
    {
        if (!visible)
        {
            return false;
        }

        if (Dirty || _uploadedRevision != Field.Revision)
        {
            Field.CopyCoverageTo(_mask);
            HasSnow = false;
            bool changed = _uploadedRevision < 0;
            bool bankChanged = changed;
            for (int i = 0; i < _pixels.Length; i++)
            {
                byte coverage = SnowAppearance.Alpha(_mask[i], _grain[i], 1, false);
                byte alpha = (byte)((coverage * _opacity[i] + 127) / 255);
                byte bank = (byte)Mathf.RoundToInt(_retention[i] * 255);
                byte retained = (byte)Mathf.RoundToInt(Mathf.Clamp01(_residual.DepthMm / 80) * 255);
                bankChanged |= bank != _pixels[i].r || retained != _pixels[i].g;
                byte depth = (byte)Mathf.RoundToInt(_sectionPlow.Strength * 255);
                bankChanged |= depth != _pixels[i].b;
                changed |=
                    alpha != _pixels[i].a
                    || bank != _pixels[i].r
                    || retained != _pixels[i].g
                    || depth != _pixels[i].b;
                _pixels[i].r = bank;
                _pixels[i].g = retained;
                _pixels[i].b = depth;
                _pixels[i].a = alpha;
                HasSnow |= _pixels[i].a > 0 || bank > 0 && retained > 0;
            }

            if (changed)
            {
                _slot.Write(_pixels, Field.Columns, Field.Rows);
            }

            _uploadedRevision = Field.Revision;
            if (bankChanged)
                BankMetadataRevision++;
            Dirty = false;
            return changed;
        }

        return false;
    }

    public bool TryProject(Vector3 world, out Vector2 local, bool extendEndpoints = false)
    {
        if (TryGetSurface(world, out _, out var coordinates))
        {
            local = new Vector2(
                coordinates.x * Field.WidthMeters,
                coordinates.y * Field.LengthMeters
            );
            return true;
        }

        if (!extendEndpoints || _nodeGeometry.HasValue)
        {
            local = default;
            return false;
        }

        float best = float.MaxValue;
        local = default;
        for (int i = 0; i < Steps; i++)
        {
            Vector3 a = _centers[i];
            Vector3 b = _centers[i + 1];
            Vector3 direction = b - a;
            float denominator = direction.x * direction.x + direction.z * direction.z;
            if (denominator < 0.0001f)
            {
                continue;
            }

            float rawT =
                ((world.x - a.x) * direction.x + (world.z - a.z) * direction.z) / denominator;
            if (rawT < -0.0001f && (!extendEndpoints || i != 0))
            {
                continue;
            }

            if (rawT > 1.0001f && (!extendEndpoints || i != Steps - 1))
            {
                continue;
            }

            float t = extendEndpoints ? rawT : Mathf.Clamp01(rawT);
            Vector3 center = Vector3.LerpUnclamped(a, b, t);
            float distance = (new Vector2(world.x - center.x, world.z - center.z)).sqrMagnitude;
            if (distance >= best)
            {
                continue;
            }

            float along = (i + t) / Steps;
            Vector3 left = Vector3.LerpUnclamped(_leftPoints[i], _leftPoints[i + 1], t);
            Vector3 right = Vector3.LerpUnclamped(_rightPoints[i], _rightPoints[i + 1], t);
            Vector3 across = right - left;
            float horizontalWidth = Mathf.Sqrt(across.x * across.x + across.z * across.z);
            if (horizontalWidth < 0.1f || Math.Abs(world.y - center.y) > 1.5f)
            {
                continue;
            }

            float x =
                ((world.x - left.x) * across.x + (world.z - left.z) * across.z)
                / (horizontalWidth * horizontalWidth)
                * Field.WidthMeters;
            if (
                x < -0.25f
                || x > Field.WidthMeters + 0.25f
                || distance > Field.WidthMeters * Field.WidthMeters
            )
            {
                continue;
            }

            best = distance;
            local = new Vector2(x, along * Field.LengthMeters);
        }

        return best != float.MaxValue;
    }

    public bool TryGetSurface(Vector3 world, out Vector3 surface, out Vector2 coordinates)
    {
        surface = default;
        coordinates = default;
        if (!Contains(_bounds, world))
        {
            return false;
        }

        float nearest = 2.5f;
        bool found = false;
        for (int row = 0; row < _rowBounds.Length; row++)
        {
            if (!Contains(_rowBounds[row], world))
            {
                continue;
            }

            int end = _rowStarts[row + 1];
            for (int i = _rowStarts[row]; i < end; i += 3)
            {
                int ia = _triangles[i];
                int ib = _triangles[i + 1];
                int ic = _triangles[i + 2];
                Vector3 a = _vertices[ia];
                Vector3 b = _vertices[ib];
                Vector3 c = _vertices[ic];
                float abx = b.x - a.x;
                float abz = b.z - a.z;
                float acx = c.x - a.x;
                float acz = c.z - a.z;
                float determinant = abx * acz - abz * acx;
                if (Mathf.Abs(determinant) < 0.00001f)
                {
                    continue;
                }

                float dx = world.x - a.x;
                float dz = world.z - a.z;
                float u = (dx * acz - dz * acx) / determinant;
                float v = (abx * dz - abz * dx) / determinant;
                if (u < -0.001f || v < -0.001f || u + v > 1.001f)
                {
                    continue;
                }

                Vector3 point = a + u * (b - a) + v * (c - a);
                float distance = Mathf.Abs(point.y - world.y);
                if (distance >= nearest)
                {
                    continue;
                }

                nearest = distance;
                surface = point;
                coordinates = _uv[ia] + u * (_uv[ib] - _uv[ia]) + v * (_uv[ic] - _uv[ia]);
                found = true;
            }
        }

        return found;
    }

    private static bool Contains(Bounds bounds, Vector3 point)
    {
        // Same inclusive AABB test, avoiding a native transition per road row.
        Vector3 delta = point - bounds.center,
            extent = bounds.extents;
        return delta.x >= -extent.x
            && delta.x <= extent.x
            && delta.y >= -extent.y
            && delta.y <= extent.y
            && delta.z >= -extent.z
            && delta.z <= extent.z;
    }

    public bool StampFootprint(Vector3 world, Vector3 forward)
    {
        if (
            !HasSnow
            || !Active
            || !Detailed
            || !TryProject(world, out var local)
            || !IsPedestrian(local.x)
        )
        {
            return false;
        }

        float along = local.y / Field.LengthMeters;
        Vector3 across = SurfacePoint(1, along) - SurfacePoint(0, along);
        across.y = 0;
        across.Normalize();
        float delta = 1f / Steps;
        Vector3 tangent =
            SurfacePoint(0.5f, Mathf.Min(1, along + delta))
            - SurfacePoint(0.5f, Mathf.Max(0, along - delta));
        tangent.y = 0;
        tangent.Normalize();
        bool changed = Field.StampFootprint(
            local.x,
            local.y,
            Vector3.Dot(forward, across),
            Vector3.Dot(forward, tangent)
        );
        Dirty |= changed;
        return changed;
    }

    private readonly float _middleAcross;

    private bool IsPedestrian(float localX)
    {
        if (_profile == null)
        {
            return Pedestrian;
        }

        float x = localX - Field.WidthMeters / 2;
        foreach (var band in _profile)
        {
            if (x >= band.Left && x <= band.Right)
            {
                return band.Pedestrian;
            }
        }

        return false;
    }

    private Vector3 SurfacePoint(float across, float along, float? explicitHeight = null)
    {
        float t = Mathf.Lerp(_fromT, _toT, along);
        float u = Mathf.Lerp(_fromAcross, _toAcross, across);
        float3 position = SurfaceGeometry.Position(_segment, _nodeGeometry, _middleAcross, u, t);
        float profileHeight = explicitHeight ?? 0;
        if (!explicitHeight.HasValue && _profile != null)
        {
            float x = across * Field.WidthMeters - Field.WidthMeters / 2;
            foreach (var band in _profile)
            {
                if (x >= band.Left && x <= band.Right)
                {
                    profileHeight = band.Height;
                    break;
                }
            }
        }

        return new Vector3(position.x, position.y + _height + profileHeight, position.z);
    }

    public void Dispose()
    {
        _slot.Dispose();
    }
}
