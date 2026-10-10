using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Colossal;

namespace RoadSnow.Mod;

internal sealed class LocaleSource : IDictionarySource
{
    public static readonly string[] Languages =
    {
        "en-US",
        "pt-BR",
        "es-ES",
        "fr-FR",
        "ru-RU",
        "ja-JP",
        "zh-HANS",
        "de-DE",
    };
    private readonly Setting _settings;
    private readonly Dictionary<string, string> _entries;

    public LocaleSource(Setting settings, string language)
    {
        _settings = settings;
        _entries = Read("en-US");
        if (language != "en-US")
        {
            foreach (var entry in Read(language))
            {
                _entries[entry.Key] = entry.Value;
            }
        }
    }

    private static Dictionary<string, string> Read(string language)
    {
        using var stream =
            typeof(LocaleSource).Assembly.GetManifestResourceStream(
                $"RoadSnow.Locales.{language}.tsv"
            ) ?? throw new InvalidOperationException($"Missing RoadSnow locale: {language}");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var result = new Dictionary<string, string>();
        string line;
        while ((line = reader.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            int tab = line.IndexOf('\t');
            if (tab <= 0 || tab == line.Length - 1)
            {
                throw new InvalidDataException($"Invalid RoadSnow locale: {language}");
            }

            result.Add(line.Substring(0, tab), line.Substring(tab + 1));
        }

        return result;
    }

    public IEnumerable<KeyValuePair<string, string>> ReadEntries(
        IList<IDictionaryEntryError> errors,
        Dictionary<string, int> indexCounts
    )
    {
        yield return new(_settings.GetSettingsLocaleID(), "RoadSnow");
        foreach (var entry in _entries)
        {
            int dot = entry.Key.IndexOf('.');
            string kind = entry.Key.Substring(0, dot);
            string name = entry.Key.Substring(dot + 1);
            string key = kind switch
            {
                "tab" => _settings.GetOptionTabLocaleID(name),
                "group" => _settings.GetOptionGroupLocaleID(name),
                "label" => _settings.GetOptionLabelLocaleID(name),
                "desc" => _settings.GetOptionDescLocaleID(name),
                "quality" => _settings.GetEnumValueLocaleID(
                    (SnowBankQuality)Enum.Parse(typeof(SnowBankQuality), name)
                ),
                _ => throw new InvalidDataException($"Unknown RoadSnow locale key: {entry.Key}"),
            };
            yield return new(key, entry.Value);
        }
    }

    public void Unload() { }
}
