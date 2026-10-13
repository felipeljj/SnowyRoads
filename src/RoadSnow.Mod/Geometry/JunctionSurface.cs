using System;
using System.Collections.Generic;
using Colossal.Mathematics;
using Game.Net;
using RoadSnow.Core;
using Unity.Mathematics;

namespace RoadSnow.Mod;

internal static class JunctionSurface
{
    private const int IslandSides = 48;
    private static readonly float2[] IslandNormals = CreateIslandNormals();

    private static float2[] CreateIslandNormals()
    {
        var normals = new float2[IslandSides];
        for (int i = 0; i < normals.Length; i++)
        {
            float angle = (i + .5f) * (2 * math.PI / IslandSides);
            normals[i] = new float2(math.cos(angle), math.sin(angle));
        }
        return normals;
    }

    internal sealed class MeshData
    {
        public readonly List<float3> Vertices = new();
        public readonly List<int> Indices = new();
        public readonly List<int> RowStarts = new();

        // Native undeformed surface heights classify asphalt independently of
        // the world-space slope. Entries omitted by fallback meshes are unknown.
        public readonly List<float> RoadHeights = new();
        public readonly List<PlowSurfaceKind> RoadKinds = new();
        public readonly List<float2> RoadRanges = new();
        private readonly Dictionary<float3, int> _vertices = new();

        public int Vertex(float3 position)
        {
            // Shared vertices also share normals. Separate triangle vertices made
            // curved surfaces acquire visible lighting seams along their diagonals.
            float3 key = math.round(position * 10000) / 10000;
            if (_vertices.TryGetValue(key, out int index))
                return index;
            index = Vertices.Count;
            _vertices.Add(key, index);
            Vertices.Add(position);
            return index;
        }
    }

    public static MeshData Build(
        EdgeNodeGeometry node,
        float width,
        float middleAcross,
        SurfaceBand[] profile,
        float offset,
        float4? syncLeft = null,
        float4? syncRight = null
    )
    {
        var mesh = new MeshData();
        float middle = (middleAcross - .5f) * width;
        bool roundabout = node.m_MiddleRadius > 0;
        // Match the native deformation domains (0/2, 4, 1/3). The central fan
        // has its own vertices; it must not squeeze sidewalks into a quarter width.
        var strips = new List<(float A, float B, float Height, int Domain, bool Pedestrian)>();
        foreach (var band in profile ?? new[] { new SurfaceBand(-width / 2, width / 2, 0, false) })
        {
            if (band.Left < middle)
                strips.Add(
                    (
                        (band.Left + width / 2) / (middle + width / 2),
                        (Math.Min(band.Right, middle) + width / 2) / (middle + width / 2),
                        band.Height,
                        0,
                        band.Pedestrian
                    )
                );
        }
        float floor = FloorHeight(profile, middle);
        if (!roundabout)
        {
            strips.Add((0, 1, floor, 1, false));
            strips.Add((0, 1, floor, 2, false));
        }
        foreach (var band in profile ?? new[] { new SurfaceBand(-width / 2, width / 2, 0, false) })
        {
            if (band.Right > middle)
                strips.Add(
                    (
                        (Math.Max(band.Left, middle) - middle) / (width / 2 - middle),
                        (band.Right - middle) / (width / 2 - middle),
                        band.Height,
                        3,
                        band.Pedestrian
                    )
                );
        }
        int steps = math.clamp(
            (int)Math.Ceiling(Math.Max(node.m_Left.middleLength, node.m_Right.middleLength) / .75f),
            24,
            96
        );
        if (roundabout)
            steps = (steps + 1) / 2 * 2;
        for (int row = 0; row < steps; row++)
        {
            mesh.RowStarts.Add(mesh.Indices.Count);
            float t0 = row / (float)steps,
                t1 = (row + 1f) / steps;
            foreach (var strip in strips)
            {
                float3 Sample(float u, float t)
                {
                    if (strip.Domain == 0 && syncLeft.HasValue)
                        u = Synchronize(node, strip.Domain, u, t, syncLeft.Value);
                    else if (strip.Domain == 3 && syncRight.HasValue)
                        u = Synchronize(node, strip.Domain, u, t, syncRight.Value);
                    return Point(node, strip.Domain, u, t);
                }
                float3 a = Sample(strip.A, t0);
                float3 b = Sample(strip.A, t1);
                float3 c = Sample(strip.B, t0);
                float3 d = Sample(strip.B, t1);
                a.y += strip.Height + offset;
                b.y += strip.Height + offset;
                c.y += strip.Height + offset;
                d.y += strip.Height + offset;
                int firstTriangle = mesh.Indices.Count / 3;
                AddTriangle(mesh, a, b, c, node);
                AddTriangle(mesh, c, b, d, node);
                for (int triangle = firstTriangle; triangle < mesh.Indices.Count / 3; triangle++)
                {
                    mesh.RoadHeights.Add(strip.Height);
                    mesh.RoadKinds.Add(
                        strip.Pedestrian ? PlowSurfaceKind.Excluded : PlowSurfaceKind.Unknown
                    );
                }
            }
        }
        mesh.RowStarts.Add(mesh.Indices.Count);
        return mesh;
    }

