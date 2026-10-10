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
        (data.m_State & CompositionState.Airspace) == 0
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
            if (node.m_MiddleRadius > 0)
            {
                // Round nodes use two side segments around the middle curve. Keep this
                // mapping tied to the node geometry rather than drawing a straight fan.
                segment = along <= 0.5f ? node.m_Left : node.m_Right;
                float middleT = along <= 0.5f ? along * 1.98f : 0.99f + (along - 0.5f) * 0.02f;
                along = along <= 0.5f ? along * 2 : along * 2 - 1;
                float3 middle = MathUtils.Position(node.m_Middle, middleT);
                return across <= middleAcross
                    ? math.lerp(
                        MathUtils.Position(segment.m_Left, along),
                        middle,
                        across / middleAcross
                    )
                    : math.lerp(
                        middle,
                        MathUtils.Position(segment.m_Right, along),
                        (across - middleAcross) / (1 - middleAcross)
                    );
            }
            else
            {
                middleAcross = math.clamp(middleAcross, 0.01f, 0.99f);
                float leftSeam = middleAcross * 0.5f;
                float rightSeam = (1 + middleAcross) * 0.5f;
                float3 middle = MathUtils.Position(node.m_Middle, along);
                if (across < leftSeam)
                {
                    return math.lerp(
                        MathUtils.Position(node.m_Left.m_Left, along),
                        MathUtils.Position(node.m_Left.m_Right, along),
                        across / leftSeam
                    );
                }

                if (across < middleAcross)
                {
                    return math.lerp(
                        MathUtils.Position(node.m_Left.m_Right, along),
                        middle,
                        (across - leftSeam) / (middleAcross - leftSeam)
                    );
                }

                if (across < rightSeam)
                {
                    return math.lerp(
                        middle,
                        MathUtils.Position(node.m_Right.m_Left, along),
                        (across - middleAcross) / (rightSeam - middleAcross)
                    );
                }

                return math.lerp(
                    MathUtils.Position(node.m_Right.m_Left, along),
                    MathUtils.Position(node.m_Right.m_Right, along),
                    (across - rightSeam) / (1 - rightSeam)
                );
            }
        }

        return math.lerp(
            MathUtils.Position(segment.m_Left, along),
            MathUtils.Position(segment.m_Right, along),
            across
        );
    }
}
