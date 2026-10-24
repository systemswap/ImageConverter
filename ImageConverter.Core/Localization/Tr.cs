using System.ComponentModel;
using System.Globalization;

namespace ImageConverter.Core.Localization;

/// <summary>
/// Translates the user interface. The German text is also the key (as with gettext): code uses
/// <c>Tr.T("Bilder hierher ziehen")</c>, XAML uses <c>{l:T 'Bilder hierher ziehen'}</c>.
/// </summary>
/// <remarks>
/// German therefore needs no table, English is compiled into the program (<see cref="English"/>),
/// and further languages are loaded as JSON files from the <c>languages</c> folder next to the
/// program.
/// <para>
/// If a text is missing in a language, the English text applies, then the German text. On a
/// language switch <see cref="Version"/> raises a change notification; the user interface
/// bindings depend on it, so the texts change immediately.
/// </para>
/// </remarks>
public sealed class Tr : INotifyPropertyChanged
{
    /// <summary>The single instance that the user interface bindings observe.</summary>
    public static Tr Instance { get; } = new();

    private Language _current = Language.German;

    /// <summary>Raised for <see cref="Version"/> when the language changes.</summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Counter that increases on every language switch; it triggers the bindings.</summary>
    public int Version { get; private set; }

    /// <summary>The selected language; <see cref="Language.German"/> until <see cref="Use"/> is called.</summary>
    public static Language Current => Instance._current;

    /// <summary>Culture of the selected language for dates and numbers.</summary>
    /// <remarks>
    /// Determined from <see cref="Language.Code"/>, including for JSON languages; an unknown code
    /// falls back to <see cref="CultureInfo.InvariantCulture"/>.
    /// </remarks>
    public static CultureInfo Culture => Instance._culture;
    private CultureInfo _culture = CultureInfo.GetCultureInfo("de");

    /// <summary>Switches the user interface to a language.</summary>
    /// <param name="language">The language to use; selecting the current instance again has no effect.</param>
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

    /// <summary>Returns a text in the selected language.</summary>
    /// <param name="german">German text, which is also the key.</param>
    /// <returns>
    /// The translation of the selected language; if it is missing or empty, the English text
    /// (except for German); otherwise <paramref name="german"/> itself.
    /// </returns>
    public static string T(string german)
    {
        var lang = Instance._current;
        if (lang.Texts.TryGetValue(german, out var text) && !string.IsNullOrEmpty(text)) return text;
        if (!lang.IsGerman && English.Texts.TryGetValue(german, out var en)) return en;
        return german;
    }

    /// <summary>Returns a text with placeholders {0}, {1} … in the selected language.</summary>
    /// <param name="german">German text with composite format placeholders, which is also the key.</param>
    /// <param name="args">Values for the placeholders; numbers are formatted with <see cref="Culture"/>.</param>
    /// <returns>The translated and formatted text.</returns>
    public static string T(string german, params object?[] args) =>
        string.Format(Culture, T(german), args);
}
