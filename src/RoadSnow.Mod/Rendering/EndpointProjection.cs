using Unity.Entities;
using UnityEngine;

namespace RoadSnow.Mod;

// One path only: roads and facilities cannot change during its synchronous stamp.
// Keep failed projection results, never the fallback height of a previous segment.
internal struct EndpointProjection
{
    private bool _ready;
    private Entity _owner;
    private Vector3 _position;
    private Vector3 _surface;
    private bool _found;

    public bool TryGet(Entity owner, Vector3 position, out Vector3 surface, out bool found)
    {
        surface = _surface;
        found = _found;
        return _ready && _owner == owner && _position.Equals(position);
    }

    public void Store(Entity owner, Vector3 position, Vector3 surface, bool found)
    {
        _ready = true;
        _owner = owner;
        _position = position;
        _surface = surface;
        _found = found;
    }
}
