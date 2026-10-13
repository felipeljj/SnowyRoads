using Game.Net;
using Game.Prefabs;

namespace RoadSnow.Mod;

// An exact snapshot, not a hash: a changed curve, node, prefab or streamed
// junction must invalidate the derived plan. Padding can only cause a cache miss.
internal struct SurfacePlanInput
{
    public EdgeGeometry Geometry;
    public Edge Edge;
    public Composition Composition;
    public StartNodeGeometry Start;
    public EndNodeGeometry End;
    public NetCompositionData EdgeData,
        StartData,
        EndData;
    public byte Flags;

    public unsafe bool SameAs(SurfacePlanInput other)
    {
        SurfacePlanInput current = this;
        byte* a = (byte*)&current;
        byte* b = (byte*)&other;
        // Both pointers refer to stack values of exactly sizeof(SurfacePlanInput).
        int length = sizeof(SurfacePlanInput);
        int i = 0;
        for (; i + sizeof(ulong) <= length; i += sizeof(ulong))
            if (*(ulong*)(a + i) != *(ulong*)(b + i))
                return false;
        for (; i < length; i++)
            if (a[i] != b[i])
                return false;
        return true;
    }
}
