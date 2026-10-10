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
}
