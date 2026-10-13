using UnityEngine;

namespace RoadSnow.Mod;

internal static class WearView
{
    // Capture native camera properties once per scan, not once per history tile.
    internal readonly struct Snapshot
    {
        public readonly Matrix4x4 Matrix;
        public readonly Vector3 Eye;
        private readonly Vector3 _forward;
        private readonly float _pixelScale;
        private readonly float _horizontalWeight;
        private readonly bool _orthographic;

        public Snapshot(Camera camera)
            : this(
                camera.projectionMatrix * camera.worldToCameraMatrix,
                camera.transform.position,
                camera.transform.forward,
                camera.orthographic,
                (
                    camera.orthographic
                        ? camera.orthographicSize * 2
                        : 2 * Mathf.Tan(camera.fieldOfView * Mathf.Deg2Rad * .5f)
                ) / Mathf.Max(1, camera.pixelHeight)
            ) { }

        public Snapshot(
            Matrix4x4 matrix,
            Vector3 eye,
            Vector3 forward,
            bool orthographic,
            float pixelScale
        )
        {
            Matrix = matrix;
            Eye = eye;
            _forward = forward;
            _orthographic = orthographic;
            _pixelScale = pixelScale;
            _horizontalWeight =
                1 - Mathf.SmoothStep(0, 1, Mathf.InverseLerp(.15f, .55f, Mathf.Abs(_forward.y)));
        }

        public Vector3 Viewport(Vector3 point)
        {
            Vector4 clip = Matrix * new Vector4(point.x, point.y, point.z, 1);
            float depth = Vector3.Dot(point - Eye, _forward);
            if (Mathf.Abs(clip.w) < .000001f)
                return new Vector3(0, 0, -1);
            return new Vector3(.5f + .5f * clip.x / clip.w, .5f + .5f * clip.y / clip.w, depth);
        }

        public float PixelSize(float distanceSquared) =>
            _pixelScale * (_orthographic ? 1 : Mathf.Sqrt(distanceSquared));

        public float Priority(Vector3 screen, float distanceSquared) =>
            (screen.x - .5f) * (screen.x - .5f)
            + (screen.y - .5f) * (screen.y - .5f)
            + _horizontalWeight * Mathf.Sqrt(distanceSquared) * .015f;

        public bool Equals(Snapshot other) =>
            Matrix.Equals(other.Matrix)
            && Eye.Equals(other.Eye)
            && _forward.Equals(other._forward)
            && _pixelScale.Equals(other._pixelScale)
            && _horizontalWeight.Equals(other._horizontalWeight)
            && _orthographic == other._orthographic;
    }

    public static Vector4 DetailLaneArea(Camera camera, Vector3 pivot)
    {
        var eye = camera.transform.position;
        var offset = Vector2.ClampMagnitude(
            new Vector2(pivot.x - eye.x, pivot.z - eye.z),
            128 * .35f
        );
        return StableArea(eye.x + offset.x, eye.z + offset.y, 128, 0);
    }

    public static Vector4 DistantLaneArea(Camera camera, Vector3 pivot, float previousSize = 0)
    {
        Vector3 eye = camera.transform.position;
        const float size = 4096;
        Vector2 offset = Vector2.ClampMagnitude(
            new Vector2(pivot.x - eye.x, pivot.z - eye.z),
            size * .3f
        );
        return StableArea(eye.x + offset.x, eye.z + offset.y, size, 0);
    }

    public static Vector4 LaneArea(Camera camera, Vector3 pivot, float previousSize = 0)
    {
        Vector3 cameraPosition = camera.transform.position;
        // A fixed world texel size keeps the mask from changing under stationary
        // roads when zoom/tilt changes. The outer raster handles the distant view.
        const float areaSize = 512;
        Vector2 offset = new Vector2(pivot.x - cameraPosition.x, pivot.z - cameraPosition.z);
        offset = Vector2.ClampMagnitude(offset, areaSize * .35f);
        return StableArea(cameraPosition.x + offset.x, cameraPosition.z + offset.y, areaSize, 0);
    }

    private static Vector4 StableArea(float x, float z, float size, float previousSize)
    {
        // Avoid alternating resolutions while the camera hovers around a threshold.
        size =
            previousSize > 0 && size >= previousSize * .45f && size <= previousSize * 1.05f
                ? previousSize
                : Mathf.NextPowerOfTwo(Mathf.CeilToInt(size));
        float texel = size / LaneRaster.Size;
        return new Vector4(
            Mathf.Floor((x - size * .5f) / texel) * texel,
            Mathf.Floor((z - size * .5f) / texel) * texel,
            1 / size,
            0
        );
    }
}
