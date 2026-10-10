using System;
using System.Collections.Generic;

namespace RoadSnow.Core;

public readonly struct SnowBankBand
{
    public readonly float Left;
    public readonly float Right;
    public readonly float Height;

    public SnowBankBand(float left, float right, float height)
    {
        Left = left;
        Right = right;
        Height = height;
    }
}

public static class SnowBankProfile
{
    public const float MaximumRise = .18f;

    public static SnowBankBand[] Build(IReadOnlyList<SurfaceBand> profile, float width, bool node)
    {
        if (node || profile == null || width <= 0)
        {
            return Array.Empty<SnowBankBand>();
        }

        var result = new List<SnowBankBand>();
        for (int i = 0; i < profile.Count; i++)
        {
            var band = profile[i];
            float span = band.Right - band.Left;
            if (!band.Pedestrian || span < .5f)
            {
                continue;
            }

            float bankWidth = Math.Min(.9f, span * .36f);
            bool leftCurb = i > 0 && band.Height - profile[i - 1].Height > .04f;
            bool rightCurb = i + 1 < profile.Count && band.Height - profile[i + 1].Height > .04f;
            if (leftCurb)
            {
                result.Add(new SnowBankBand(band.Left, band.Left + bankWidth, band.Height));
            }

            if (rightCurb)
            {
                result.Add(new SnowBankBand(band.Right - bankWidth, band.Right, band.Height));
            }

            if (leftCurb || rightCurb)
            {
                continue;
            }

            bankWidth = Math.Min(.55f, span * .28f);
            if (band.Left <= -width * .5f + .01f)
            {
                result.Add(new SnowBankBand(band.Left, band.Left + bankWidth, band.Height));
            }

            if (band.Right >= width * .5f - .01f)
            {
                result.Add(new SnowBankBand(band.Right - bankWidth, band.Right, band.Height));
            }
        }

        return result.ToArray();
    }

    public static float Rise(float across, float worldX, float worldZ, float distanceToEnd)
    {
        float dome = Math.Max(0, 4 * across * (1 - across));
        float cap = Math.Max(0, Math.Min(1, distanceToEnd / 1.6f));
        cap = cap * cap * (3 - 2 * cap);
        float organic = SnowAppearance.Grain(worldX * .75f, worldZ * .75f);
        return MaximumRise * dome * dome * cap * (.3f + .7f * organic);
    }
}
