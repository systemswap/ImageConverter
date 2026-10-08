using System.ComponentModel;
using System.Globalization;

namespace ImageConverter.Core.Localization;

/// <summary>
/// Übersetzung der Oberfläche. Der deutsche Text ist zugleich der Schlüssel
/// (wie bei gettext): Im Code steht <c>Tr.T("Bilder hierher ziehen")</c>, in XAML
/// <c>{l:T 'Bilder hierher ziehen'}</c>. Deutsch braucht deshalb keine Tabelle,
/// Englisch steht im Code (<see cref="English"/>), weitere Sprachen kommen als
/// JSON-Dateien aus dem Ordner <c>languages</c> neben dem Programm.
///
/// Fehlt ein Text in einer Sprache, gilt Englisch, dann der deutsche Text.
/// Beim Umschalten meldet <see cref="Version"/> eine Änderung — daran hängen die
/// Bindungen der Oberfläche, die Texte wechseln sofort.
/// </summary>
public sealed class Tr : INotifyPropertyChanged
{
    public static Tr Instance { get; } = new();

    private Language _current = Language.German;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Zählt bei jedem Sprachwechsel hoch (Auslöser für Bindungen).</summary>
    public int Version { get; private set; }

    public static Language Current => Instance._current;

    /// <summary>Kultur der Sprache für Datum und Zahlen; JSON-Sprachen über ihren Code.</summary>
    public static CultureInfo Culture => Instance._culture;
    private CultureInfo _culture = CultureInfo.GetCultureInfo("de");

    public static void Use(Language language)
    {
        var tr = Instance;
        if (ReferenceEquals(tr._current, language)) return;
        tr._current = language;
        try { tr._culture = CultureInfo.GetCultureInfo(language.Code); }
        catch (CultureNotFoundException) { tr._culture = CultureInfo.InvariantCulture; }
        tr.Version++;
        tr.PropertyChanged?.Invoke(tr, new PropertyChangedEventArgs(nameof(Version)));
    }

    /// <summary>Text in der gewählten Sprache.</summary>
    public static string T(string german)
    {
        var lang = Instance._current;
        if (lang.Texts.TryGetValue(german, out var text) && !string.IsNullOrEmpty(text)) return text;
        if (!lang.IsGerman && English.Texts.TryGetValue(german, out var en)) return en;
        return german;
    }

    /// <summary>Text mit Platzhaltern {0}, {1} … — Zahlen im Format der Sprache.</summary>
    public static string T(string german, params object?[] args) =>
        string.Format(Culture, T(german), args);
}
