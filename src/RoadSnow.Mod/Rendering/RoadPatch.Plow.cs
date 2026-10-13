using System;
using RoadSnow.Core;
using UnityEngine;

namespace RoadSnow.Mod;

internal sealed partial class RoadPatch
{
    public bool CompleteSectionPlow(double snowfallMm)
    {
        if (!Active || Pedestrian || !HasSnow || !_hasPlowRoad)
            return false;
        bool changed = _sectionPlow.Complete(snowfallMm);
        if (changed)
        {
            Dirty = true;
            Upload(true);
        }
        return changed;
    }

    public void RestoreSectionPlow(double atMm, double snowfallMm)
    {
        _sectionPlow.Restore(atMm, snowfallMm);
        Dirty = true;
    }

    public void ObservePlowSnowfall(double snowfallMm)
    {
        Dirty |= _sectionPlow.Observe(snowfallMm);
    }

    public void DepositSectionSnow(
        float depthMm,
        double snowfallMm,
        Action<Vector3, Vector3, float> add
    )
    {
        if (!Active || !HasSnow || !HasRoadBanks || !_hasPlowRoad || depthMm < .2f)
            return;
        var prior = new SectionPlowState();
        prior.Restore(_sectionPlow.AtSnowfallMm, snowfallMm);
        float removedFraction = 1 - prior.Strength;
        if (removedFraction <= 0)
            return;
        int steps = Mathf.Clamp(Mathf.CeilToInt(Field.LengthMeters / 2), 1, 256);
        for (int i = 0; i < steps; i++)
        {
            float t = (i + .5f) / steps;
            Vector3 center = SurfacePoint(.5f, t);
            Vector3 direction = (
                SurfacePoint(.5f, Mathf.Min(1, t + .001f))
                - SurfacePoint(.5f, Mathf.Max(0, t - .001f))
            ).normalized;
            if (TryPlowEdge(center, direction, out var edge, out float width))
                add(
                    edge,
                    direction,
                    Mathf.Clamp(depthMm, 0, 80)
                        * .001f
                        * width
                        * Field.LengthMeters
                        / steps
                        * removedFraction
                        * .5f
                );
        }
    }

    public bool TryPlowEdge(Vector3 center, Vector3 direction, out Vector3 edge, out float width)
    {
        edge = default;
        width = 0;
        if (
            !Active
            || Pedestrian
            || _nodeGeometry.HasValue
            || _profile == null
            || !TryProject(center, out var local)
        )
            return false;
        float low = float.MaxValue,
            left = float.MaxValue,
            right = float.MinValue;
        foreach (var band in _profile)
            if (!band.Pedestrian)
                low = Math.Min(low, band.Height);
        foreach (var band in _profile)
            if (!band.Pedestrian && band.Height <= low + .08f)
            {
                left = Math.Min(left, band.Left);
                right = Math.Max(right, band.Right);
            }
        if (left >= right)
            return false;
        float along = local.y / Field.LengthMeters;
        Vector3 a = SurfacePoint((left + Field.WidthMeters * .5f) / Field.WidthMeters, along, low);
        Vector3 b = SurfacePoint((right + Field.WidthMeters * .5f) / Field.WidthMeters, along, low);
        Vector3 bladeRight = Vector3.Cross(Vector3.up, direction);
        edge = Vector3.Dot(b - a, bladeRight) > 0 ? b : a;
        width = Vector3.Distance(a, b);
        return true;
    }

