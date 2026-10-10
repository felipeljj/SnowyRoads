using System;
using System.Collections.Generic;
using System.Linq;

namespace RoadSnow.Core;

public readonly struct SurfaceBand
{
    public readonly float Left;
    public readonly float Right;
    public readonly float Height;
    public readonly bool Pedestrian;

    public SurfaceBand(float left, float right, float height, bool pedestrian)
    {
        Left = left;
        Right = right;
        Height = height;
        Pedestrian = pedestrian;
    }
}

public static class SurfaceLayout
{
    public static List<SurfaceBand> Build(
        float width,
        IReadOnlyList<SurfaceBand> lanes,
        IReadOnlyList<SurfaceBand> pieces,
        float fallbackHeight
    )
    {
        Guard.Positive(width, nameof(width));
        Guard.Finite(fallbackHeight, nameof(fallbackHeight));
        float left = -width / 2;
        float right = width / 2;
        var cuts = new SortedSet<float> { left, right };
        foreach (var band in lanes.Concat(pieces))
        {
            Guard.Finite(band.Left, nameof(lanes));
            Guard.Finite(band.Right, nameof(lanes));
            Guard.Finite(band.Height, nameof(lanes));
            if (band.Right <= band.Left)
            {
                continue;
            }

            cuts.Add(Math.Max(left, Math.Min(right, band.Left)));
            cuts.Add(Math.Max(left, Math.Min(right, band.Right)));
        }

        var positions = cuts.ToArray();
        var result = new List<SurfaceBand>();
        for (int i = 0; i < positions.Length - 1; i++)
        {
            float a = positions[i];
            float b = positions[i + 1];
            float middle = (a + b) / 2;
            if (b - a < 0.001f)
            {
                continue;
            }

            float height = fallbackHeight;
            bool physical = false;
            bool pedestrian = false;
            foreach (var piece in pieces)
            {
                if (middle < piece.Left || middle > piece.Right)
                {
                    continue;
                }

                height = physical ? Math.Max(height, piece.Height) : piece.Height;
                physical = true;
                pedestrian |=
                    lanes.Any(l => l.Pedestrian && l.Left < piece.Right && l.Right > piece.Left)
                    && !lanes.Any(l =>
                        !l.Pedestrian && l.Left < piece.Right && l.Right > piece.Left
                    );
            }

            foreach (var lane in lanes)
            {
                if (middle < lane.Left || middle > lane.Right)
                {
                    continue;
                }

                pedestrian |= lane.Pedestrian;
                height = Math.Max(height, lane.Height);
            }

            if (result.Count > 0)
            {
                var previous = result[result.Count - 1];
                if (
                    previous.Pedestrian == pedestrian
                    && Math.Abs(previous.Height - height) < 0.001f
                )
                {
                    result[result.Count - 1] = new SurfaceBand(
                        previous.Left,
                        b,
                        height,
                        pedestrian
                    );
                    continue;
                }
            }

            result.Add(new SurfaceBand(a, b, height, pedestrian));
        }

        return result;
    }
}
