using System.Collections.Generic;
using Colossal.IO.AssetDatabase;
using Colossal.Logging;
using Game;
using Game.Modding;
using Game.SceneFlow;
using Unity.Entities;

namespace RoadSnow.Mod;

public class Mod : IMod
{
    public static ILog log = LogManager
        .GetLogger($"{nameof(RoadSnow.Mod)}.{nameof(Mod)}")
        .SetShowsErrorsInUI(false);
    private Setting _setting;
    private Dictionary<string, LocaleSource> _locales;
    private bool _optionsRegistered;
    public static Setting Settings { get; private set; }
    public static bool RequestDiagnostics { get; set; } = true;
    public static string DirectoryPath { get; private set; }

    public void OnLoad(UpdateSystem updateSystem)
    {
        log.Info(nameof(OnLoad));
        _locales = new Dictionary<string, LocaleSource>();
        if (GameManager.instance.modManager.TryGetExecutableAsset(this, out var asset))
        {
            DirectoryPath = System.IO.Path.GetDirectoryName(asset.path);
            log.Info($"Current mod asset at {asset.path}");
        }

        _setting = new Setting(this);
        _setting.SetDefaults();
        Settings = _setting;
        AssetDatabase.global.LoadSettings("RoadSnow", _setting, new Setting(this));
        _setting.PreviewSnow = false;
        foreach (string language in LocaleSource.Languages)
        {
            var source = new LocaleSource(_setting, language);
            _locales.Add(language, source);
            GameManager.instance.localizationManager.AddSource(language, source);
        }

        _setting.RegisterInOptionsUI();
        _optionsRegistered = true;
        updateSystem.UpdateAt<RoadSnowSystem>(SystemUpdatePhase.Rendering);
        log.Info("RoadSnow 5.7.5 junction and weather development build loaded.");
    }

    public void OnDispose()
    {
        log.Info(nameof(OnDispose));
        var world = World.DefaultGameObjectInjectionWorld;
        if (world != null && world.IsCreated)
        {
            world.GetExistingSystemManaged<RoadSnowSystem>()?.ReleaseOverlays();
        }

        Settings = null;
        if (_locales != null)
        {
            var localization = GameManager.instance?.localizationManager;
            foreach (var locale in _locales)
            {
                localization?.RemoveSource(locale.Key, locale.Value);
            }

            _locales.Clear();
        }

        if (_setting != null)
        {
            if (_optionsRegistered)
            {
                _setting.UnregisterInOptionsUI();
            }

            _optionsRegistered = false;
            _setting = null;
        }
    }
}

//as seen in some code, 5.6.3 is the internal version of development. consider release 1.0. i'll make it right in next updates
