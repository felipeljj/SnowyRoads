using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using Object = UnityEngine.Object;

namespace RoadSnow.Mod;

public partial class RoadSnowSystem
{
    private bool EnsureMaterial()
    {
        if (_material != null)
        {
            return true;
        }

        if (_shaderFailed)
        {
            return false;
        }

        Shader shader = Shader.Find("HDRP/Lit");
        if (_shaderBundle == null)
        {
            string path = System.IO.Path.Combine(Mod.DirectoryPath ?? "", "roadsnow-shaders");
            if (System.IO.File.Exists(path))
            {
                _shaderBundle = AssetBundle.LoadFromFile(path);
                _surfaceShader = _shaderBundle?.LoadAsset<Shader>("Assets/RoadSnow/Surface.shader");
            }
        }

        var trackProbe = _shaderBundle?.LoadAsset<ComputeShader>(
            "Assets/RoadSnow/TrackAtlasProbe.compute"
        );
        var laneRaster = _shaderBundle?.LoadAsset<ComputeShader>(
            "Assets/RoadSnow/LaneRaster.compute"
        );
        if (
            _surfaceShader == null
            || !_surfaceShader.isSupported
            || trackProbe == null
            || laneRaster == null
            || !SystemInfo.supportsComputeShaders
        )
        {
            _shaderFailed = true;
            Mod.log.Error(
                "RoadSnow shader bundle missing or unsupported. Coverage disabled to avoid opaque white roads."
            );
            return false;
        }

        if (shader == null || !shader.isSupported)
        {
            _shaderFailed = true;
            Mod.log.Warn(
                "HDRP/Lit unavailable. Overlay disabled; a bundled shader may be required. Use material diagnostics."
            );
            return false;
        }

        _material = new Material(shader)
        {
            name = "RoadSnow blended snow",
            hideFlags = HideFlags.DontSave,
        };
        if (!_material.HasProperty("_BaseColorMap") || !_material.HasProperty("_AlphaCutoffEnable"))
        {
            Object.Destroy(_material);
            _material = null;
            _shaderFailed = true;
            Mod.log.Warn("Expected HDRP material properties missing. Overlay disabled.");
            return false;
        }

        _material.SetColor("_BaseColor", Color.white);
        _material.SetFloat("_SurfaceType", 1);
        _material.SetFloat("_BlendMode", 0);
        _material.SetFloat("_AlphaCutoffEnable", 0);
        _material.SetFloat("_TransparentZWrite", 0);
        _material.SetFloat("_TransparentDepthPrepassEnable", 0);
        _material.SetFloat("_TransparentDepthPostpassEnable", 0);
        _material.SetFloat("_EnableBlendModePreserveSpecularLighting", 0);
        _material.SetFloat("_EnableFogOnTransparent", 1);
        _material.SetFloat("_SupportDecals", 0);
        _material.SetFloat("_SupportSSR", 0);
        _material.SetFloat("_Smoothness", 0.18f);
        _material.SetFloat("_Metallic", 0);
        _material.SetFloat("_DoubleSidedEnable", 1);
        HDMaterial.ValidateMaterial(_material);
        _material.SetFloat("_TransparentWritingMotionVec", 0);
        _material.SetShaderPassEnabled("MotionVectors", false);
        _material.SetShaderPassEnabled("DistortionVectors", false);
        _material.SetShaderPassEnabled("TransparentDepthPrepass", false);
        _material.SetShaderPassEnabled("TransparentDepthPostpass", false);
        _material.SetVector("_BaseColorMap_ST", new Vector4(1, 1, 0, 0));
        _material.shader = _surfaceShader;
        _material.renderQueue = (int)RenderQueue.Transparent;
        _material.SetFloat("_SrcBlend", (float)BlendMode.One);
        _material.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
        _material.SetFloat("_ZWrite", 0);
        _material.SetFloat("_ZTestDepthEqualForOpaque", (float)CompareFunction.LessEqual);
        _material.SetFloat("_CullModeForward", (float)CullMode.Off);
        _atlas = new SnowAtlas(_material);
        _distantMarks = new TrafficMarks(null, _wearHistory, true);
        _trafficMarks = new TrafficMarks(trackProbe, _wearHistory);
        _laneWearMask = new LaneWearMask(laneRaster, _laneHistory, _laneLayout);
        _plowShader = _shaderBundle.LoadAsset<Shader>("Assets/RoadSnow/PlowBanks.shader");
        _cacheShader = _shaderBundle.LoadAsset<Shader>("Assets/RoadSnow/CachedSurface.shader");
        var bankShader = _shaderBundle.LoadAsset<Shader>("Assets/RoadSnow/Banks.shader");
        var facilityShader = _shaderBundle.LoadAsset<Shader>("Assets/RoadSnow/Facility.shader");
        if (bankShader != null && bankShader.isSupported)
        {
            _snowBanks = new SnowBanks(_material, bankShader);
        }

        if (facilityShader != null && facilityShader.isSupported)
        {
            _facilities = new FacilitySurfaces(EntityManager, _material, facilityShader);
            _terrainMarks = new TerrainMarks(
                _terrainSystem,
                _wearHistory,
                _material,
                facilityShader
            );
        }

        Mod.log.Info("Road overlay material created with " + _surfaceShader.name);
        return true;
    }

    public void ReleaseOverlays()
    {
        _plowedSnow.Dispose();
        _cachedCoverage.Dispose();
        _snowBanks?.Dispose();
        _snowBanks = null;
        _facilities?.Dispose();
        _terrainMarks?.Dispose();
        _terrainMarks = null;
        _terrainHeights = default;
        _facilities = null;
        _refreshScan?.Dispose();
        _refreshScan = null;
        foreach (var patch in _patches.Values)
        {
            patch.Dispose();
        }

        _patches.Clear();
        _patchesByRoad.Clear();
        _vehicleHistory.Clear();
        _singleTrackPrefabs.Clear();
        _plowHistory.Clear();
        _trainHistory.Clear();
        _pedestrianHistory.Clear();
        _surfaceCache.Clear();
        _nativeJunctions.Clear();
        _desiredByEdge.Clear();
        _keysByEdge.Clear();
        _desired.Clear();
        _pending.Clear();
        _queued.Clear();
        _trafficKeys.Clear();
        _activePatches.Clear();
        _touched.Clear();
        _distantMarks?.Dispose();
        _distantMarks = null;
        _trafficMarks?.Dispose();
        _trafficMarks = null;
        _laneWearMask?.Dispose();
        _laneWearMask = null;
        _atlas?.Dispose();
        _atlas = null;
        _snowSnapshot = null;
        _nativeSnowPresent = true;
        _dormant = false;
        _performanceCalls = _performanceDryCalls = 0;
        _performanceTotal = _performanceMaximum = 0;
        _performanceNext = 0;
        _readbackPending = false;
        _generation++;
        _snowSnapshotVersion = _snowCursor = _pendingHead = 0;
        _colorFound = false;
        _nextColorProbe = 0;
        _nativeAlbedo = _nativeNormal = null;
        _shaderFailed = false;
        _nextRefresh = _nextTick = _nextReadback = _lastTickTime = 0;
        _nextTrafficScan = _nextMetrics = 0;
        if (_material != null)
        {
            Object.Destroy(_material);
        }

        _material = null;
    }
}
