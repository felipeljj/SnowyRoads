using System;
using RoadSnow.Core;
using UnityEngine;
using UnityEngine.Rendering;

namespace RoadSnow.Mod;

public partial class RoadSnowSystem
{
    private bool _nativeSnowPresent = true;
    private bool _dormant;
    private readonly SnowActivity _snowActivity = new();

    private bool SuspendDryScene()
    {
        bool residual = _plowedSnow.Deposits.Count > 0;
        if (_snowActivity.Active && !_nativeSnowPresent && !residual)
        {
            foreach (float depth in _residualHistory.Values)
            {
                if (depth > 0.125f)
                {
                    residual = true;
                    break;
                }
            }
        }
        // Season names come from the base season prefab, not the localized UI.
        // Outside winter a snowfall must start the cycle; existing snow then thaws normally.
        bool active = _snowActivity.Update(
            string.Equals(
                _climateSystem.currentSeasonName,
                "Winter",
                StringComparison.OrdinalIgnoreCase
            ),
            _climateSystem.isSnowing,
            _snowSnapshot != null,
            _nativeSnowPresent,
            residual
        );
        bool dry = !_isPreviewActive && !active;
        if (dry != _dormant)
        {
            _dormant = dry;
            _atlas?.SetVisible(!dry);
            _cachedCoverage.SetVisible(!dry);
            _facilities?.SetVisible(!dry);
            _snowBanks?.Invalidate();
            _vehicleHistory.Clear();
            _pedestrianHistory.Clear();
            _plowHistory.Clear();
            _trainHistory.Clear();
            if (dry)
            {
                _sectionPlowHistory.Clear();
                _completedSectionOwners.Clear();
                _cachedCoverage.ResetSectionPlows();
                foreach (var patch in _activePatches)
                    patch.RestoreSectionPlow(-1, _snowfall.TotalMm);
                _wearHistory.Clear();
                foreach (var lane in _laneHistory.Values)
                    lane.Advance(0, 0, 0, false, 0, _snowfall.TotalMm);
            }
            if (!dry)
                _nextRefresh = 0;
        }
        return dry;
    }

    private void RequestSnowSnapshot(float now)
    {
        _nextReadback = now + (_dormant && !_climateSystem.isSnowing ? 3f : 0.5f);
        var texture = _snowSystem.SnowDepth;
        if (_readbackPending || texture == null || !SystemInfo.supportsAsyncGPUReadback)
        {
            return;
        }

        _readbackPending = true;
        //readback can finish after the city has been unloaded. only accept results
        // from the current generation or old snow data can leak into the next save.
        int generation = _generation;
        int width = texture.width;
        int height = texture.height;
        Vector4 mapping = _terrainSystem.mapOffsetScale;
        AsyncGPUReadback.Request(
            texture,
            0,
            request =>
            {
                if (generation != _generation || !_isCityLoaded)
                {
                    return;
                }

                _readbackPending = false;
                if (request.hasError)
                {
                    Mod.log.Warn("Native snow snapshot failed.");
                    return;
                }

                var values = request.GetData<ushort>();
                if (values.Length != width * height * 2)
                {
                    Mod.log.Warn("Unexpected native snow texture layout.");
                    return;
                }

                if (_snowSnapshot == null || _snowSnapshot.Length != values.Length)
                {
                    _snowSnapshot = new ushort[values.Length];
                }

                values.CopyTo(_snowSnapshot);
                _nativeSnowPresent = false;
                for (int i = 0; i < _snowSnapshot.Length; i += 2)
                {
                    // Below 0.125 mm no overlay is visible. Encoded depth is quadratic;
                    // tiny nonzero texels must not keep an otherwise dry city awake.
                    if (_snowSnapshot[i] >= 260)
                    {
                        _nativeSnowPresent = true;
                        break;
                    }
                }
                _snowSnapshotWidth = width;
                _snowSnapshotHeight = height;
                _snowSnapshotMapping = mapping;
                _snowSnapshotVersion++;
            }
        );
    }

    private float SampleSnow(Vector3 position)
    {
        if (_snowSnapshot == null)
        {
            return 0;
        }

        float u = (position.x + _snowSnapshotMapping.x) * _snowSnapshotMapping.z;
        float v = (position.z + _snowSnapshotMapping.y) * _snowSnapshotMapping.w;
        if (u < 0 || u > 1 || v < 0 || v > 1)
        {
            return 0;
        }

        float px = Mathf.Clamp(u * _snowSnapshotWidth - 0.5f, 0, _snowSnapshotWidth - 1);
        float pz = Mathf.Clamp(v * _snowSnapshotHeight - 0.5f, 0, _snowSnapshotHeight - 1);
        int x = (int)px;
        int z = (int)pz;
        int nextX = Math.Min(x + 1, _snowSnapshotWidth - 1);
        int nextZ = Math.Min(z + 1, _snowSnapshotHeight - 1);
        // snowDepth stores two ushort channels per texel. the first is the encoded depth,
        // interpolate it before converting to millimeters, matching the shader sampling
        float a = Mathf.Lerp(
            _snowSnapshot[(z * _snowSnapshotWidth + x) * 2],
            _snowSnapshot[(z * _snowSnapshotWidth + nextX) * 2],
            px - x
        );
        float b = Mathf.Lerp(
            _snowSnapshot[(nextZ * _snowSnapshotWidth + x) * 2],
            _snowSnapshot[(nextZ * _snowSnapshotWidth + nextX) * 2],
            px - x
        );
        return NativeSnowEncoding.DepthMm(Mathf.Lerp(a, b, pz - z) / 65535f);
    }
}