    // Matches the installed network vertex shader: width-weighted sync targets
    // align curbs from different road profiles along the connection.
    internal static float Synchronize(
        EdgeNodeGeometry node,
        int domain,
        float u,
        float t,
        float4 source
    )
    {
        bool second = node.m_MiddleRadius > 0 && t > .5f;
        float startT = node.m_MiddleRadius > 0 && second ? .500001f : 0;
        float endT = node.m_MiddleRadius > 0 && !second ? .5f : 1;
        float startWidth = math.distance(
            Point(node, domain, 0, startT).xz,
            Point(node, domain, 1, startT).xz
        );
        float endWidth = math.distance(
            Point(node, domain, 0, endT).xz,
            Point(node, domain, 1, endT).xz
        );
        if (node.m_MiddleRadius > 0)
            t = second ? t * 2 - 1 : t * 2;
        var target = domain == 0 ? node.m_SyncVertexTargetsLeft : node.m_SyncVertexTargetsRight;
        return Synchronize(u, t, source, target, startWidth, endWidth, second);
    }

    internal static float Synchronize(
        float u,
        float t,
        float4 source,
        float4 target,
        float startWidth,
        float endWidth,
        bool second
    )
    {
        float blend = second ? 1 : 2 * math.smoothstep(0, 1, t * .5f);
        float4 mapped =
            math.lerp(source * startWidth, target * endWidth, blend)
            / math.max(.000001f, math.lerp(startWidth, endWidth, blend));
        float s2 = math.min(source.y, source.z);
        float s1 = math.min(source.x, s2 - .000001f);
        float s3 = math.max(source.z, s2 + .000001f);
        float s4 = math.max(source.w, s3 + .000001f);
        float result =
            math.saturate((u - math.min(0, s1 - .000001f)) / (s1 - math.min(0, s1 - .000001f)))
            * mapped.x;
        result += math.saturate((u - s1) / (s2 - s1)) * (mapped.y - mapped.x);
        result += math.saturate((u - s2) / (s3 - s2)) * (mapped.z - mapped.y);
        result += math.saturate((u - s3) / (s4 - s3)) * (mapped.w - mapped.z);
        result += math.saturate((u - s4) / (math.max(1, s4 + .000001f) - s4)) * (1 - mapped.w);
        return math.saturate(result);
    }

    internal static float3 Point(EdgeNodeGeometry node, int domain, float across, float along)
    {
        if (node.m_MiddleRadius > 0)
        {
            bool second = along > .5f;
            float t = second ? along * 2 - 1 : along * 2;
            Segment side = second ? node.m_Right : node.m_Left;
            // BatchDataHelpers splits the native center curve at .99, not halfway.
            float3 center = MathUtils.Position(node.m_Middle, second ? .99f + t * .01f : t * .99f);
            return domain == 0
                ? math.lerp(MathUtils.Position(side.m_Left, t), center, math.saturate(across))
                : math.lerp(center, MathUtils.Position(side.m_Right, t), math.saturate(across));
        }
        float3 left = MathUtils.Position(node.m_Left.m_Left, along);
        float3 leftInner = MathUtils.Position(node.m_Left.m_Right, along);
        float3 centerPoint = MathUtils.Position(node.m_Middle, along);
        float3 rightInner = MathUtils.Position(node.m_Right.m_Left, along);
        float3 right = MathUtils.Position(node.m_Right.m_Right, along);
        return domain switch
        {
            0 => math.lerp(left, leftInner, math.saturate(across)),
            1 => math.lerp(leftInner, centerPoint, across),
            2 => math.lerp(centerPoint, rightInner, across),
            _ => math.lerp(rightInner, right, math.saturate(across)),
        };
    }

    private static float FloorHeight(SurfaceBand[] profile, float middle)
    {
        float distance = float.MaxValue,
            height = 0;
        if (profile == null)
            return height;
        foreach (var band in profile)
        {
            if (band.Pedestrian)
                continue;
            float d = Math.Max(band.Left - middle, Math.Max(middle - band.Right, 0));
            if (d < distance || d == distance && band.Height < height)
            {
                distance = d;
                height = band.Height;
            }
        }
        return height;
    }

