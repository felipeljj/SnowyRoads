using System.Reflection;
using Game;
using Game.SceneFlow;
using Game.UI;
using Game.UI.Localization;
using Game.UI.Menu;
using UnityEngine.Scripting;

namespace RoadSnow.Mod;

public sealed partial class ModNewsSystem : GameSystemBase
{
    // Advance only when publishing a new changelog, not for every test rebuild.
    internal const string NewsRevision = "2026-10-performance-snowplows";
    private bool _shown;
    private float _readySince = -1;
    private static readonly FieldInfo ConfirmationCallback = typeof(AppBindings).GetField(
        "m_ConfirmationDialogCallback", BindingFlags.Instance | BindingFlags.NonPublic
    );

    [Preserve]
    protected override void OnUpdate()
    {
        var settings = Mod.Settings;
        if (_shown || settings == null || settings.SeenWhatsNewRevision == NewsRevision)
            return;
        var manager = GameManager.instance;
        var bindings = manager?.userInterface?.appBindings;
        if (bindings == null || !bindings.ready || manager.isGameLoading
            || ConfirmationCallback?.GetValue(bindings) != null)
        {
            _readySince = -1;
            return;
        }
        if (_readySince < 0)
            _readySince = UnityEngine.Time.realtimeSinceStartup;
        if (UnityEngine.Time.realtimeSinceStartup - _readySince < 2)
            return;
        bindings.ShowMessageDialog(
            new MessageDialog(
                LocalizedString.Id(settings.GetOptionLabelLocaleID("WhatsNewTitle")),
                LocalizedString.Id(settings.GetOptionDescLocaleID("WhatsNewBody")),
                LocalizedString.Id(settings.GetOptionLabelLocaleID("Acknowledge"))
            ),
            _ => { }
        );
        _shown = true;
        settings.SeenWhatsNewRevision = NewsRevision;
        settings.ApplyAndSave();
        Mod.log.Info("Displayed update notes: " + NewsRevision);
    }
}
