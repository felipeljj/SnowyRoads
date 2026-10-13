using System.Collections.Generic;
using RoadSnow.Core;
using UnityEngine;

namespace RoadSnow.Mod;

// The terrain is still sampled at the original cadence, including terraforming.
// Only identical geometry is retained; UVs and topology depend on vertex count.
internal sealed class TerrainMeshData
{
    public readonly ExactSequence<Vector3> Vertices = new();
    public readonly List<Vector2> UV = new();
    public readonly List<int> Indices = new();

    public bool End(out bool topologyChanged)
    {
        bool changed = Vertices.End();
        int count = Vertices.Items.Count;
        topologyChanged = UV.Count != count;
        if (topologyChanged)
        {
            UV.Clear();
            Indices.Clear();
            for (int first = 0; first < count; first += 81)
            {
                for (int i = 0; i < 81; i++)
                    UV.Add(Vector2.zero);
                for (int z = 0; z < 8; z++)
                for (int x = 0; x < 8; x++)
                {
                    int a = first + z * 9 + x;
                    Indices.Add(a);
                    Indices.Add(a + 9);
                    Indices.Add(a + 1);
                    Indices.Add(a + 1);
                    Indices.Add(a + 9);
                    Indices.Add(a + 10);
                }
            }
        }
        return changed;
    }
}
