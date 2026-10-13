using System;

namespace RoadSnow.Core;

// World-anchored brush boundaries; never feather the interior triangle edges.
public readonly struct PlowFeather
{
    private readonly float _cx,
        _cz,
        _nx,
        _nz,
        _halfWidth;
    private readonly float _tipX,
        _tipZ,
        _dx,
        _dz;
    private readonly bool _active;
    private readonly bool _surface;
    private readonly float _alongOffset,
        _sideOffset;

    public PlowFeather(
        float tipX,
        float tipZ,
        float dx,
        float dz,
        float centerX = 0,
        float centerZ = 0,
        float nx = 0,
        float nz = 0,
        float halfWidth = 0
    )
    {
        _tipX = tipX;
        _tipZ = tipZ;
        _dx = dx;
        _dz = dz;
        _cx = centerX;
        _cz = centerZ;
        _nx = nx;
        _nz = nz;
        _halfWidth = halfWidth;
        _active = true;
        _surface = false;
        _alongOffset = _sideOffset = 0;
    }

    private PlowFeather(
        float originX,
        float originZ,
        float alongX,
        float alongZ,
        float alongOffset,
        float sideX,
        float sideZ,
        float sideOffset,
        float halfWidth,
        bool surface
    )
    {
        _tipX = originX;
        _tipZ = originZ;
        _dx = alongX;
        _dz = alongZ;
        _alongOffset = alongOffset;
        _nx = sideX;
        _nz = sideZ;
        _sideOffset = sideOffset;
        _halfWidth = halfWidth;
        _cx = _cz = 0;
        _surface = surface;
        _active = true;
    }

    // Affine UV mapping of the same road triangle being rasterized. A skewed
    // cross-section must have one longitudinal blade front across its full width.
    public static PlowFeather Surface(
        float ax,
        float az,
        float bx,
        float bz,
        float cx,
        float cz,
        float au,
        float av,
        float bu,
        float bv,
        float cu,
        float cv,
        float tipV,
        bool forward,
        float leftU,
        float rightU,
        float width,
        float length
    )
    {
        double abx = bx - ax,
            abz = bz - az,
            acx = cx - ax,
            acz = cz - az;
        double determinant = abx * acz - abz * acx;
        if (Math.Abs(determinant) < .000001)
            return default;
        float dudx = (float)(((bu - au) * acz - (cu - au) * abz) / determinant);
        float dudz = (float)((abx * (cu - au) - acx * (bu - au)) / determinant);
        float dvdx = (float)(((bv - av) * acz - (cv - av) * abz) / determinant);
        float dvdz = (float)((abx * (cv - av) - acx * (bv - av)) / determinant);
        float sign = forward ? 1 : -1;
        return new PlowFeather(
            ax,
            az,
            -sign * dvdx * length,
            -sign * dvdz * length,
            sign * (tipV - av) * length,
            dudx * width,
            dudz * width,
            (au - (leftU + rightU) * .5f) * width,
            (rightU - leftU) * width * .5f,
            true
        );
    }

    public float Weight(float x, float z)
    {
        if (!_active)
            return 1;
        float behind = _surface
            ? _alongOffset + (x - _tipX) * _dx + (z - _tipZ) * _dz
            : (_tipX - x) * _dx + (_tipZ - z) * _dz;
        float lateral = _surface
            ? _sideOffset + (x - _tipX) * _nx + (z - _tipZ) * _nz
            : (x - _cx) * _nx + (z - _cz) * _nz;
        float side = _halfWidth > 0 ? _halfWidth - Math.Abs(lateral) : 1;
        float distance = Math.Min(behind, side);
        if (distance >= .65f)
            return 1;
        float width = .25f + .35f * SnowAppearance.Grain(x, z);
        float t = Math.Max(0, Math.Min(1, distance / width));
        return .12f + .88f * t * t * (3 - 2 * t);
    }
}
