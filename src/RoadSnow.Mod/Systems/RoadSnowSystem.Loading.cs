using System.Diagnostics;
using UnityEngine;

namespace RoadSnow.Mod;

public partial class RoadSnowSystem
{
    private bool _coveragePrepared;

    private void CompleteCoverageReplacements()
    {
        // Upload the live atlas before hiding the corresponding saved coverage.
        foreach (var center in _coverageReplacements)
            _cachedCoverage.Hide(center);
        _coverageReplacements.Clear();
    }

    private void PrepareLoadingCoverage()
    {
        long started = Stopwatch.GetTimestamp();
        _surfaceOffsetCm = Mod.Settings.SurfaceOffsetCm;
        var camera = _cameraSystem.activeCamera;
        if (camera == null || !EnsureMaterial())
            return;
        float now = UnityEngine.Time.realtimeSinceStartup;
        using (var scan = RefreshPatches(camera, now).GetEnumerator())
            while (scan.MoveNext()) { }
        // Revalidate format-6 coverage: an earlier build could report omissions
        // from float identity differences even when the complete mesh was saved.
        if (_cachedCoverage.CoversAll(_coverageCenters))
            _coverageNeedsBootstrap = false;
        if (_coverageNeedsBootstrap)
        {
            // Legacy saves contain a truncated mesh cache. Repair once during
            // loading, then the next save carries the complete coverage.
            while (_pendingHead < _pending.Count)
                CreatePending(now, true);
            _atlas.Flush();
            CompleteCoverageReplacements();
            _nextRefresh = UnityEngine.Time.realtimeSinceStartup + 3;
        }
        _cachedCoverage.Prepare(_material, _cacheShader);
        _cachedCoverage.SetVisible(false);
        Mod.log.Info(
            $"RoadSnow loading coverage: legacyRepair={_coverageNeedsBootstrap}, cached={_cachedCoverage.Count}, live={_patches.Count}, milliseconds={Milliseconds(started):F1}"
        );
        _coverageNeedsBootstrap = false;
        _coveragePrepared = true;
    }

    private void EnsurePlowGeometry(Unity.Entities.Entity owner, float now)
    {
        if (_surfacePlans.TryGetValue(owner, out var plan))
            EnsurePlan(plan, now);
        else if (EntityManager.HasBuffer<Game.Net.ConnectedEdge>(owner))
            foreach (var edge in EntityManager.GetBuffer<Game.Net.ConnectedEdge>(owner, true))
                if (_surfacePlans.TryGetValue(edge.m_Edge, out plan))
                    EnsurePlan(plan, now);
    }

    private void EnsurePlan(SurfacePlan plan, float now)
    {
        foreach (var request in plan.Requests)
        {
            if (!_desired.Contains(request.Key))
                continue;
            if (!_patches.TryGetValue(request.Key, out var patch))
            {
                CreatePatch(request, now, _terrainSystem.GetHeightData());
                _queued.Remove(request.Key);
                patch = _patches[request.Key];
            }
            if (patch.NeedsSeed && (_isPreviewActive || _snowSnapshot != null))
            {
                float target = _isPreviewActive
                    ? 15
                    : patch.Observation.Observe(SampleSnow(patch.Center), _snowSnapshotVersion);
                patch.FollowSnow(
                    target,
                    0,
                    _isPreviewActive || _climateSystem.isSnowing,
                    0,
                    Mod.Settings.ResidualMeltHours,
                    _climateSystem.temperature
                );
                patch.Upload(true);
                _residualHistory[patch.Center] = patch.ResidualDepth;
            }
        }
    }
}