    public int PlowRoad(
        Vector3 from,
        Vector3 to,
        PlowSurfaceClip clip,
        Action<Vector3, Vector3, Vector3> stamp,
        Action<PlowFeather> configureFeather = null
    )
    {
        if (!Active || !HasSnow || Pedestrian || _profile == null)
            return 0;
        // Revisit the short trailing fringe so many small movements reach full
        // cleaning behind the blade. The brush never extends ahead of 'to'.
        Vector3 travel = to - from;
        float distance = new Vector2(travel.x, travel.z).magnitude;
        if (distance > .001f)
            from -= travel * (.65f / distance);
        // A node consists of separate meshes, some entirely opposite the lane.
        // Test the full-width sweep, not only the vehicle's narrow trajectory.
        if (!IntersectsPlowSweep(from, to))
            return 0;
        float lowest = float.MaxValue,
            left = float.MaxValue,
            right = float.MinValue;
        foreach (var band in _profile)
            if (!band.Pedestrian)
                lowest = Math.Min(lowest, band.Height);
        foreach (var band in _profile)
            if (!band.Pedestrian && band.Height <= lowest + .08f)
            {
                left = Math.Min(left, band.Left);
                right = Math.Max(right, band.Right);
            }
        if (left >= right)
            return 0;
        int triangles = 0;
        if (!_nodeGeometry.HasValue)
        {
            if (!TryProject(from, out var a, true) || !TryProject(to, out var b, true))
                return 0;
            float start = Mathf.Clamp01(Math.Min(a.y, b.y) / Field.LengthMeters);
            float end = Mathf.Clamp01(Math.Max(a.y, b.y) / Field.LengthMeters);
            bool forward = b.y >= a.y;
            // The blade keeps moving beyond this patch. Clamping its fringe to
            // the patch boundary permanently left an icy transverse seam there.
            float tip = b.y / Field.LengthMeters;
            if (end <= start)
                return 0;
            foreach (var band in _profile)
            {
                if (band.Pedestrian || band.Height > lowest + .08f)
                    continue;
                float lo = (band.Left + Field.WidthMeters * .5f) / Field.WidthMeters;
                float hi = (band.Right + Field.WidthMeters * .5f) / Field.WidthMeters;
                for (int i = 0; i < _triangles.Length; i += 3)
                {
                    int ia = _triangles[i],
                        ib = _triangles[i + 1],
                        ic = _triangles[i + 2];
                    var ua = _uv[ia];
                    var ub = _uv[ib];
                    var uc = _uv[ic];
                    if (
                        Math.Max(ua.y, Math.Max(ub.y, uc.y)) < start
                        || Math.Min(ua.y, Math.Min(ub.y, uc.y)) > end
                        || Math.Max(ua.x, Math.Max(ub.x, uc.x)) <= lo
                        || Math.Min(ua.x, Math.Min(ub.x, uc.x)) >= hi
                    )
                        continue;
                    if (configureFeather != null)
                    {
                        var va = _vertices[ia];
                        var vb = _vertices[ib];
                        var vc = _vertices[ic];
                        configureFeather(
                            PlowFeather.Surface(
                                va.x,
                                va.z,
                                vb.x,
                                vb.z,
                                vc.x,
                                vc.z,
                                ua.x,
                                ua.y,
                                ub.x,
                                ub.y,
                                uc.x,
                                uc.y,
                                tip,
                                forward,
                                (left + Field.WidthMeters * .5f) / Field.WidthMeters,
                                (right + Field.WidthMeters * .5f) / Field.WidthMeters,
                                Field.WidthMeters,
                                Field.LengthMeters
                            )
                        );
                    }
                    triangles += clip.Clip(
                        _vertices[ia],
                        _vertices[ib],
                        _vertices[ic],
                        ua,
                        ub,
                        uc,
                        lo,
                        hi,
                        start,
                        end,
                        stamp
                    );
                }
            }
        }
        else
        {
            // Junction UVs are planar. Clip actual mesh triangles to the driven
            // corridor; height rejects lifted sidewalks/medians and other decks.
            Vector3 along = to - from;
            along.y = 0;
            float length = along.magnitude;
            if (length < .001f)
                return 0;
            along /= length;
            Vector3 across = Vector3.Cross(Vector3.up, along);
            // Clear the whole junction cross-section, even when the vehicle is
            // traveling in the outside lane. The mesh and height test bound it.
            float halfWidth = _bounds.size.x + _bounds.size.z;
            for (int i = 0; i < _triangles.Length; i += 3)
            {
                if (_junctionRoadTriangles != null && !_junctionRoadTriangles[i / 3])
                    continue;
                var a = _vertices[_triangles[i]];
                var b = _vertices[_triangles[i + 1]];
                var c = _vertices[_triangles[i + 2]];
                Vector2 ua = new(Vector3.Dot(a - from, across), Vector3.Dot(a - from, along));
                Vector2 ub = new(Vector3.Dot(b - from, across), Vector3.Dot(b - from, along));
                Vector2 uc = new(Vector3.Dot(c - from, across), Vector3.Dot(c - from, along));
                if (
                    Math.Max(ua.y, Math.Max(ub.y, uc.y)) < 0
                    || Math.Min(ua.y, Math.Min(ub.y, uc.y)) > length
                )
                    continue;
                float slope = (to.y - from.y) / length;
                triangles += clip.Clip(
                    a,
                    b,
                    c,
                    ua,
                    ub,
                    uc,
                    -halfWidth,
                    halfWidth,
                    0,
                    length,
                    stamp,
                    _junctionRoadTriangles == null ? from.y : null,
                    slope
                );
            }
        }
        return triangles;
    }

    private bool IntersectsPlowSweep(Vector3 from, Vector3 to)
    {
        var min = _bounds.min;
        var max = _bounds.max;
        if (Math.Max(from.y, to.y) < min.y || Math.Min(from.y, to.y) > max.y)
            return false;
        if (!_nodeGeometry.HasValue)
            return Math.Max(from.x, to.x) >= min.x
                && Math.Min(from.x, to.x) <= max.x
                && Math.Max(from.z, to.z) >= min.z
                && Math.Min(from.z, to.z) <= max.z;
        var direction = to - from;
        direction.y = 0;
        float length = direction.magnitude;
        if (length < .001f)
            return false;
        direction /= length;
        var side = Vector3.Cross(Vector3.up, direction);
        var delta = _bounds.center - from;
        var extent = _bounds.extents;
        float along = Vector3.Dot(delta, direction);
        float radius = Math.Abs(direction.x) * extent.x + Math.Abs(direction.z) * extent.z;
        float lateral = Math.Abs(Vector3.Dot(delta, side));
        float lateralRadius = Math.Abs(side.x) * extent.x + Math.Abs(side.z) * extent.z;
        return along + radius >= 0
            && along - radius <= length
            && lateral <= Field.WidthMeters + lateralRadius;
    }
}
