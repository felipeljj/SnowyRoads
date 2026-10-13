using System.Globalization;
using UnityEngine;

namespace RoadSnow.Mod;

public partial class RoadSnowSystem
{
    private int _performanceCalls;
    private int _performanceDryCalls;
    private double _performanceTotal;
    private double _performanceMaximum;
    private float _performanceNext;
    private readonly System.Collections.Generic.List<double> _performanceSamples = new();
    private readonly double[] _stageTotals = new double[(int)PerformanceStage.Count];
    private readonly double[] _stageMaximums = new double[(int)PerformanceStage.Count];
    private readonly int[] _stageCalls = new int[(int)PerformanceStage.Count];

    private enum PerformanceStage
    {
        Setup,
        Coverage,
        Refresh,
        Create,
        Facilities,
        Terrain,
        Banks,
        Plow,
        Snow,
        LaneUpdate,
        Mesh,
        Agents,
        Discovery,
        Stamp,
        LaneDraw,
        FarMarks,
        FineMarks,
        Count,
    }

    private void RecordStage(PerformanceStage stage, double milliseconds)
    {
        int index = (int)stage;
        _stageCalls[index]++;
        _stageTotals[index] += milliseconds;
        _stageMaximums[index] = System.Math.Max(_stageMaximums[index], milliseconds);
    }

    private void RecordPerformance(double milliseconds)
    {
        _performanceCalls++;
        if (_dormant)
            _performanceDryCalls++;
        _performanceTotal += milliseconds;
        _performanceSamples.Add(milliseconds);
        _performanceMaximum = System.Math.Max(_performanceMaximum, milliseconds);
        float now = UnityEngine.Time.realtimeSinceStartup;
        if (_performanceNext == 0)
            _performanceNext = now + 20;
        if (now < _performanceNext)
            return;

        _performanceSamples.Sort();
        double p95 = _performanceSamples[
            (int)System.Math.Ceiling(_performanceSamples.Count * .95) - 1
        ];
        // Whole-update timings include discovery and footsteps, unlike tickMs.
        Mod.log.Info(
            string.Format(
                CultureInfo.InvariantCulture,
                "RoadSnow update window: calls={0}, dormant={1}, avgMs={2:F3}, maxMs={3:F3}, nativeSnow={4}, deposits={5}, modEnabled={6}, snowVisual={7}, patches={8}, p95Ms={9:F3}, profile=heavy10",
                _performanceCalls,
                _performanceDryCalls,
                _performanceTotal / _performanceCalls,
                _performanceMaximum,
                _nativeSnowPresent,
                _plowedSnow.Deposits.Count,
                Mod.Settings.Enabled,
                Game.Settings.SharedSettings.instance?.gameplay?.snowVisual ?? true,
                _patches.Count,
                p95
            )
        );
        var stages = new System.Text.StringBuilder(
            "RoadSnow CPU stages (ms/update; calls; maxMs): "
        );
        double accounted = 0;
        for (int i = 0; i < (int)PerformanceStage.Count; i++)
        {
            if (_stageCalls[i] == 0)
                continue;
            stages.AppendFormat(
                CultureInfo.InvariantCulture,
                "{0}={1:F3}/{2}/{3:F3}; ",
                (PerformanceStage)i,
                _stageTotals[i] / _performanceCalls,
                _stageCalls[i],
                _stageMaximums[i]
            );
            accounted += _stageTotals[i];
        }
        stages.AppendFormat(
            CultureInfo.InvariantCulture,
            "Other={0:F3}",
            System.Math.Max(0, _performanceTotal - accounted) / _performanceCalls
        );
        Mod.log.Info(stages.ToString());
        LogAtlasTimings("far", _distantMarks);
        LogAtlasTimings("fine", _trafficMarks);
        Mod.log.Info(
            string.Format(
                CultureInfo.InvariantCulture,
                "RoadSnow heavy6 work: budgetMs={0:F2}, deferred={1}, overdueForced={2}, maxLateMs={3:F1}, roadPlowTriangles={4}, plowFallbackSegments={5}, weatherHistogramReuses={6}, bankGroupReuses={7}",
                RoadSnow.Core.FrameWorkBudget.TargetMs,
                _work.Deferrals,
                _work.Forced,
                _work.MaximumLateness * 1000,
                _plowRoadTriangles,
                _plowFallbackSegments,
                _distantMarks?.WeatherOverviewReuses ?? 0,
                _snowBanks?.GroupReuses ?? 0
            )
        );
        _work.ResetMetrics();
        Mod.log.Info(
            $"RoadSnow coverage/LOD: cached={_cachedCoverage.Count}, remaining={_cachedCoverage.Remaining}, farPending={_distantMarks?.PendingUploadTiles ?? 0}, pending={_pending.Count - _pendingHead}"
        );
        _plowRoadTriangles = _plowFallbackSegments = 0;
        Mod.log.Info(
            $"RoadSnow section plows: mode=whole-section, completedPartsTotal={_sectionPlows}, cameraIndependent=True"
        );
        Mod.log.Info(
            $"RoadSnow immediate plow atlas: fineUploadsTotal={_trafficMarks?.ImmediatePlowUploads ?? 0}, farUploadsTotal={_distantMarks?.ImmediatePlowUploads ?? 0}"
        );
        Mod.log.Info(
            string.Format(
                CultureInfo.InvariantCulture,
                "RoadSnow snowfall/plows: snowing={0}, snowfallMm={1:F3}, activePlows={2}, sweptSegments={3}, sweptMeters={4:F2}, discontinuities={5}, precipitation={6:F3}, temperature={7:F2}",
                _climateSystem.isSnowing,
                _snowfall.TotalMm,
                _activePlows,
                _plowSegments,
                _plowMeters,
                _plowDiscontinuities,
                (float)_climateSystem.precipitation,
                (float)_climateSystem.temperature
            )
        );
        _plowSegments = _plowDiscontinuities = 0;
        _plowMeters = 0;
        var camera = _cameraSystem.activeCamera;
        Vector3 eye = camera != null ? camera.transform.position : default;
        Mod.log.Info(
            string.Format(
                CultureInfo.InvariantCulture,
                "RoadSnow work window: carsExamined={0}, ownerLookups={1}, ownerReuses={2}, projectionQueries={3}, projectionReuses={4}, plansBuilt={5}, plansReused={6}; terrainBuildsTotal={7}, terrainReusesTotal={8}, liveLanes={9}, restoredOnlyLanes={10}, simSpeed={11}, simFrame={12}, camera=({13:F2},{14:F2},{15:F2}), pixels={16}x{17}, fov={18:F2}",
                _carsExamined,
                _ownerLookups,
                _ownerReuses,
                _projectionQueries,
                _projectionReuses,
                _surfacePlanBuilds,
                _surfacePlanReuses,
                _terrainMarks?.GeometryBuilds ?? 0,
                _terrainMarks?.GeometryReuses ?? 0,
                (_laneWearMask?.Registered ?? 0) - (_laneWearMask?.RestoredOnly ?? 0),
                _laneWearMask?.RestoredOnly ?? 0,
                _simulationSystem.selectedSpeed,
                _simulationSystem.frameIndex,
                eye.x,
                eye.y,
                eye.z,
                camera != null ? camera.pixelWidth : 0,
                camera != null ? camera.pixelHeight : 0,
                camera != null ? camera.fieldOfView : 0
            )
        );
        _carsExamined = _ownerLookups = _ownerReuses = _projectionQueries = _projectionReuses = 0;
        _surfacePlanBuilds = _surfacePlanReuses = 0;
        System.Array.Clear(_stageTotals, 0, _stageTotals.Length);
        System.Array.Clear(_stageMaximums, 0, _stageMaximums.Length);
        System.Array.Clear(_stageCalls, 0, _stageCalls.Length);
        _performanceSamples.Clear();
        _performanceCalls = _performanceDryCalls = 0;
        _performanceTotal = _performanceMaximum = 0;
        _performanceNext = now + 20;
    }

