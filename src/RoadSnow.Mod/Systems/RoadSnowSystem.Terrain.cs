using Game.Simulation;
using UnityEngine;

namespace RoadSnow.Mod;

public partial class RoadSnowSystem
{
    private TerrainMarks _terrainMarks;
    private TerrainHeightData _terrainHeights;

    private bool TryTerrainSurface(Vector3 position, out Vector3 surface)
    {
        surface = default;
        if (!_terrainHeights.isCreated)
            return false;
        float height = TerrainUtils.SampleHeight(ref _terrainHeights, position);
        if (
            Mathf.Abs(position.y - height) > .6f
            || (!_isPreviewActive && SampleSnow(position) < .2f)
        )
            return false;
        surface = new Vector3(position.x, height + .035f, position.z);
        return true;
    }
}
