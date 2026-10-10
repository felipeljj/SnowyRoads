using System;
using UnityEngine;

namespace RoadSnow.Mod;

public partial class RoadSnowSystem
{
    private Texture2D _nativeAlbedo;
    private Texture2D _nativeNormal;

    private void UpdateAppearance(float now)
    {
        Color color = Mod.Settings.UseGameSnowColor ? Color.white : CurrentSnowColor.linear;
        color.a = Mod.Settings.HideRoadMarkings ? 1 : .72f;
        _material.SetColor("_BaseColor", color);
        _snowBanks?.SetColor(color);
        _facilities?.SetColor(color);
        Shader.SetGlobalFloat("_RoadSnowDepthView", Mod.Settings.ShowSnowDepth ? 1 : 0);
        Shader.SetGlobalFloat("_RoadSnowRelief", Mod.Settings.VisibleRelief ? 1 : 0);
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
