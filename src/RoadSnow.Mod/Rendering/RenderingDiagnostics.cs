using System;
using System.IO;
using System.Linq;
using System.Text;
using Colossal.PSI.Environment;
using Game.Simulation;
using UnityEngine;
using UnityEngine.Rendering;

namespace RoadSnow.Mod;

internal static class RenderingDiagnostics
{
    public static void Write(
        ClimateSystem climate,
        SnowSystem snow,
        int roads,
        int patches,
        bool preview,
        int visibleNetworks,
        int detailedParts,
        int tireStamps,
        int footsteps,
        bool nativeSnapshot,
        string runtime = ""
    )
    {
        var text = new StringBuilder();
        text.AppendLine("RoadSnow local rendering diagnostic — " + DateTime.UtcNow.ToString("O"));
        text.AppendLine(
            $"Roads={roads}; active patches={patches}; temperature={climate.temperature}; precipitation={climate.precipitation}; snowing={climate.isSnowing}"
        );
        text.AppendLine(
            $"Preview={preview}; visible networks={visibleNetworks}; detailed parts={detailedParts}; tire stamps last tick={tireStamps}; footprint stamps last tick={footsteps}; native snapshot={nativeSnapshot}"
        );
        text.AppendLine(runtime);
        var ambient = new Color[1];
        RenderSettings.ambientProbe.Evaluate(new[] { Vector3.up }, ambient);
        text.AppendLine("Game ambient light above snow=" + ambient[0]);
        text.AppendLine(
            $"Native snow texture={snow.SnowDepth?.name}; size={snow.SnowDepth?.width}x{snow.SnowDepth?.height}"
        );
        text.AppendLine(
            "Current overlay requires HDRP/Lit. Shader.Find result="
                + (Shader.Find("HDRP/Lit")?.name ?? "missing")
        );
        var materials = Resources
            .FindObjectsOfTypeAll<Material>()
            .Where(m => m != null && m.shader != null)
            .OrderByDescending(m => Relevant(m.name + " " + m.shader.name))
            .ThenBy(m => m.name)
            .Take(200);
        foreach (var material in materials)
        {
            text.AppendLine(
                $"\nMATERIAL {material.name}; shader={material.shader.name}; queue={material.renderQueue}"
            );
            text.AppendLine("Keywords: " + string.Join(", ", material.shaderKeywords));
            Shader shader = material.shader;
            for (int i = 0; i < shader.GetPropertyCount(); i++)
            {
                string name = shader.GetPropertyName(i);
                var type = shader.GetPropertyType(i);
                text.Append("  " + name + " (" + type + ")");
                if (type == ShaderPropertyType.Float || type == ShaderPropertyType.Range)
                {
                    text.Append(" = " + material.GetFloat(name));
                }
                else if (type == ShaderPropertyType.Texture)
                {
                    text.Append(" = " + (material.GetTexture(name)?.name ?? "none"));
                }
                else if (type == ShaderPropertyType.Color)
                {
                    text.Append(" = " + material.GetColor(name));
                }
                else if (type == ShaderPropertyType.Vector)
                {
                    text.Append(" = " + material.GetVector(name));
                }

                text.AppendLine();
            }
        }

        string directory = Path.Combine(EnvPath.kUserDataPath, "ModsData", "RoadSnow");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "rendering-diagnostic.txt");
        File.WriteAllText(path, text.ToString(), Encoding.UTF8);
        Mod.log.Info("Rendering diagnostic written to " + path);
    }

    private static bool Relevant(string value)
    {
        value = value.ToLowerInvariant();
        return value.Contains("road")
            || value.Contains("curve")
            || value.Contains("snow")
            || value.Contains("asphalt");
    }
}
