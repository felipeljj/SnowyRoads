using System;
using UnityEngine;

namespace RoadSnow.Mod;

public partial class RoadSnowSystem
{
    private Texture2D _nativeAlbedo;
    private Texture2D _nativeNormal;
    private float _nextAmbientProbe;
    private readonly Vector3[] _ambientDirections =
    {
        Vector3.up,
        Vector3.down,
        Vector3.left,
        Vector3.right,
        Vector3.forward,
        Vector3.back,
    };
    private readonly Color[] _ambientColors = new Color[6];

    private void UpdateAppearance(float now)
    {
        if (now >= _nextAmbientProbe)
        {
            _nextAmbientProbe = now + .1f;
            var ambient = RenderSettings.ambientProbe;
            ambient.Evaluate(_ambientDirections, _ambientColors);
            Shader.SetGlobalVector("_RoadSnowAmbientUp", _ambientColors[0]);
            Shader.SetGlobalVector("_RoadSnowAmbientDown", _ambientColors[1]);
            Shader.SetGlobalVector("_RoadSnowAmbientLeft", _ambientColors[2]);
            Shader.SetGlobalVector("_RoadSnowAmbientRight", _ambientColors[3]);
            Shader.SetGlobalVector("_RoadSnowAmbientForward", _ambientColors[4]);
            Shader.SetGlobalVector("_RoadSnowAmbientBack", _ambientColors[5]);
        }
        Color color = Mod.Settings.UseGameSnowColor ? Color.white : CurrentSnowColor.linear;
        color.a = Mod.Settings.HideRoadMarkings ? 1 : .72f;
        _material.SetColor("_BaseColor", color);
        _snowBanks?.SetColor(color);
        _facilities?.SetColor(color);
        Shader.SetGlobalFloat("_RoadSnowDepthView", Mod.Settings.ShowSnowDepth ? 1 : 0);
        Shader.SetGlobalFloat("_RoadSnowRelief", Mod.Settings.VisibleRelief ? 1 : 0);
        Shader.SetGlobalFloat("_RoadSnowRealistic", Mod.Settings.RealisticSnow ? 1 : 0);
        Shader.SetGlobalFloat("_RoadSnowUseNativeColor", Mod.Settings.UseGameSnowColor ? 1 : 0);
        if ((_nativeAlbedo == null || _nativeNormal == null) && now >= _nextColorProbe)
        {
            _nextColorProbe = now + 10;
            foreach (var texture in Resources.FindObjectsOfTypeAll<Texture2D>())
            {
                if (texture == null)
                {
                    continue;
                }

                if (string.Equals(texture.name, "snowalbedo2", StringComparison.OrdinalIgnoreCase))
                {
                    _nativeAlbedo = texture;
                }

                if (string.Equals(texture.name, "snownormal2", StringComparison.OrdinalIgnoreCase))
                {
                    _nativeNormal = texture;
                }
            }

            _colorFound = _nativeAlbedo != null;
            if (_colorFound)
            {
                Mod.log.Info(
                    "RoadSnow native snow textures: "
                        + _nativeAlbedo.name
                        + "; normal="
                        + _nativeNormal?.name
                );
            }
        }

        Shader.SetGlobalTexture(
            "_RoadSnowAlbedo",
            _nativeAlbedo != null ? _nativeAlbedo : Texture2D.whiteTexture
        );
        Shader.SetGlobalTexture(
            "_RoadSnowNormal",
            _nativeNormal != null ? _nativeNormal : Texture2D.normalTexture
        );
        Shader.SetGlobalFloat("_RoadSnowHasNormal", _nativeNormal != null ? 1 : 0);
    }
}
