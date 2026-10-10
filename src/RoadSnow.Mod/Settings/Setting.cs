using Colossal.IO.AssetDatabase;
using Colossal.Json;
using Game.Modding;
using Game.Settings;
using Game.UI.Menu;
using UnityEngine;

namespace RoadSnow.Mod;

public enum SnowBankQuality
{
    Low = 1,
    Balanced = 2,
    High = 3,
}

[FileLocation("RoadSnow")]
[SettingsUITabOrder("Main", "Appearance", "Advanced")]
[SettingsUIGroupOrder("General", "Weather", "Color", "Relief", "Performance", "Inspect", "Test")]
[SettingsUIShowGroupName("General", "Weather", "Color", "Relief", "Performance", "Inspect", "Test")]
public sealed class Setting : ModSetting
{
    // Property names are used by saved settings and localization keys. Preserve them
    // when reorganizing the menu so existing users keep their configuration.
    public Setting(IMod mod)
        : base(mod) => SetDefaults();

    [SettingsUISection("Main", "General")]
    public bool Enabled { get; set; } = true;

    [SettingsUISection("Main", "General")]
    public bool HideRoadMarkings { get; set; } = true;

    [SettingsUISlider(min = 0, max = 100, step = 10)]
    [SettingsUISection("Main", "Weather")]
    public int TrafficInfluence { get; set; } = 70;

    [SettingsUISlider(min = 0, max = 6, step = 1)]
    [SettingsUISection("Main", "Weather")]
    public int ResidualMeltHours { get; set; } = 2;

    [SettingsUISection("Appearance", "Color")]
    public bool UseGameSnowColor { get; set; } = true;

    [SettingsUIHidden]
    public int SnowRed { get; set; } = 235;

    [SettingsUIHidden]
    public int SnowGreen { get; set; } = 239;

    [SettingsUIHidden]
    public int SnowBlue { get; set; } = 242;

    [Exclude, SettingsUIHidden]
    public Color SnowColor
    {
        get =>
            new(
                Mathf.Clamp01(SnowRed / 255f),
                Mathf.Clamp01(SnowGreen / 255f),
                Mathf.Clamp01(SnowBlue / 255f),
                1
            );
        set
        {
            SnowRed = Mathf.RoundToInt(Mathf.Clamp01(value.r) * 255);
            SnowGreen = Mathf.RoundToInt(Mathf.Clamp01(value.g) * 255);
            SnowBlue = Mathf.RoundToInt(Mathf.Clamp01(value.b) * 255);
        }
    }

    [SettingsUISection("Appearance", "Relief")]
    public bool VisibleRelief { get; set; } = true;

    [SettingsUISection("Appearance", "Relief")]
    public bool RaisedSnow { get; set; } = true;

    [SettingsUIHidden]
    public int BankDetail { get; set; } = 2;

    [Exclude]
    [SettingsUIDisableByCondition(typeof(Setting), nameof(BanksDisabled))]
    [SettingsUISection("Appearance", "Relief")]
    public SnowBankQuality BankQuality
    {
        get => (SnowBankQuality)Mathf.Clamp(BankDetail, 1, 3);
        set => BankDetail = Mathf.Clamp((int)value, 1, 3);
    }

    public bool BanksDisabled() => !RaisedSnow;

    [SettingsUISlider(min = 16, max = 128, step = 16)]
    [SettingsUISection("Advanced", "Performance")]
    public int MaximumPatches { get; set; } = 64;

    [SettingsUISection("Advanced", "Inspect")]
    public bool ShowSnowDepth { get; set; }

    [SettingsUISlider(min = 1, max = 12, step = 1)]
    [SettingsUISection("Advanced", "Inspect")]
    public int SurfaceOffsetCm { get; set; } = 2;

    [SettingsUISection("Advanced", "Test")]
    public bool PreviewSnow { get; set; }

    [SettingsUIButton]
    [SettingsUISection("Advanced", "Test")]
    public bool WriteDiagnostics
    {
        set => Mod.RequestDiagnostics = true;
    }

    public override AutomaticSettings.SettingPageData GetPageData(string id, bool addPrefix)
    {
        var page = base.GetPageData(id, addPrefix);
        page["Appearance"].InsertItem(new SnowColorSettingItem(this, page.prefix), 1);
        return page;
    }

    public override void SetDefaults()
    {
        Enabled = HideRoadMarkings = UseGameSnowColor = true;
        TrafficInfluence = 70;
        SnowRed = 235;
        SnowGreen = 239;
        SnowBlue = 242;
        MaximumPatches = 64;
        SurfaceOffsetCm = 2;
        PreviewSnow = ShowSnowDepth = false;
        VisibleRelief = RaisedSnow = true;
        ResidualMeltHours = 2;
        BankDetail = 2;
    }
}
