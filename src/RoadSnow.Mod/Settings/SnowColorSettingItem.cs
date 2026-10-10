using Game.Reflection;
using Game.UI.Menu;
using Game.UI.Widgets;
using UnityEngine;

namespace RoadSnow.Mod;

internal sealed class SnowColorField : ColorField { }

internal sealed class SnowColorSettingItem : AutomaticSettings.SettingItemData
{
    private readonly Setting _snow;

    public SnowColorSettingItem(Setting snow, string prefix)
        : base(
            AutomaticSettings.WidgetType.None,
            snow,
            new AutomaticSettings.ProxyProperty(
                typeof(Setting).GetProperty(nameof(Setting.SnowColor))
            ),
            prefix
        )
    {
        _snow = snow;
        simpleGroup = advancedGroup = "Color";
    }

    protected override IWidget GetWidget() =>
        new SnowColorField
        {
            path = this.path,
            displayName = this.displayName,
            description = this.description,
            hdr = false,
            showAlpha = false,
            disabled = () => _snow.UseGameSnowColor,
            accessor = new DelegateAccessor<Color>(
                () => _snow.SnowColor,
                color =>
                {
                    _snow.SnowColor = color;
                    _snow.ApplyAndSave();
                }
            ),
        };
}
