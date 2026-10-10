using System;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

public static class BuildRoadSnowShader
{
    // Patch the installed HDRP shader source instead of keeping a second copy here.
    // The exact-match anchors deliberately fail when an upstream layout changes.
    [MenuItem("Tools/Snowy Roads/Build Shader Bundle")]
    public static void Build()
    {
        string output = EditorUtility.OpenFolderPanel(
            "Select the repository's assets folder",
            "",
            ""
        );
        if (string.IsNullOrEmpty(output))
        {
            return;
        }

        var pipeline = ScriptableObject.CreateInstance<HDRenderPipelineAsset>();
        var pipelineSettings = new SerializedObject(pipeline);
        pipelineSettings.FindProperty("allowShaderVariantStripping").boolValue = false;
        pipelineSettings.ApplyModifiedPropertiesWithoutUndo();
        GraphicsSettings.defaultRenderPipeline = pipeline;
        QualitySettings.renderPipeline = pipeline;
        string package =
            "Packages/com.unity.render-pipelines.high-definition/Runtime/Material/Lit/";
        string source = File.ReadAllText(package + "Lit.shader");
        Directory.CreateDirectory("Assets/RoadSnow");
        string lights = File.ReadAllText(
            "Packages/com.unity.render-pipelines.high-definition/Runtime/Lighting/LightDefinition.cs.hlsl"
        );
        const string directionalTail = "    real4 shadowMaskSelector;\n    float diffuseDimmer;";
        lights = lights.Replace("\r\n", "\n");
        if (!lights.Contains(directionalTail))
        {
            throw new Exception("Unexpected directional light layout.");
        }

        lights = lights.Replace(
            directionalTail,
            "    real4 shadowMaskSelector;\n    float2 cascadesBorderFadeScaleBias;\n    float diffuseDimmer;"
        );
        File.WriteAllText("Assets/RoadSnow/LightDefinition.cs.hlsl", lights);
        string data = File.ReadAllText(package + "LitData.hlsl");
        const string alphaLine =
            "float alpha = GetSurfaceData(input, layerTexCoord, surfaceData, normalTS, bentNormalTS);";
        if (!data.Contains(alphaLine))
        {
            throw new Exception(
                "HDRP LitData layout changed; refusing an unverified shader patch."
            );
        }

        data = "#include \"Assets/RoadSnow/SnowCommon.hlsl\"\n" + data;
        string meshModification = File.ReadAllText(package + "LitDataMeshModification.hlsl");
        const string vertexAnchor =
            "void ApplyVertexModification(AttributesMesh input, float3 normalWS, inout float3 positionRWS, float3 timeParameters)";
        int vertexAt = meshModification.IndexOf(vertexAnchor, StringComparison.Ordinal);
        if (vertexAt < 0)
        {
            throw new Exception("Missing bank vertex entry point.");
        }

        int vertexBrace = meshModification.IndexOf('{', vertexAt);
        meshModification = meshModification.Insert(
            vertexBrace + 1,
            @"
#if defined(ROADSNOW_BANKS)
    float3 bankBase=GetAbsolutePositionWS(positionRWS); bankBase.y=input.uv2.x;
    float4 bankMetadata=SAMPLE_TEXTURE2D_LOD(_BaseColorMap,sampler_BaseColorMap,input.uv0.xy,0);
    positionRWS.y+=input.uv2.y*(SnowBankScale(bankBase,bankMetadata)-1);
#endif
"
        );
        File.WriteAllText("Assets/RoadSnow/BankMeshModification.hlsl", meshModification);
        data = data.Replace(
            "Packages/com.unity.render-pipelines.high-definition/Runtime/Material/Lit/LitDataMeshModification.hlsl",
            "Assets/RoadSnow/BankMeshModification.hlsl"
        );
        data = data.Replace(
            alphaLine,
            alphaLine
                + @"
    float3 absolutePosition = GetAbsolutePositionWS(input.positionRWS);
    float3 snowSamplePosition=absolutePosition;
#if defined(ROADSNOW_BANKS)
    snowSamplePosition.y=input.texCoord2.x;
#endif
    float4 snowMetadata = SAMPLE_UVMAPPING_TEXTURE2D(_BaseColorMap, sampler_BaseColorMap, layerTexCoord.base);
    float snowDepth, retainedDepth, fineWear;
    alpha = SnowCoverage(snowSamplePosition, snowMetadata, snowDepth, retainedDepth, fineWear) * _BaseColor.a;
#if defined(ROADSNOW_BANKS)
    float bankScale=SnowBankScale(snowSamplePosition,snowMetadata);
    alpha*=smoothstep(.0003,.003,input.texCoord2.y*bankScale);
    snowDepth=max(snowDepth,input.texCoord2.y*bankScale*1000);
#elif defined(ROADSNOW_FACILITY)
    alpha=SnowFacilityOpacity(snowSamplePosition,fineWear);
#endif
    clip(alpha - 0.001);
    float3 snowTex = SAMPLE_TEXTURE2D(_RoadSnowAlbedo, s_linear_repeat_sampler, absolutePosition.xz * .5).rgb;
    surfaceData.baseColor = _RoadSnowUseNativeColor > .5 ? snowTex : _BaseColor.rgb * lerp(.85,1.05,saturate(dot(snowTex,float3(.2126,.7152,.0722))));
    surfaceData.baseColor *= SnowTrackTint(fineWear);
#if defined(ROADSNOW_FACILITY)
    surfaceData.baseColor*=.62;
#endif
"
        );
        const string normalAnchor = "    // Use bent normal to sample GI if available";
        if (!data.Contains(normalAnchor))
        {
            throw new Exception("Missing snow normal insertion point.");
        }

        data = data.Replace(
            normalAnchor,
            @"
    float2 snowP = absolutePosition.xz;
    float footprint = max(length(ddx(snowP)), length(ddy(snowP)));
    float detailFade = 1 - smoothstep(.10, .5, footprint);
    float3 nativeNormal = UnpackNormalmapRGorAG(SAMPLE_TEXTURE2D(_RoadSnowNormal, s_linear_repeat_sampler, snowP * .5));
    float3 bump = float3(nativeNormal.x,0,nativeNormal.y) * (.35 * detailFade * _RoadSnowHasNormal);
#if defined(ROADSNOW_BANKS)
    surfaceData.normalWS=normalize(lerp(float3(0,1,0),surfaceData.normalWS,bankScale));
#endif
    float3 dpdx=ddx(absolutePosition), dpdy=ddy(absolutePosition);
    float3 rx=cross(dpdy,surfaceData.normalWS), ry=cross(surfaceData.normalWS,dpdx);
    float determinant=dot(dpdx,rx);
    float bankHeight=min(.03,retainedDepth*.001);
    float3 gradient=(ddx(bankHeight)*rx+ddy(bankHeight)*ry)*sign(determinant)/max(.00001,abs(determinant));
    bump -= clamp(gradient,-.2,.2);
    bump -= surfaceData.normalWS * dot(bump,surfaceData.normalWS);
    surfaceData.normalWS = normalize(surfaceData.normalWS + bump * _RoadSnowRelief);
    surfaceData.perceptualSmoothness = .18 + .12 * detailFade * saturate(nativeNormal.z) + .12 * (1-saturate(snowDepth/10));
" + normalAnchor
        );
        const string builtinLine =
            "GetBuiltinData(input, V, posInput, surfaceData, alpha, bentNormalWS, depthOffset, layerTexCoord.base, builtinData);";
        if (!data.Contains(builtinLine))
        {
            throw new Exception("Missing builtin lighting insertion point.");
        }

        data = data.Replace(
            builtinLine,
            builtinLine
                + @"
    builtinData.bakeDiffuseLighting = max(builtinData.bakeDiffuseLighting,
        max(0,EvaluateAmbientProbe(surfaceData.normalWS)) * surfaceData.baseColor * surfaceData.ambientOcclusion);
    if (_RoadSnowDepthView > .5) {
        surfaceData.baseColor = 0;
        builtinData.bakeDiffuseLighting = 0;
        builtinData.opacity = 1;
        builtinData.emissiveColor = SnowDepthColor(snowDepth) * GetInverseCurrentExposureMultiplier();
    }
"
        );
        File.WriteAllText("Assets/RoadSnow/SurfaceData.hlsl", data);
        source = source.Replace(
            "Packages/com.unity.render-pipelines.high-definition/Runtime/Material/Lit/LitData.hlsl",
            "Assets/RoadSnow/SurfaceData.hlsl"
        );
        source = source.Replace("Shader \"HDRP/Lit\"", "Shader \"RoadSnow/Surface\"");
        const string commonInclude =
            "#include \"Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl\"";
        if (!source.Contains(commonInclude))
        {
            throw new Exception("Missing Common include for native light layout.");
        }

        source = source.Replace(
            commonInclude,
            commonInclude + "\n#include \"Assets/RoadSnow/LightDefinition.cs.hlsl\""
        );
        source = Regex.Replace(source, @"(?m)^\s*#pragma shader_feature[^\r\n]*", "");
        source = source.Replace(
            "HLSLINCLUDE",
            "HLSLINCLUDE\n#define _SURFACE_TYPE_TRANSPARENT\n#define _ENABLE_FOG_ON_TRANSPARENT\n#define _DISABLE_DECALS\n#define _DISABLE_SSR_TRANSPARENT\n#define _NORMALMAP_TANGENT_SPACE"
        );
        source = Regex.Replace(
            source,
            "#include \"(Lit[^\"]*\\.hlsl)\"",
            "#include \"" + package + "$1\""
        );
        var matches = Regex.Matches(source, @"(?m)^\s*Pass\s*\{");
        for (int i = matches.Count - 1; i >= 0; i--)
        {
            int start = matches[i].Index;
            int brace = source.IndexOf('{', start);
            int end = brace + 1;
            int depth = 1;
            while (depth > 0 && end < source.Length)
            {
                if (source[end] == '{')
                {
                    depth++;
                }

                if (source[end] == '}')
                {
                    depth--;
                }

                end++;
            }

            string pass = source.Substring(start, end - start);
            if (!Regex.IsMatch(pass, "Name\\s+\"Forward\""))
            {
                source = source.Remove(start, end - start);
            }
        }

        source = Regex.Replace(source, @"SubShader\s*\{\s*Tags\s*\{[^}]*\}\s*\}", "");
        source = Regex.Replace(
            source,
            @"(?m)^\s*#pragma multi_compile[^\r\n]*",
            match =>
                match.Value.Contains("SHADOW_LOW") || match.Value.Contains("AREA_SHADOW_MEDIUM")
                    ? match.Value
                    : ""
        );
        source = source.Replace(
            "#define SHADERPASS SHADERPASS_FORWARD",
            "#define PROBE_VOLUMES_OFF\n#define SCREEN_SPACE_SHADOWS_OFF\n#define DECALS_OFF\n#define USE_CLUSTERED_LIGHTLIST\n#define SHADERPASS SHADERPASS_FORWARD"
        );
        source = source.Replace("#define USE_FPTL_LIGHTLIST", "");
        Directory.CreateDirectory("Assets/RoadSnow");
        File.WriteAllText("Assets/RoadSnow/Surface.shader", source);
        File.WriteAllText(
            "Assets/RoadSnow/Banks.shader",
            source
                .Replace("Shader \"RoadSnow/Surface\"", "Shader \"RoadSnow/Banks\"")
                .Replace("HLSLINCLUDE", "HLSLINCLUDE\n#define ROADSNOW_BANKS\n#define _REQUIRE_UV2")
        );
        File.WriteAllText(
            "Assets/RoadSnow/Facility.shader",
            source
                .Replace("Shader \"RoadSnow/Surface\"", "Shader \"RoadSnow/Facility\"")
                .Replace("HLSLINCLUDE", "HLSLINCLUDE\n#define ROADSNOW_FACILITY")
        );
        AssetDatabase.Refresh();
        var shader = AssetDatabase.LoadAssetAtPath<Shader>("Assets/RoadSnow/Surface.shader");
        if (shader == null || ShaderUtil.ShaderHasError(shader))
        {
            throw new Exception("RoadSnow shader import failed.");
        }

        var banks = AssetDatabase.LoadAssetAtPath<Shader>("Assets/RoadSnow/Banks.shader");
        var facility = AssetDatabase.LoadAssetAtPath<Shader>("Assets/RoadSnow/Facility.shader");
        if (
            banks == null
            || facility == null
            || ShaderUtil.ShaderHasError(banks)
            || ShaderUtil.ShaderHasError(facility)
        )
        {
            throw new Exception("Bank/facility shader import failed.");
        }

        var stampShader = AssetDatabase.LoadAssetAtPath<Shader>(
            "Assets/RoadSnow/TrackStamp.shader"
        );
        if (stampShader == null || ShaderUtil.ShaderHasError(stampShader))
        {
            throw new Exception("RoadSnow track shader import failed.");
        }

        var laneShader = AssetDatabase.LoadAssetAtPath<Shader>("Assets/RoadSnow/LaneStamp.shader");
        if (laneShader == null || ShaderUtil.ShaderHasError(laneShader))
        {
            throw new Exception("RoadSnow lane shader import failed.");
        }

        Directory.CreateDirectory(output);
        var build = new AssetBundleBuild
        {
            assetBundleName = "roadsnow-shaders",
            assetNames = new[]
            {
                "Assets/RoadSnow/Surface.shader",
                "Assets/RoadSnow/TrackStamp.shader",
                "Assets/RoadSnow/LaneStamp.shader",
                "Assets/RoadSnow/Banks.shader",
                "Assets/RoadSnow/Facility.shader",
                "Assets/RoadSnow/TrackAtlasProbe.compute",
                "Assets/RoadSnow/LaneRaster.compute",
            },
        };
        if (
            BuildPipeline.BuildAssetBundles(
                output,
                new[] { build },
                BuildAssetBundleOptions.ChunkBasedCompression,
                BuildTarget.StandaloneWindows64
            ) == null
        )
        {
            throw new Exception("Shader bundle build failed.");
        }

        if (
            ShaderUtil.ShaderHasError(shader)
            || ShaderUtil.ShaderHasError(stampShader)
            || ShaderUtil.ShaderHasError(laneShader)
        )
        {
            throw new Exception("Shader compilation failed.");
        }

        if (ShaderUtil.ShaderHasError(banks) || ShaderUtil.ShaderHasError(facility))
        {
            throw new Exception("Bank/facility shader compilation failed.");
        }

        Debug.Log("ROADSNOW_SHADER_BUILD_SUCCESS");
    }
}
