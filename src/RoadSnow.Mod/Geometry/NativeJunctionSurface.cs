using System;
using System.Collections.Generic;
using Colossal.Mathematics;
using Game.Net;
using Game.Prefabs;
using Game.Rendering;
using Game.Simulation;
using Unity.Entities;
using Unity.Mathematics;

namespace RoadSnow.Mod;

// Keep the pavement's own triangulation. Reconstructing each strip independently
// loses median fans, curb transitions and height changes inside native pieces.
internal sealed class NativeJunctionSurface
{
    private readonly Dictionary<Entity, Template> _templates = new();
    private readonly Dictionary<Entity, bool> _availability = new();
    public int Built { get; private set; }
    public int Unavailable { get; private set; }

    public void Clear()
    {
        _templates.Clear();
        _availability.Clear();
        Built = Unavailable = 0;
    }

    public void BeginRefresh() => _availability.Clear();

    // Check once per composition per discovery pass, not once per road/frame.
    // A temporary fallback must be replaced after native mesh streaming finishes.
    public bool IsReady(EntityManager manager, Entity composition)
    {
        if (_templates.ContainsKey(composition))
            return true;
        if (_availability.TryGetValue(composition, out bool ready))
            return ready;
        ready = composition != Entity.Null && manager.HasBuffer<NetCompositionPiece>(composition);
        bool found = false;
        if (ready)
        {
            foreach (var piece in manager.GetBuffer<NetCompositionPiece>(composition, true))
            {
                if (!IsSurface(piece))
                    continue;
                found = true;
                if (
                    !manager.HasBuffer<MeshVertex>(piece.m_Piece)
                    || !manager.HasBuffer<MeshIndex>(piece.m_Piece)
                )
                {
                    ready = false;
                    break;
                }
            }
        }
        return _availability[composition] = ready && found;
    }

    public JunctionSurface.MeshData Build(
        EntityManager manager,
        Entity composition,
        NetCompositionData data,
        EdgeNodeGeometry node,
        float lift,
        TerrainHeightData terrain = default
    )
    {
        if (!_templates.TryGetValue(composition, out var template))
        {
            template = Read(manager, composition, data);
            if (template == null)
            {
                Unavailable++;
                return null;
            }
            _templates[composition] = template;
        }
        BatchDataHelpers.CalculateNodeParameters(node, data, out var parameters);
        var matrices = new[]
        {
            parameters.m_CompositionMatrix0,
            parameters.m_CompositionMatrix1,
            parameters.m_CompositionMatrix2,
            parameters.m_CompositionMatrix3,
            parameters.m_CompositionMatrix4,
            parameters.m_CompositionMatrix5,
            parameters.m_CompositionMatrix6,
            parameters.m_CompositionMatrix7,
        };
        float3 origin = MathUtils.Center(node.m_Bounds);
        var vertices = new float3[template.Vertices.Count];
        for (int i = 0; i < vertices.Length; i++)
        {
            var v = template.Vertices[i];
            var a = Curve(matrices[v.A]);
            var b = Curve(matrices[v.B]);
            float u = v.U;
            if (v.Sync != 0)
            {
                var source =
                    v.Sync == 2 ? data.m_SyncVertexOffsetsLeft : data.m_SyncVertexOffsetsRight;
                var target =
                    v.Sync == 2 ? node.m_SyncVertexTargetsLeft : node.m_SyncVertexTargetsRight;
                u = JunctionSurface.Synchronize(
                    u,
                    v.T,
                    source,
                    target,
                    math.distance(a.a.xz, b.a.xz),
                    math.distance(a.d.xz, b.d.xz),
                    v.Final
                );
            }
            float3 pa = MathUtils.Position(a, v.T) + origin;
            float3 pb = MathUtils.Position(b, v.T) + origin;
            float3 p = math.lerp(pa, pb, u);
            p.y += v.Height;
            if (v.TerrainMode != 0 && terrain.isCreated)
            {
                float3 boundary = u >= .5f ? pb : pa;
                boundary.y += v.Height;
                p.y = NativeTerrainConformance.Height(
                    p.y,
                    boundary.y,
                    v.Height,
                    TerrainUtils.SampleHeight(ref terrain, boundary),
                    v.TerrainMode
                );
            }
            p.y += lift;
            vertices[i] = p;
        }
        var mesh = new JunctionSurface.MeshData();
        mesh.RowStarts.Add(0);
        for (int i = 0; i < template.Indices.Count; i += 3)
        {
            int firstTriangle = mesh.Indices.Count / 3;
            float3 a = vertices[template.Indices[i]],
                b = vertices[template.Indices[i + 1]],
                c = vertices[template.Indices[i + 2]];
            if (template.Walls[i / 3])
                JunctionSurface.EmitWall(mesh, a, b, c);
            else
                JunctionSurface.Emit(mesh, a, b, c);
            float roadHeight = template.Walls[i / 3]
                ? float.NaN
                : math.max(
                    template.Vertices[template.Indices[i]].Height,
                    math.max(
                        template.Vertices[template.Indices[i + 1]].Height,
                        template.Vertices[template.Indices[i + 2]].Height
                    )
                );
            for (int triangle = firstTriangle; triangle < mesh.Indices.Count / 3; triangle++)
            {
                mesh.RoadHeights.Add(roadHeight);
                mesh.RoadKinds.Add(template.Kinds[i / 3]);
                float ax = template.Vertices[template.Indices[i]].Lateral;
                float bx = template.Vertices[template.Indices[i + 1]].Lateral;
                float cx = template.Vertices[template.Indices[i + 2]].Lateral;
                mesh.RoadRanges.Add(
                    new float2(math.min(ax, math.min(bx, cx)), math.max(ax, math.max(bx, cx)))
                );
            }
            // Small groups keep point-to-surface queries bounded by local AABBs.
            if ((i + 3) % 96 == 0)
                mesh.RowStarts.Add(mesh.Indices.Count);
        }
        if (mesh.RowStarts[mesh.RowStarts.Count - 1] != mesh.Indices.Count)
            mesh.RowStarts.Add(mesh.Indices.Count);
        if (mesh.Indices.Count == 0)
            return null;
        JunctionSurface.CloseLiftedEdges(mesh, lift);
        Built++;
        return mesh;
    }

