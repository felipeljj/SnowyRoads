using System;
using System.Collections.Generic;
using UnityEngine;

namespace RoadSnow.Mod;

internal static class SectionPlowGeometry
{
    // Road and sidewalk faces can share a vertex. Split only those boundaries
    // so a constant road-clear flag cannot interpolate across the whole sidewalk.
    public static byte[] Separate(
        ref Vector3[] vertices,
        ref Vector2[] uv,
        int[] indices,
        bool[] road
    )
    {
        var kinds = new int[vertices.Length];
        for (int i = 0; i < kinds.Length; i++)
            kinds[i] = -1;
        var points = new List<Vector3>(vertices);
        var coordinates = new List<Vector2>(uv);
        var masks = new List<byte>(new byte[vertices.Length]);
        var copies = new Dictionary<int, int>();
        for (int triangle = 0; triangle < road.Length; triangle++)
        {
            int kind = road[triangle] ? 1 : 0;
            for (int corner = 0; corner < 3; corner++)
            {
                int at = triangle * 3 + corner,
                    original = indices[at];
                if (kinds[original] < 0)
                {
                    kinds[original] = kind;
                    masks[original] = (byte)(kind * 255);
                }
                else if (kinds[original] != kind)
                {
                    if (!copies.TryGetValue(original, out int copy))
                    {
                        copy = points.Count;
                        copies.Add(original, copy);
                        points.Add(vertices[original]);
                        coordinates.Add(uv[original]);
                        masks.Add((byte)(kind * 255));
                    }
                    indices[at] = copy;
                }
            }
        }
        vertices = points.ToArray();
        uv = coordinates.ToArray();
        return masks.ToArray();
    }
}
