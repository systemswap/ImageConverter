using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using ImageConverter.Core.Platform;

namespace ImageConverter.Core.Localization;

/// <summary>Specifies how the flag of a language is drawn.</summary>
public enum FlagKind
{
    /// <summary>Stripes of equal width in <see cref="Flag.Colors"/>.</summary>
    Stripes,
    /// <summary>Union Jack (built in for English).</summary>
    UnionJack,
    /// <summary>Image file (PNG) in the languages folder, see <see cref="Flag.ImagePath"/>.</summary>
    Image,
    /// <summary>No flag specified; drawn as a grey box with the language code.</summary>
    None,
}

/// <summary>Describes the flag shown for a language.</summary>
/// <param name="Kind">How the flag is drawn.</param>
/// <param name="Vertical">Vertical stripes (as in the French flag) instead of horizontal ones (as in the German flag).</param>
/// <param name="Colors">Stripe colours as hexadecimal strings ("#RRGGBB") for <see cref="FlagKind.Stripes"/>.</param>
/// <param name="ImagePath">Absolute path of the image file for <see cref="FlagKind.Image"/>.</param>
public sealed record Flag(FlagKind Kind, bool Vertical = false, IReadOnlyList<string>? Colors = null, string? ImagePath = null)
{
    /// <summary>Flag used when a language specifies none or an invalid one.</summary>
    public static readonly Flag None = new(FlagKind.None);
}

/// <summary>
/// A user interface language: built in (German, English) or loaded from a JSON file.
/// </summary>
/// <remarks>
/// <see cref="Name"/> is written in the language itself ("Français"); together with the flag this
/// lets users find their own language even while a foreign one is selected.
/// </remarks>
/// <param name="Code">Lower-case language code, for example "fr"; also used for <see cref="Tr.Culture"/>.</param>
/// <param name="Name">Name of the language in the language itself.</param>
/// <param name="Flag">Flag shown next to the name.</param>
/// <param name="Texts">Translations keyed by the German text; empty for German.</param>
/// <param name="File">Path of the JSON file; <see langword="null"/> for a built-in language.</param>
public sealed record Language(string Code, string Name, Flag Flag, IReadOnlyDictionary<string, string> Texts, string? File = null)
{
    /// <summary>The built-in German language, whose texts are the keys themselves.</summary>
    public static readonly Language German = new("de", "Deutsch",
        new Flag(FlagKind.Stripes, Colors: ["#000000", "#DD0000", "#FFCE00"]),
        new Dictionary<string, string>());

    /// <summary>The built-in English language (<see cref="English.Language"/>).</summary>
    public static Language EnglishLanguage => English.Language;

    /// <summary>Whether this is German, the language of the keys.</summary>
    public bool IsGerman => Code == "de";

    /// <summary>Returns <see cref="Name"/>.</summary>
    /// <returns>The name of the language in the language itself.</returns>
    public override string ToString() => Name;
}

/// <summary>
/// Finds all languages: the built-in ones and the JSON files in the <c>languages</c> folder next to
/// the program.
/// </summary>
/// <remarks>
/// Structure of a language file (UTF-8), for example <c>languages/fr.json</c>:
/// <code>
/// {
///   "code": "fr",
///   "name": "Français",
///   "flag": { "stripes": "vertical", "colors": ["#002395", "#FFFFFF", "#ED2939"] },
///   "texts": { "Bilder hierher ziehen": "Glissez les images ici", … }
/// }
/// </code>
/// Instead of stripes, an image file in the same folder can be given: <c>"flag": "fr.png"</c>.
/// The key is always the German text; missing texts fall back to English.
/// Comments and trailing commas are allowed; files whose name starts with "_" are skipped.
/// </remarks>
public static class Languages
{
    /// <summary>Name of the language folder next to the program.</summary>
    public const string FolderName = "languages";

    /// <summary>Absolute path of the language folder.</summary>
    public static string Folder => Path.Combine(AppLocations.ProgramFolder, FolderName);

    /// <summary>Returns the built-in languages and all valid language files.</summary>
    /// <param name="problems">Receives one message per file that could not be used.</param>
    /// <param name="folder">Folder to search; <see langword="null"/> for <see cref="Folder"/>.</param>
    /// <returns>
    /// German and the built-in English first, then the languages from the files in file name order
    /// (case-insensitive). A file with the code of the built-in English replaces that entry; a file
    /// for German is rejected with a message in <paramref name="problems"/>.
    /// </returns>
    public static List<Language> All(out List<string> problems, string? folder = null)
    {
        folder ??= Folder;
        problems = [];
        var list = new List<Language> { Language.German, English.Language };

        if (!Directory.Exists(folder)) return list;
        foreach (var file in Directory.EnumerateFiles(folder, "*.json").Order(StringComparer.OrdinalIgnoreCase))
        {
            if (Path.GetFileName(file).StartsWith('_')) continue;   // template or similar
            try
            {
                var lang = Load(file);
                // A file for a built-in language supplements or replaces its texts.
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

    /// <summary>Loads a language file.</summary>
    /// <param name="file">Path of the JSON file.</param>
    /// <returns>
    /// The language; a missing code is taken from the file name, a missing name from the code. Empty
    /// texts are ignored.
    /// </returns>
    /// <exception cref="InvalidDataException">The file does not contain a JSON object.</exception>
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
    /// Chooses the language for the first start: the system language if available, otherwise English
    /// (German only if the system language is German).
    /// </summary>
    /// <param name="available">Available languages; must contain English.</param>
    /// <param name="culture">Culture to match; <see langword="null"/> for <see cref="CultureInfo.CurrentUICulture"/>.</param>
    /// <returns>The language whose code matches the two-letter ISO language name, otherwise English.</returns>
    public static Language ForSystem(IReadOnlyList<Language> available, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentUICulture;
        return available.FirstOrDefault(l => l.Code == culture.TwoLetterISOLanguageName)
               ?? available.First(l => l.Code == "en");
    }

    /// <summary>
    /// Writes a template for a new language that contains all texts, pre-filled with English for
    /// translation.
    /// </summary>
    /// <remarks>The file is named "_vorlage.json" and is therefore skipped by <see cref="All"/>.</remarks>
    /// <param name="folder">Target folder, created if missing; <see langword="null"/> for <see cref="Folder"/>.</param>
    /// <returns>Path of the written file.</returns>
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
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,   // keep non-ASCII characters such as umlauts readable
        }));
        return path;
    }
}
