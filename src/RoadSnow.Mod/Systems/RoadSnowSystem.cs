using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Colossal.Serialization.Entities;
using Game;
using Game.Common;
using Game.Creatures;
using Game.Net;
using Game.Rendering;
using Game.Settings;
using Game.Simulation;
using Game.Tools;
using Game.Vehicles;
using RoadSnow.Core;
using Unity.Entities;
using UnityEngine;
using UnityEngine.Scripting;
using MaintenanceVehicle = Game.Vehicles.MaintenanceVehicle;

namespace RoadSnow.Mod;

public partial class RoadSnowSystem : GameSystemBase, IDefaultSerializable
{
    // this file owns the lifecycle and update order. traffic, surfaces, weather and rendering
    // live in the other partial files, but still share this system's state
    private ClimateSystem _climateSystem;
    private SimulationSystem _simulationSystem;
    private CameraUpdateSystem _cameraSystem;
    private SnowSystem _snowSystem;
    private TerrainSystem _terrainSystem;
    private EntityQuery _roadQuery;
    private EntityQuery _vehicleQuery;
    private EntityQuery _pedestrianQuery;
    private EntityQuery _snowplowQuery;
    private EntityQuery _trainQuery;
    private Material _material;
    private AssetBundle _shaderBundle;
    private Shader _surfaceShader;
    private SnowAtlas _atlas;
    private TrafficMarks _trafficMarks;
    private TrafficMarks _distantMarks;
    private LaneWearMask _laneWearMask;
    private SnowBanks _snowBanks;
    private FacilitySurfaces _facilities;
    private readonly WearHistory _wearHistory = new();
    private Color _gameSnowColor = new Color(235 / 255f, 239 / 255f, 242 / 255f);
    private bool _colorFound;
    private float _nextColorProbe;
    private Color CurrentSnowColor =>
        Mod.Settings.UseGameSnowColor
            ? _gameSnowColor
            : new Color(
                Mathf.Clamp01(Mod.Settings.SnowRed / 255f),
                Mathf.Clamp01(Mod.Settings.SnowGreen / 255f),
                Mathf.Clamp01(Mod.Settings.SnowBlue / 255f)
            );

    private bool _isCityLoaded;
    private bool _isPreviewActive;
    private bool _shaderFailed;
    private bool _readbackPending;
    private int _generation;
    private int _surfaceOffsetCm;
    private int _snowSnapshotWidth;
    private int _snowSnapshotHeight;
    private int _scanOffset;
    private int _snowSnapshotVersion;
    private int _snowCursor;
    private ushort[] _snowSnapshot;
    private Vector4 _snowSnapshotMapping;
    private float _nextRefresh;
    private float _nextReadback;
    private float _previewStarted;
    private float _nextMetrics;
    private double _lastTickMs;
    private double _lastRefreshMs;
    private double _snowBudgetMs;
    private double _createMs;
    private double _atlasMs;
    private int _nodeParts;
    private int _uploadedParts;
    private int _visibleNetworks;
    private int _detailedParts;
    private int _carsStamped;
    private int _footprintsStamped;
    private uint _weatherFrame;
    private readonly SnowfallClock _snowfall = new();
    private readonly Dictionary<PatchKey, RoadPatch> _patches = new();
    private readonly Dictionary<Entity, List<RoadPatch>> _patchesByRoad = new();
    private readonly Dictionary<Entity, WheelSample> _vehicleHistory = new();
    private readonly Dictionary<Entity, Entity> _trafficOwners = new();
    private long _ownerLookups,
        _ownerReuses,
        _projectionQueries,
        _projectionReuses,
        _carsExamined;
    private readonly Dictionary<Entity, PedestrianSample> _pedestrianHistory = new();
    private readonly LruCache<(Entity, Entity), List<SurfaceStrip>> _surfaceCache = new(8192);
    private readonly Dictionary<Entity, HashSet<PatchKey>> _desiredByEdge = new();
    private readonly Dictionary<Entity, List<PatchKey>> _keysByEdge = new();
    private readonly HashSet<PatchKey> _desired = new();
    private readonly List<PatchRequest> _pending = new();
    private int _pendingHead;
    private readonly HashSet<PatchKey> _queued = new();
    private readonly List<Entity> _trafficKeys = new();
    private readonly List<RoadPatch> _activePatches = new();
    private readonly HashSet<RoadPatch> _touched = new();
    private int _peopleScanOffset;
    private float _diagnosticsAfter;
    private IEnumerator<bool> _refreshScan;
    private float _nextFootSample;
    private uint _footFrame;
    private int _carCursor;
    private double _stampMs;
    private double _marksMs;

