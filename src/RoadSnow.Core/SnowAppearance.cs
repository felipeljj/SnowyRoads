using System;

namespace RoadSnow.Core;

public static class SnowAppearance
{
    public static float Grain(float worldX, float worldZ) =>
        (float)(
            0.55 * Noise(worldX * 0.13, worldZ * 0.13)
            + 0.3 * Noise(worldX * 0.57 + 23, worldZ * 0.57 - 11)
            + 0.15 * Noise(worldX * 2.1, worldZ * 2.1)
        );

    public static byte Alpha(byte coverage, float grain, float edgeDistance, bool pedestrian)
    {
        if (coverage == 0)
        {
            return 0;
        }

        float feather = Math.Min(1, Math.Max(0, edgeDistance) / (pedestrian ? 0.10f : 0.18f));
        feather = feather * feather * (3 - 2 * feather);
        float fraction = coverage / 255f;
        float threshold = 0.04f + grain * 0.65f;
        float organic = fraction >= 1 ? 1 : Math.Max(0, (fraction - threshold) / (1 - threshold));
        organic = organic * organic * (3 - 2 * organic);
        organic = 0.08f * fraction + 0.92f * organic;
        return (byte)Math.Round(255 * organic * feather);
    }

    private static double Noise(double x, double z)
    {
        x %= 1_000_000;
        z %= 1_000_000;
        int ix = (int)Math.Floor(x);
        int iz = (int)Math.Floor(z);
        double tx = x - ix;
        double tz = z - iz;
        tx = tx * tx * (3 - 2 * tx);
        tz = tz * tz * (3 - 2 * tz);
        double a = Hash(ix, iz);
        double b = Hash(ix + 1, iz);
        double c = Hash(ix, iz + 1);
        double d = Hash(ix + 1, iz + 1);
        return (a + (b - a) * tx) * (1 - tz) + (c + (d - c) * tx) * tz;
    }

    private static double Hash(int x, int z)
    {
        unchecked
        {
            uint v = (uint)x * 374761393u + (uint)z * 668265263u + 1984;
            v = (v ^ (v >> 13)) * 1274126177u;
            v ^= v >> 16;
            return (v & 0x00ffffff) / 16777215.0;
        }
    }
}
