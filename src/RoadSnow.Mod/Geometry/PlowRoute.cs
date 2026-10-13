using System.Collections.Generic;

namespace RoadSnow.Mod;

// An edge and its node buckets share meshes. Visit each mesh exactly once per
// trajectory slice; clearing the same triangle twice is needless raster work.
internal sealed class PlowRoute
{
    private readonly HashSet<RoadPatch> _unique = new();
    public readonly List<RoadPatch> Patches = new();

    public void Clear()
    {
        _unique.Clear();
        Patches.Clear();
    }

    public void Add(IEnumerable<RoadPatch> patches)
    {
        foreach (var patch in patches)
            if (patch.Active && _unique.Add(patch))
                Patches.Add(patch);
    }

    public void Filter(UnityEngine.Bounds path)
    {
        // Broad phase once for the whole Bezier hull, before repeated surface
        // projections. Widen by road width so the opposite junction side stays.
        for (int i = Patches.Count - 1; i >= 0; i--)
        {
            var patch = Patches[i];
            var corridor = path;
            corridor.Expand(
                new UnityEngine.Vector3(patch.Field.WidthMeters * 2, 5, patch.Field.WidthMeters * 2)
            );
            if (!patch.Bounds.Intersects(corridor))
                Patches.RemoveAt(i);
        }
    }
}