    private static void AddTriangle(
        MeshData mesh,
        float3 a,
        float3 b,
        float3 c,
        EdgeNodeGeometry node
    )
    {
        if (node.m_MiddleRadius <= 0)
        {
            Emit(mesh, a, b, c);
            return;
        }
        // Subtract a fixed polygon for the native island. A triangle that straddles
        // the island is clipped, never stretched across it or raised into a sheet.
        float2 center = node.m_Middle.d.xz;
        float2 nearest = math.clamp(
            center,
            math.min(a.xz, math.min(b.xz, c.xz)),
            math.max(a.xz, math.max(b.xz, c.xz))
        );
        if (math.lengthsq(nearest - center) >= node.m_MiddleRadius * node.m_MiddleRadius)
        {
            Emit(mesh, a, b, c);
            return;
        }
        var inside = new List<float3> { a, b, c };
        float radius = node.m_MiddleRadius * math.cos(math.PI / IslandSides);
        for (int edge = 0; edge < IslandSides && inside.Count >= 3; edge++)
        {
            float2 normal = IslandNormals[edge];
            var remaining = new List<float3>();
            var outside = new List<float3>();
            float3 previous = inside[inside.Count - 1];
            float previousDistance = math.dot(previous.xz - center, normal) - radius;
            foreach (float3 current in inside)
            {
                float currentDistance = math.dot(current.xz - center, normal) - radius;
                if ((previousDistance > 0) != (currentDistance > 0))
                {
                    float3 intersection = math.lerp(
                        previous,
                        current,
                        previousDistance / (previousDistance - currentDistance)
                    );
                    remaining.Add(intersection);
                    outside.Add(intersection);
                }
                if (currentDistance > 0)
                    outside.Add(current);
                else
                    remaining.Add(current);
                previous = current;
                previousDistance = currentDistance;
            }
            for (int i = 1; i + 1 < outside.Count; i++)
                Emit(mesh, outside[0], outside[i], outside[i + 1]);
            inside = remaining;
        }
    }

    internal static void Emit(MeshData mesh, float3 a, float3 b, float3 c)
    {
        float area = math.cross(b - a, c - a).y;
        if (
            !math.all(math.isfinite(a))
            || !math.all(math.isfinite(b))
            || !math.all(math.isfinite(c))
            || math.abs(area) < .000001f
        )
            return;
        int ia = mesh.Vertex(a),
            ib = mesh.Vertex(b),
            ic = mesh.Vertex(c);
        if (ia == ib || ib == ic || ia == ic)
            return;
        mesh.Indices.Add(ia);
        mesh.Indices.Add(area > 0 ? ib : ic);
        mesh.Indices.Add(area > 0 ? ic : ib);
    }

    internal static void EmitWall(MeshData mesh, float3 a, float3 b, float3 c)
    {
        // A vertical curb has zero projected XZ area, but is not a degenerate
        // face. Keep its native winding and a tiny outward bias against z-fight.
        float3 normal = math.cross(b - a, c - a);
        if (!math.all(math.isfinite(normal)) || math.lengthsq(normal) < 1e-12f)
            return;
        float3 bias = math.normalizesafe(new float3(normal.x, 0, normal.z)) * .002f;
        EmitFace(mesh, a + bias, b + bias, c + bias);
    }

    private static void EmitFace(MeshData mesh, float3 a, float3 b, float3 c)
    {
        if (
            !math.all(math.isfinite(a))
            || !math.all(math.isfinite(b))
            || !math.all(math.isfinite(c))
            || math.lengthsq(math.cross(b - a, c - a)) < 1e-12f
        )
            return;
        // Separate side vertices preserve the pavement's upward shading normal.
        int first = mesh.Vertices.Count;
        mesh.Vertices.Add(a);
        mesh.Vertices.Add(b);
        mesh.Vertices.Add(c);
        mesh.Indices.Add(first);
        mesh.Indices.Add(first + 1);
        mesh.Indices.Add(first + 2);
    }

    internal static void CloseLiftedEdges(MeshData mesh, float lift)
    {
        // Only the outside of the top surface gets a skirt. Interior triangle
        // diagonals must never become walls (the old star-shaped junction bug).
        var edges = new Dictionary<(int, int), (int A, int B, int Count)>();
        void Add(int a, int b)
        {
            var key = a < b ? (a, b) : (b, a);
            if (edges.TryGetValue(key, out var edge))
                edges[key] = (edge.A, edge.B, edge.Count + 1);
            else
                edges.Add(key, (a, b, 1));
        }
        for (int i = 0; i < mesh.Indices.Count; i += 3)
        {
            int a = mesh.Indices[i],
                b = mesh.Indices[i + 1],
                c = mesh.Indices[i + 2];
            if (
                math.cross(
                    mesh.Vertices[b] - mesh.Vertices[a],
                    mesh.Vertices[c] - mesh.Vertices[a]
                ).y <= 1e-6f
            )
                continue;
            Add(a, b);
            Add(b, c);
            Add(c, a);
        }
        float3 drop = new(0, math.max(.001f, lift) + .005f, 0);
        foreach (var edge in edges.Values)
        {
            if (edge.Count != 1)
                continue;
            float3 a = mesh.Vertices[edge.A],
                b = mesh.Vertices[edge.B];
            EmitFace(mesh, a, a - drop, b);
            EmitFace(mesh, b, a - drop, b - drop);
        }
        if (mesh.RowStarts[mesh.RowStarts.Count - 1] != mesh.Indices.Count)
            mesh.RowStarts.Add(mesh.Indices.Count);
    }
}
