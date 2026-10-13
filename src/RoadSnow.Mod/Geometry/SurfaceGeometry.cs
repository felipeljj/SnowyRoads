using Colossal.Mathematics;
using Game.Net;
using Game.Prefabs;
using Unity.Mathematics;

namespace RoadSnow.Mod;

internal static class SurfaceGeometry
{
    // Airport airspace uses network curves too, but those curves are not pavement.
    // Covering them produces the giant floating snow sheets this filter avoids.
    public static bool SupportsSnow(NetCompositionData data, TaxiwayFlags taxiway = 0) =>
        (data.m_State & (CompositionState.Airspace | CompositionState.Hidden)) == 0
        && (data.m_State & CompositionState.HasSurface) != 0
        && (taxiway & TaxiwayFlags.Airspace) == 0
        && (
            data.m_Flags.m_General
            & (CompositionFlags.General.Tunnel | CompositionFlags.General.Inside)
        ) == 0
        && data.m_Width >= 1
        && data.m_Width <= 100;

    public static float2 PlanarUV(float3 position, float3 minimum, float3 size) =>
        (position.xz - minimum.xz) / math.max(size.xz, new float2(0.01f));

    public static float3 Position(
        Segment segment,
        EdgeNodeGeometry? geometry,
        float middleAcross,
        float across,
        float along
    )
    {
        if (geometry.HasValue)
        {
            var node = geometry.Value;
            middleAcross = math.clamp(middleAcross, .01f, .99f);
            // A single cross-section cannot address the central fan. Mesh generation
            // uses JunctionSurface; sampling the middle follows the native center curve.
            if (across == middleAcross && node.m_MiddleRadius <= 0)
                return MathUtils.Position(node.m_Middle, along);
            return across < middleAcross
                ? JunctionSurface.Point(node, 0, across / middleAcross, along)
                : JunctionSurface.Point(
                    node,
                    3,
                    (across - middleAcross) / (1 - middleAcross),
                    along
                );
        }

        return math.lerp(
            MathUtils.Position(segment.m_Left, along),
            MathUtils.Position(segment.m_Right, along),
            across
        );
    }
}