    private void LogAtlasTimings(string name, TrafficMarks marks)
    {
        if (marks == null)
            return;
        double factor = 1000.0 / System.Diagnostics.Stopwatch.Frequency / _performanceCalls;
        Mod.log.Info(
            string.Format(
                CultureInfo.InvariantCulture,
                "RoadSnow {0} atlas CPU (ms/update): historyRead={1:F4}, reduction={2:F4}, textureCopy={3:F4}, atlasApply={4:F4}, pageUpload={5:F4}, overviewBuildsTotal={6}, overviewReusesTotal={7}, blocksBuiltTotal={8}, blocksRetainedTotal={9}, weatherOnlyReusesTotal={10}, uploadedMiBTotal={11:F3}, partialUploads={12}",
                name,
                marks.HistoryReadTicks * factor,
                marks.ReductionTicks * factor,
                marks.TextureCopyTicks * factor,
                marks.AtlasApplyTicks * factor,
                marks.PageUploadTicks * factor,
                marks.OverviewBuilds,
                marks.OverviewReuses,
                marks.OverviewBlocksBuilt,
                marks.OverviewBlocksRetained,
                marks.WeatherOnlyReuses,
                marks.UploadedBytes / 1048576.0,
                marks.PartialUploads
            )
        );
        marks.ResetTimings();
    }
}