    [Preserve]
    protected override void OnCreate()
    {
        base.OnCreate();
        _climateSystem = World.GetOrCreateSystemManaged<ClimateSystem>();
        _simulationSystem = World.GetOrCreateSystemManaged<SimulationSystem>();
        _cameraSystem = World.GetOrCreateSystemManaged<CameraUpdateSystem>();
        _snowSystem = World.GetOrCreateSystemManaged<SnowSystem>();
        _terrainSystem = World.GetOrCreateSystemManaged<TerrainSystem>();
        _roadQuery = GetEntityQuery(
            new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Edge>(),
                    ComponentType.ReadOnly<EdgeGeometry>(),
                    ComponentType.ReadOnly<Composition>(),
                },
                None = new[] { ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Temp>() },
            }
        );
        _vehicleQuery = GetEntityQuery(
            new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<CarCurrentLane>(),
                    ComponentType.ReadOnly<Game.Objects.Transform>(),
                },
                None = new[] { ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Temp>() },
            }
        );
        _snowplowQuery = GetEntityQuery(
            new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<MaintenanceVehicle>(),
                    ComponentType.ReadOnly<CarCurrentLane>(),
                    ComponentType.ReadOnly<Game.Objects.Transform>(),
                },
                None = new[] { ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Temp>() },
            }
        );
        _pedestrianQuery = GetEntityQuery(
            new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<HumanCurrentLane>(),
                    ComponentType.ReadOnly<Game.Objects.Transform>(),
                },
                None = new[] { ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Temp>() },
            }
        );
        _trainQuery = GetEntityQuery(
            new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<TrainCurrentLane>(),
                    ComponentType.ReadOnly<Game.Objects.Transform>(),
                },
                None = new[] { ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Temp>() },
            }
        );
    }

    protected override void OnGamePreload(Purpose purpose, GameMode mode)
    {
        _isCityLoaded = false;
        ReleaseOverlays(false);
        _plowedSnow.Deposits.Clear();
        _wearHistory.Clear();
        _laneHistory.Clear();
        _laneLayout.Clear();
        _residualHistory.Clear();
        _cachedCoverage.Clear();
        _snowfall.Reset();
        _snowActivity.Restore(false);
    }

    protected override void OnGameLoadingComplete(Purpose purpose, GameMode mode)
    {
        _isCityLoaded = mode == GameMode.Game;
        _weatherFrame = _simulationSystem.frameIndex;
        _nextRefresh = _nextReadback = 0;
        Mod.RequestDiagnostics = true;
        _diagnosticsAfter = UnityEngine.Time.realtimeSinceStartup + 10;
        if (_isCityLoaded && Mod.Settings?.Enabled == true)
            PrepareLoadingCoverage();
    }

    [Preserve]
    protected override void OnUpdate()
    {
        if (!_isCityLoaded || Mod.Settings == null)
            return;
        long started = Stopwatch.GetTimestamp();
        try
        {
            UpdateSnow();
        }
        finally
        {
            RecordPerformance(Milliseconds(started));
        }
    }

    private void UpdateSnow()
    {
        long setupStarted = Stopwatch.GetTimestamp();
        if (!_isCityLoaded || Mod.Settings == null)
        {
            return;
        }

        // weather follows simulation time. rrendering budgets use real time so game speed
        // does not multiply the amount of work we do in one frame
        uint weatherElapsed = unchecked(_simulationSystem.frameIndex - _weatherFrame);
        _weatherFrame = _simulationSystem.frameIndex;
        if (weatherElapsed <= TimeSystem.kTicksPerDay)
        {
            _plowedSnow.Melt(
                weatherElapsed * (24f / TimeSystem.kTicksPerDay),
                _climateSystem.temperature,
                Mod.Settings.ResidualMeltHours
            );
            _snowfall.Advance(
                weatherElapsed * (24f / TimeSystem.kTicksPerDay),
                _climateSystem.isSnowing && !Mod.Settings.PreviewSnow,
                (float)_climateSystem.precipitation
            );
        }

        if (
            Mod.RequestDiagnostics
            && _patches.Count > 0
            && UnityEngine.Time.realtimeSinceStartup >= _diagnosticsAfter
        )
        {
            Mod.RequestDiagnostics = false;
            try
            {
                RenderingDiagnostics.Write(
                    _climateSystem,
                    _snowSystem,
                    _roadQuery.CalculateEntityCount(),
                    _patches.Count,
                    _isPreviewActive,
                    _visibleNetworks,
                    _detailedParts,
                    _carsStamped,
                    _footprintsStamped,
                    _snowSnapshot != null,
                    $"banks + native facility floors + organic lanes; visible wear tiles={_trafficMarks?.Drawn}; stored wear tiles={_trafficMarks?.Stored}; nativeAlbedo={_nativeAlbedo?.name}; nativeNormal={_nativeNormal?.name}; laneRibbons={_laneWearMask?.Drawn}; depthView={Mod.Settings.ShowSnowDepth}; residualHours={Mod.Settings.ResidualMeltHours}; atlas batches={_atlas?.BatchCount}; native junctions={_nativeJunctions.Built}; unavailable native templates={_nativeJunctions.Unavailable}; node parts={_nodeParts}; queued={_pending.Count - _pendingHead}; field cells={_activePatches.Sum(p => (long)p.Field.Columns * p.Field.Rows)}; tickMs={_lastTickMs:F2}; snowMs={_snowBudgetMs:F2}; refreshMs={_lastRefreshMs:F2}"
                );
            }
            catch (Exception error)
            {
                Mod.log.Warn("Could not write diagnostics: " + error.Message);
            }
        }

        bool visible =
            Mod.Settings.Enabled && (SharedSettings.instance?.gameplay?.snowVisual ?? true);
        if (!visible)
        {
            _cachedCoverage.SetVisible(false);
            if (_patches.Count > 0)
            {
                ReleaseOverlays();
            }

            return;
        }

        float now = UnityEngine.Time.realtimeSinceStartup;
        if (!_isPreviewActive && Mod.Settings.PreviewSnow)
        {
            _previewStarted = now;
        }

        if (_isPreviewActive && now - _previewStarted >= 30)
        {
            Mod.Settings.PreviewSnow = false;
        }

        if (_surfaceOffsetCm != Mod.Settings.SurfaceOffsetCm)
        {
            ReleaseOverlays();
            _isPreviewActive = Mod.Settings.PreviewSnow;
            _previewStarted = now;
            _surfaceOffsetCm = Mod.Settings.SurfaceOffsetCm;
            _nextRefresh = 0;
        }

        _isPreviewActive = Mod.Settings.PreviewSnow;
        // Some load paths attach the camera after OnGameLoadingComplete.
        if (!_coveragePrepared)
            PrepareLoadingCoverage();
        if (!_isPreviewActive && now >= _nextReadback)
        {
            RequestSnowSnapshot(now);
        }
        if (SuspendDryScene())
        {
            return;
        }
        if (!EnsureMaterial())
        {
            return;
        }

        Shader.SetGlobalTexture("_RoadSnowNative", _snowSystem.SnowDepth);
        Shader.SetGlobalVector("_RoadSnowMapping", _terrainSystem.mapOffsetScale);
        Shader.SetGlobalFloat("_RoadSnowPreview", _isPreviewActive ? 1 : 0);
        UpdateAppearance(now);
        RecordStage(PerformanceStage.Setup, Milliseconds(setupStarted));
        long renderStarted = Stopwatch.GetTimestamp();
        _cachedCoverage.ObservePlowSnowfall(_snowfall.TotalMm);
        _cachedCoverage.Draw(_cameraSystem.activeCamera, _material, _cacheShader);
        RecordStage(PerformanceStage.Coverage, Milliseconds(renderStarted));
        Camera camera = _cameraSystem.activeCamera;
        if (camera == null)
        {
            return;
        }

        RunScheduledWork(camera, now, setupStarted);
        _lastTickMs = Milliseconds(setupStarted);
        if (now >= _nextMetrics)
        {
            _trafficMarks.ProbeGpu(message => Mod.log.Info("RoadSnow " + message));
            _laneWearMask.ProbeGpu(message => Mod.log.Info("RoadSnow " + message));
            Mod.log.Info(
                $"RoadSnow reuse totals: lanePrepared={_laneWearMask.PreparedAreas}, laneReused={_laneWearMask.ReusedAreas}, quadsPrepared={_laneWearMask.PreparedQuads}, quadsRetained={_laneWearMask.RetainedQuads}, fineSelect={_trafficMarks.SelectionBuilds}, fineSelectReuse={_trafficMarks.SelectionReuses}, farSelect={_distantMarks.SelectionBuilds}, farSelectReuse={_distantMarks.SelectionReuses}, fineUploads={_trafficMarks.UploadedTiles}, farUploads={_distantMarks.UploadedTiles}"
            );
            Mod.log.Info(
                $"RoadSnow performance: patches={_patches.Count}, batches={_atlas.BatchCount}, nodes={_nodeParts}, pending={_pending.Count - _pendingHead}, detailed={_detailedParts}, tileWrites={_uploadedParts}, tickMs={_lastTickMs:F2}, stampMs={_stampMs:F2}, agentsMs={_agentsMs:F2}, discoveryMs={_discoveryMs:F2}, laneUpdateMs={_laneUpdateMs:F2}, marksMs={_marksMs:F2}, laneDrawMs={_laneDrawMs:F2}, fineMarksMs={_trafficMarks.LastDrawMs:F2}, farMarksMs={_distantMarks.LastDrawMs:F2}, snowMs={_snowBudgetMs:F2}, createMs={_createMs:F2}, meshMs={_atlasMs:F2}, refreshMs={_lastRefreshMs:F2}, tires={_carsStamped}, feet={_footprintsStamped}, native={_snowSnapshot != null}, visibleWearTiles={_trafficMarks.Drawn}, storedWearTiles={_trafficMarks.Stored}, liveWearTiles={_trafficMarks.LiveTiles}, viewCandidates={_trafficMarks.ViewCandidates}, trackedPeople={_pedestrianHistory.Count}, trackedCars={_vehicleHistory.Count}, lanes={_laneWearMask.Registered}, laneRibbons={_laneWearMask.Drawn}, laneRasterTotal={_laneWearMask.Rasterizations}, laneRasterReuseTotal={_laneWearMask.ReusedRasterizations}, lanePeakPressure={_laneWearMask.PeakPressure:F3}, bankTriangles={_snowBanks?.Triangles}, bankBatches={_snowBanks?.Batches}, bankMs={_snowBanks?.UpdateMs:F2}, facilitySurfaces={_facilities?.Count}, facilityTriangles={_facilities?.Triangles}, facilityMs={_facilities?.UpdateMs:F2}"
            );
            _uploadedParts = 0;
            _nextMetrics = now + 20;
        }
    }

    private double _lastCreateMs;
    private double _laneUpdateMs;
    private double _laneDrawMs;
    private double _agentsMs;
    private double _discoveryMs;

    private static double Milliseconds(long started) =>
        (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;

    [Preserve]
    protected override void OnDestroy()
    {
        ReleaseOverlays(false);
        if (_shaderBundle != null)
        {
            _shaderBundle.Unload(true);
        }

        _shaderBundle = null;
        _surfaceShader = null;
        base.OnDestroy();
    }
}
