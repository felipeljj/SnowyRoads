using System;
using System.Collections.Generic;
using RoadSnow.Core;
using UnityEngine;

namespace RoadSnow.Mod;

internal sealed class SnowBankMesh
{
    public readonly List<Vector3> Vertices = new();
    public readonly List<Vector2> UV = new();
    public readonly List<Vector2> BaseAndRise = new();
    public readonly List<int> Triangles = new();
    public readonly List<bool> Interior = new();
    public int TriangleCount => Triangles.Count / 3;

    public static SnowBankMesh Build(
        SnowBankBand[] bands,
        float width,
        float length,
        int lod,
        Func<float, float, float, Vector3> point
    )
    {
        var mesh = new SnowBankMesh();
        int rows = Mathf.Clamp(
            Mathf.CeilToInt(
                length
                    / (
                        lod == 0 ? .9f
                        : lod == 1 ? 1.8f
                        : 3f
                    )
            ),
            2,
            64
        );
        int columns = lod == 0 ? 6 : 4;
        foreach (var band in bands)
        {
            int first = mesh.Vertices.Count;
            for (int row = 0; row <= rows; row++)
            {
                for (int column = 0; column <= columns; column++)
                {
                    float t = row / (float)rows;
                    float u = column / (float)columns;
                    float across = (Mathf.Lerp(band.Left, band.Right, u) + width * .5f) / width;
                    Vector3 p = point(across, t, band.Height);
                    float rise = SnowBankProfile.Rise(u, p.x, p.z, Mathf.Min(t, 1 - t) * length);
                    mesh.Interior.Add(false);
                    mesh.BaseAndRise.Add(new Vector2(p.y, rise));
                    mesh.Vertices.Add(p + Vector3.up * rise);
                    mesh.UV.Add(new Vector2(across, t));
                    if (row == rows || column == columns)
                    {
                        continue;
                    }

                    int a = first + row * (columns + 1) + column;
                    int b = a + columns + 1;
                    mesh.Triangles.Add(a);
                    mesh.Triangles.Add(b);
                    mesh.Triangles.Add(a + 1);
                    mesh.Triangles.Add(a + 1);
                    mesh.Triangles.Add(b);
                    mesh.Triangles.Add(b + 1);
                }
            }
        }

        return mesh;
    }

    public void AddRoadbeds(
        SurfaceBand[] bands,
        float width,
        float length,
        int lod,
        Func<float, float, float, Vector3> point
    )
    {
        // The lane/wheel masks sculpt this sheet on the GPU. There is no fixed
        // lane count and no new object or mesh allocation for each passing car.
        float spacing =
            lod == 0 ? .25f
            : lod == 1 ? .5f
            : 1f;
        int rows = Mathf.Clamp(
            Mathf.CeilToInt(
                length
                    / (
                        lod == 0 ? .6f
                        : lod == 1 ? 1.2f
                        : 2.4f
                    )
            ),
            2,
            96
        );
        foreach (var band in bands)
        {
            if (band.Pedestrian || band.Right - band.Left < .4f)
                continue;
            int columns = Mathf.Clamp(Mathf.CeilToInt((band.Right - band.Left) / spacing), 2, 128);
            int first = Vertices.Count;
            for (int row = 0; row <= rows; row++)
            for (int column = 0; column <= columns; column++)
            {
                float t = row / (float)rows,
                    u = column / (float)columns;
                float across = (Mathf.Lerp(band.Left, band.Right, u) + width * .5f) / width;
                Vector3 p = point(across, t, band.Height);
                float end = Mathf.SmoothStep(0, 1, Mathf.Min(t, 1 - t) * length / 1.5f);
                float edge = Mathf.SmoothStep(
                    0,
                    1,
                    Mathf.Min(u, 1 - u) * (band.Right - band.Left) / .35f
                );
                float rise = .10f * end * edge;
                Vertices.Add(p + Vector3.up * rise);
                BaseAndRise.Add(new Vector2(p.y, rise));
                UV.Add(new Vector2(across, t));
                Interior.Add(true);
                if (row == rows || column == columns)
                    continue;
                int a = first + row * (columns + 1) + column,
                    b = a + columns + 1;
                Triangles.Add(a);
                Triangles.Add(b);
                Triangles.Add(a + 1);
                Triangles.Add(a + 1);
                Triangles.Add(b);
                Triangles.Add(b + 1);
            }
        }
    }
}
