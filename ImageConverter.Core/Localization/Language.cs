using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using ImageConverter.Core.Platform;

namespace ImageConverter.Core.Localization;

/// <summary>Wie die Flagge einer Sprache gezeichnet wird.</summary>
public enum FlagKind
{
    /// <summary>Gleich breite Streifen in <see cref="Flag.Colors"/>.</summary>
    Stripes,
    /// <summary>Union Jack (für Englisch eingebaut).</summary>
    UnionJack,
    /// <summary>Bilddatei (PNG) im Ordner languages.</summary>
    Image,
    /// <summary>Keine Angabe — grauer Kasten mit dem Sprachcode.</summary>
    None,
}

/// <param name="Vertical">Streifen senkrecht (Frankreich) statt waagerecht (Deutschland).</param>
public sealed record Flag(FlagKind Kind, bool Vertical = false, IReadOnlyList<string>? Colors = null, string? ImagePath = null)
{
    public static readonly Flag None = new(FlagKind.None);
}

/// <summary>
/// Eine Sprache: eingebaut (Deutsch, Englisch) oder aus einer JSON-Datei.
/// <see cref="Name"/> steht in der Sprache selbst („Français“) — zusammen mit der
/// Flagge findet man die eigene Sprache wieder, auch wenn gerade eine fremde eingestellt ist.
/// </summary>
public sealed record Language(string Code, string Name, Flag Flag, IReadOnlyDictionary<string, string> Texts, string? File = null)
{
    public static readonly Language German = new("de", "Deutsch",
        new Flag(FlagKind.Stripes, Colors: ["#000000", "#DD0000", "#FFCE00"]),
        new Dictionary<string, string>());

    public static Language EnglishLanguage => English.Language;

    public bool IsGerman => Code == "de";

    public override string ToString() => Name;
}

/// <summary>
/// Findet alle Sprachen: die eingebauten und die JSON-Dateien im Ordner
/// <c>languages</c> neben dem Programm.
/// </summary>
/// <remarks>
/// Aufbau einer Sprachdatei (UTF-8), z. B. <c>languages/fr.json</c>:
/// <code>
/// {
///   "code": "fr",
///   "name": "Français",
///   "flag": { "stripes": "vertical", "colors": ["#002395", "#FFFFFF", "#ED2939"] },
///   "texts": { "Bilder hierher ziehen": "Glissez les images ici", … }
/// }
/// </code>
/// Statt der Streifen geht auch eine Bilddatei: <c>"flag": "fr.png"</c>.
/// Schlüssel ist immer der deutsche Text; fehlende Texte erscheinen auf Englisch.
/// </remarks>
public static class Languages
{
    public const string FolderName = "languages";

    public static string Folder => Path.Combine(AppLocations.ProgramFolder, FolderName);

    /// <summary>Eingebaute Sprachen und gültige Dateien; fehlerhafte Dateien landen in <paramref name="problems"/>.</summary>
    public static List<Language> All(out List<string> problems, string? folder = null)
    {
        folder ??= Folder;
        problems = [];
        var list = new List<Language> { Language.German, English.Language };

        if (!Directory.Exists(folder)) return list;
        foreach (var file in Directory.EnumerateFiles(folder, "*.json").Order(StringComparer.OrdinalIgnoreCase))
        {
            if (Path.GetFileName(file).StartsWith('_')) continue;   // Vorlage o. Ä.
            try
            {
                var lang = Load(file);
                // Eine Datei für eine eingebaute Sprache ergänzt bzw. ersetzt deren Texte
                list.RemoveAll(l => l.Code == lang.Code && l.File is null && !l.IsGerman);
                if (lang.IsGerman) problems.Add(Tr.T("{0}: Deutsch ist eingebaut und lässt sich nicht ersetzen.", Path.GetFileName(file)));
                else list.Add(lang);
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException)
            {
                problems.Add($"{Path.GetFileName(file)}: {ex.Message}");
            }
        }
        return list;
    }

    public static Language Load(string file)
    {
        var root = JsonNode.Parse(System.IO.File.ReadAllText(file),
                       documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })
                   as JsonObject ?? throw new InvalidDataException(Tr.T("kein JSON-Objekt"));

        var code = (string?)root["code"] ?? Path.GetFileNameWithoutExtension(file);
        var name = (string?)root["name"] ?? code;
        var texts = new Dictionary<string, string>();
        if (root["texts"] is JsonObject t)
            foreach (var (key, value) in t)
                if (value is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0)
                    texts[key] = s;

        return new Language(code.Trim().ToLowerInvariant(), name, ReadFlag(root["flag"], Path.GetDirectoryName(file)!), texts, file);
    }

    private static Flag ReadFlag(JsonNode? node, string folder)
    {
        switch (node)
        {
            case JsonValue v when v.TryGetValue<string>(out var image):
                var path = Path.Combine(folder, image);
                return System.IO.File.Exists(path) ? new Flag(FlagKind.Image, ImagePath: path) : Flag.None;
            case JsonObject o when o["colors"] is JsonArray colors:
                var list = colors.Select(c => (string?)c).Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c!).ToList();
                var vertical = string.Equals((string?)o["stripes"], "vertical", StringComparison.OrdinalIgnoreCase);
                return list.Count > 0 ? new Flag(FlagKind.Stripes, vertical, list) : Flag.None;
            default:
                return Flag.None;
        }
    }

    /// <summary>
    /// Sprache für den ersten Start: die des Systems, sonst Englisch
    /// (Deutsch nur, wenn das System deutsch ist).
    /// </summary>
    public static Language ForSystem(IReadOnlyList<Language> available, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentUICulture;
        return available.FirstOrDefault(l => l.Code == culture.TwoLetterISOLanguageName)
               ?? available.First(l => l.Code == "en");
    }

    /// <summary>
    /// Schreibt eine Vorlage für eine neue Sprache: alle Texte, vorbelegt mit Englisch
    /// zum Übersetzen. Liefert den Pfad.
    /// </summary>
    public static string WriteTemplate(string? folder = null)
    {
        folder ??= Folder;
        Directory.CreateDirectory(folder);
        var texts = new JsonObject();
        foreach (var (german, english) in English.Texts.OrderBy(e => e.Key, StringComparer.Ordinal))
            texts[german] = english;

        var root = new JsonObject
        {
            ["code"] = "xx",
            ["name"] = "Name der Sprache in der Sprache selbst, z. B. Français",
            ["flag"] = new JsonObject
            {
                ["stripes"] = "horizontal",
                ["colors"] = new JsonArray("#000000", "#DD0000", "#FFCE00"),
            },
            ["texts"] = texts,
        };

        var path = Path.Combine(folder, "_vorlage.json");
        System.IO.File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,   // Umlaute lesbar lassen
        }));
        return path;
    }
}