    private static Bezier4x3 Curve(float4x4 m) => new(m.c0.xyz, m.c1.xyz, m.c2.xyz, m.c3.xyz);

    private static bool IsSurface(NetCompositionPiece piece) =>
        (piece.m_PieceFlags & NetPieceFlags.Surface) != 0
        && (
            piece.m_SectionFlags
            & (
                NetSectionFlags.Underground
                | NetSectionFlags.Overhead
                | NetSectionFlags.HiddenSurface
            )
        ) == 0;

    private static Template Read(EntityManager manager, Entity composition, NetCompositionData data)
    {
        if (composition == Entity.Null || !manager.HasBuffer<NetCompositionPiece>(composition))
            return null;
        var result = new Template();
        bool round = (data.m_Flags.m_General & CompositionFlags.General.Roundabout) != 0;
        foreach (var piece in manager.GetBuffer<NetCompositionPiece>(composition, true))
        {
            if (!IsSurface(piece))
                continue;
            if (
                !manager.HasBuffer<MeshVertex>(piece.m_Piece)
                || !manager.HasBuffer<MeshIndex>(piece.m_Piece)
            )
                return null; // Streaming has not provided a complete template yet.
            var source = manager.GetBuffer<MeshVertex>(piece.m_Piece, true);
            var indices = manager.GetBuffer<MeshIndex>(piece.m_Piece, true);
            bool hasNormals = manager.HasBuffer<MeshNormal>(piece.m_Piece);
            var normals = hasNormals ? manager.GetBuffer<MeshNormal>(piece.m_Piece, true) : default;
            bool invert = (piece.m_SectionFlags & NetSectionFlags.Invert) != 0;
            bool flip = (piece.m_SectionFlags & NetSectionFlags.FlipMesh) != 0;
            bool half = (piece.m_SectionFlags & NetSectionFlags.HalfLength) != 0;
            bool preserve = (piece.m_PieceFlags & NetPieceFlags.PreserveShape) != 0;
            bool median = (piece.m_SectionFlags & NetSectionFlags.Median) != 0;
            bool right = (piece.m_SectionFlags & NetSectionFlags.Right) != 0;
            bool skipBottom = (piece.m_PieceFlags & NetPieceFlags.SkipBottomHalf) != 0;
            bool lower = (piece.m_PieceFlags & NetPieceFlags.LowerBottomToTerrain) != 0;
            bool raise = (piece.m_PieceFlags & NetPieceFlags.RaiseTopToTerrain) != 0;
            float2[] heightBounds = null;
            if (lower || raise)
            {
                heightBounds = new float2[257];
                for (int i = 0; i < heightBounds.Length; i++)
                    heightBounds[i] = new float2(float.MaxValue, float.MinValue);
                for (int i = 0; i + 2 < indices.Length; i += 3)
                {
                    float3 a = source[indices[i].m_Index].m_Vertex,
                        b = source[indices[i + 1].m_Index].m_Vertex,
                        c = source[indices[i + 2].m_Index].m_Vertex;
                    NativeTerrainConformance.AddEdge(heightBounds, a, b, piece.m_Size.z);
                    NativeTerrainConformance.AddEdge(heightBounds, b, c, piece.m_Size.z);
                    NativeTerrainConformance.AddEdge(heightBounds, c, a, piece.m_Size.z);
                }
            }
            int first = result.Vertices.Count;
            var accepted = new bool[source.Length];
            for (int i = 0; i < source.Length; i++)
            {
                float3 raw = source[i].m_Vertex;
                accepted[i] =
                    (!((!round && !preserve) || half) || raw.z >= -.01f)
                    && (!skipBottom || raw.z <= .01f);
                float3 p = raw * new float3(invert ? 1 : -1, 1, flip ? 1 : -1) + piece.m_Offset;
                if (half && !round && !preserve)
                    p.z = raw.z * (flip ? 2 : -2) + piece.m_Offset.z + piece.m_Size.z * .5f;
                float denom = math.max(.001f, piece.m_Size.z * .5f);
                var v = new Vertex { Height = p.y };
                if (heightBounds != null)
                {
                    var bounds = heightBounds[
                        NativeTerrainConformance.Slice(raw.z, piece.m_Size.z)
                    ];
                    if (lower && raw.y <= bounds.x + .01f)
                        v.TerrainMode = 1;
                    if (raise && raw.y >= bounds.y - .01f)
                        v.TerrainMode = 2;
                }
                if (preserve)
                {
                    v.A = 6;
                    v.B = 7;
                    v.U = p.x / data.m_Width + .5f;
                    v.T = (p.z / denom + 1) * (.5f * piece.m_Size.z / data.m_Width);
                }
                else
                {
                    float relative = p.x - piece.m_Offset.x;
                    if (median && piece.m_Size.x == 0)
                        p.x = piece.m_Offset.x;
                    bool center = median && math.abs(relative) < .01f;
                    bool useRight = median ? relative > 0 : right;
                    v.T = p.z / denom + 1;
                    bool second = round && p.z >= 0;
                    if (center)
                    {
                        v.A = round && second ? 5 : 4;
                        v.B = round ? (second ? 3 : 2) : 0;
                        v.U = 0;
                    }
                    else if (useRight)
                    {
                        v.A = round ? (second ? 5 : 4) : 1;
                        v.B = round && second ? 3 : (round ? 2 : 3);
                        v.U =
                            (p.x - data.m_MiddleOffset)
                            / math.max(.001f, data.m_Width * .5f - data.m_MiddleOffset);
                        v.Sync = 4;
                    }
                    else
                    {
                        v.A = round && second ? 1 : 0;
                        v.B = round ? (second ? 5 : 4) : 2;
                        v.U =
                            (p.x + data.m_Width * .5f)
                            / math.max(.001f, data.m_Width * .5f + data.m_MiddleOffset);
                        v.Sync = 2;
                    }
                    // The median's centre is pinned to the end of its matrix;
                    // only the side domains restart their parameter at the split.
                    if (second && !center)
                        v.T -= 1;
                    v.Final = second;
                }
                v.U = math.saturate(v.U);
                v.T = math.saturate(v.T);
                v.Lateral = p.x;
                result.Vertices.Add(v);
            }
            for (int i = 0; i + 2 < indices.Length; i += 3)
            {
                int a = indices[i].m_Index,
                    b = indices[i + 1].m_Index,
                    c = indices[i + 2].m_Index;
                if (!accepted[a] || !accepted[b] || !accepted[c])
                    continue;
                float up = hasNormals
                    ? normals[a].m_Normal.y + normals[b].m_Normal.y + normals[c].m_Normal.y
                    : math.cross(
                        source[b].m_Vertex - source[a].m_Vertex,
                        source[c].m_Vertex - source[a].m_Vertex
                    ).y;
                if (up < -.01f)
                    continue; // Undersides do not carry snow; curb faces must stay closed.
                bool wall = up <= .01f;
                result.Indices.Add(first + a);
                result.Indices.Add(first + (wall && invert != flip ? c : b));
                result.Indices.Add(first + (wall && invert != flip ? b : c));
                result.Walls.Add(wall);
                result.Kinds.Add(
                    wall
                        ? PlowSurfaceKind.Excluded
                        : PlowSurfaceClassification.Classify(
                            piece.m_PieceFlags,
                            piece.m_SectionFlags
                        )
                );
            }
        }
        return result.Indices.Count > 0 ? result : null;
    }

    private sealed class Template
    {
        public readonly List<Vertex> Vertices = new();
        public readonly List<int> Indices = new();
        public readonly List<bool> Walls = new();
        public readonly List<PlowSurfaceKind> Kinds = new();
    }

    private struct Vertex
    {
        public int A,
            B,
            Sync,
            TerrainMode;
        public float U,
            T,
            Height,
            Lateral;
        public bool Final;
    }
}
