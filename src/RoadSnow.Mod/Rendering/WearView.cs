using UnityEngine;

namespace RoadSnow.Mod;

internal static class WearView
{
    public static Vector4 LaneArea(Camera camera, Vector3 pivot)
    {
        Vector3 cameraPosition = camera.transform.position;
        float minimumSize =
            camera.orthographic || Mathf.Abs(camera.transform.forward.y) > .3f ? 256 : 384;
        float areaSize = Mathf.Clamp(
            camera.orthographic
                ? camera.orthographicSize * 4
                : Mathf.Abs(cameraPosition.y - pivot.y) * 3,
            minimumSize,
            2048
        );
        Vector2 offset = new Vector2(pivot.x - cameraPosition.x, pivot.z - cameraPosition.z);
        offset = Vector2.ClampMagnitude(offset, areaSize * .35f);
        return new Vector4(
            cameraPosition.x + offset.x - areaSize * .5f,
            cameraPosition.z + offset.y - areaSize * .5f,
            1 / areaSize,
            0
        );
    }

    public static float ImpressionPriority(Camera camera, Vector3 screen, float distanceSquared)
    {
        float centerDistanceSquared =
            (screen.x - .5f) * (screen.x - .5f) + (screen.y - .5f) * (screen.y - .5f);
        float horizontalViewWeight =
            1
            - Mathf.SmoothStep(
                0,
                1,
                Mathf.InverseLerp(.15f, .55f, Mathf.Abs(camera.transform.forward.y))
            );
        return centerDistanceSquared + horizontalViewWeight * Mathf.Sqrt(distanceSquared) * .015f;
    }
}
