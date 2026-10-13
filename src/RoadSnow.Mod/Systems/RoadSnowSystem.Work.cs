using System.Diagnostics;
using Game.Simulation;
using RoadSnow.Core;
using UnityEngine;

namespace RoadSnow.Mod;

public partial class RoadSnowSystem
{
    private readonly FrameWorkBudget _work = new(
        0,
        0,
        .05,
        .5,
        0,
        .35,
        0,
        0,
        1,
        0,
        .075,
        0,
        .25,
        .25,
        .5,
        .25,
        .10,
        .125
    );
    private readonly uint[] _workFrames = new uint[18];

    private void RunScheduledWork(Camera camera, float now, long updateStarted)
    {
        // Sampling plows is urgent: never drop a driven interval to fit a budget.
        _agentsMs = _marksMs = 0;
        _trafficOwners.Clear();
        _trafficMarks.SetWeather(
            p => _isPreviewActive ? 15 : SampleSnow(p),
            _snowSnapshotVersion,
            _climateSystem.isSnowing && !_isPreviewActive,
            _snowfall.TotalMm
        );
        if (now >= _nextFootSample && _simulationSystem.frameIndex != _footFrame)
        {
            _footFrame = _simulationSystem.frameIndex;
            _nextFootSample = now + .075f;
            long started = Stopwatch.GetTimestamp();
            StampSnowplows(now);
            _agentsMs = Milliseconds(started);
            RecordStage(PerformanceStage.Agents, _agentsMs);
            _wearHistory.DrainPlowChanges(_plowTileChanges);
            started = Stopwatch.GetTimestamp();
            _trafficMarks.RefreshPlowTiles(_plowTileChanges);
            RecordStage(PerformanceStage.FineMarks, Milliseconds(started));
            started = Stopwatch.GetTimestamp();
            _distantMarks.RefreshPlowTiles(_plowTileChanges);
            RecordStage(PerformanceStage.FarMarks, Milliseconds(started));
        }

        // Retained draw meshes still submit on EVERY frame, even when preparation
        // is deferred. GameObject road/bank renderers remain visible themselves.
        long drawStarted = Stopwatch.GetTimestamp();
        _terrainMarks?.Draw(camera, _material, now, false);
        RecordStage(PerformanceStage.Terrain, Milliseconds(drawStarted));
        drawStarted = Stopwatch.GetTimestamp();
        _plowedSnow.Draw(camera, _material, _plowShader, Mod.Settings.RaisedSnow, now, false);
        RecordStage(PerformanceStage.Plow, Milliseconds(drawStarted));

        _work.Begin(now);
        // Admit resumable slices by their bounded entry cost, not a whole prior
        // batch. Otherwise a slow batch runs only on the overdue escape hatch.
        _work.SetAdmissionCeiling(0, .4);
        _work.SetAdmissionCeiling(1, .6);
        int job;
        while ((job = _work.Next(Milliseconds(updateStarted))) >= 0)
        {
            long started = Stopwatch.GetTimestamp();
            PerformanceStage stage = PerformanceStage.Setup;
            bool weather = _snowSnapshot != null || _isPreviewActive;
            uint frame = _simulationSystem.frameIndex;
            bool advanced = frame != _workFrames[job];
            _workFrames[job] = frame;
            switch (job)
            {
                case 0:
                    stage = PerformanceStage.Refresh;
                    if (_refreshScan == null && now >= _nextRefresh)
                        _refreshScan = RefreshPatches(camera, now).GetEnumerator();
                    int scanned = 0;
                    while (_refreshScan != null && scanned++ < 128 && Milliseconds(started) < .4)
                    {
                        if (_refreshScan.MoveNext())
                            continue;
                        _refreshScan.Dispose();
                        _refreshScan = null;
                        _nextRefresh = now + 3;
                    }
                    _lastRefreshMs = Milliseconds(started);
                    break;
                case 1:
                    stage = PerformanceStage.Create;
                    CreatePending(now);
                    _createMs = _lastCreateMs;
                    break;
                case 2:
                    stage = PerformanceStage.Facilities;
                    _facilities?.Update(camera, now);
                    break;
                case 3:
                    stage = PerformanceStage.Terrain;
                    _terrainMarks?.Draw(camera, _material, now, true, false);
                    break;
                case 4:
                    stage = PerformanceStage.Banks;
                    _snowBanks?.Update(
                        camera,
                        _activePatches,
                        now,
                        Mod.Settings.RaisedSnow,
                        Mod.Settings.BankDetail,
                        _cameraSystem.activeCameraController?.pivot,
                        Mod.Settings.RealisticSnow
                    );
                    break;
                case 5:
                    stage = PerformanceStage.Plow;
                    _plowedSnow.Draw(
                        camera,
                        _material,
                        _plowShader,
                        Mod.Settings.RaisedSnow,
                        now,
                        true,
                        false
                    );
                    break;
                case 6:
                    stage = PerformanceStage.Snow;
                    UpdateSnowBudget(now);
                    break;
                case 7:
                    stage = PerformanceStage.LaneUpdate;
                    if (weather)
                        _laneWearMask.Update(
                            EntityManager,
                            frame,
                            24f / TimeSystem.kTicksPerDay,
                            _climateSystem.isSnowing && !_isPreviewActive,
                            Mathf.Clamp01(Mod.Settings.TrafficInfluence / 100f),
                            p => _isPreviewActive ? 15 : SampleSnow(p),
                            _snowfall.TotalMm
                        );
                    _laneUpdateMs = Milliseconds(started);
                    break;
                case 8:
                    stage = PerformanceStage.Discovery;
                    if (advanced)
                        DiscoverTraffic();
                    _discoveryMs = Milliseconds(started);
                    break;
                case 9:
                    stage = PerformanceStage.Stamp;
                    if (advanced)
                        StampTraffic();
                    _stampMs = Milliseconds(started);
                    break;
                case 10:
                    stage = PerformanceStage.Agents;
                    if (advanced)
                        StampTrains();
                    break;
                case 11:
                    stage = PerformanceStage.Agents;
                    if (advanced)
                        StampPedestrians();
                    break;
                case 12:
                case 13:
                case 14:
                    stage = PerformanceStage.LaneDraw;
                    _laneWearMask.Draw(
                        camera,
                        Mod.Settings.TrafficInfluence,
                        _cameraSystem.activeCameraController?.pivot,
                        job - 12
                    );
                    _laneDrawMs = Milliseconds(started);
                    break;
                case 15:
                case 16:
                    stage = job == 15 ? PerformanceStage.FineMarks : PerformanceStage.FarMarks;
                    if (weather)
                        (job == 15 ? _trafficMarks : _distantMarks).Draw(
                            camera,
                            job == 15
                                ? Mathf.Clamp(Mod.Settings.MaximumPatches, 16, 128) * 32
                                : 32768,
                            CurrentSnowColor,
                            p => _isPreviewActive ? 15 : SampleSnow(p),
                            _snowSnapshotVersion,
                            _climateSystem.isSnowing && !_isPreviewActive,
                            _snowfall.TotalMm
                        );
                    break;
                case 17:
                    stage = PerformanceStage.Mesh;
                    _atlas.Flush();
                    CompleteCoverageReplacements();
                    _atlasMs = Milliseconds(started);
                    break;
            }
            double cost = Milliseconds(started);
            RecordStage(stage, cost);
            if (stage == PerformanceStage.Agents)
                _agentsMs += cost;
            if (
                stage == PerformanceStage.LaneDraw
                || stage == PerformanceStage.FineMarks
                || stage == PerformanceStage.FarMarks
            )
                _marksMs += cost;
            _work.Finish(job, cost);
        }
    }
}
