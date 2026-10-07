using System.Globalization;

namespace ImageConverter.Core.Imaging;

/// <summary>
/// Dateinamen für den Export aus einem Muster mit Platzhaltern:
/// {name} {breite} {höhe} {n} {datum}. Überschreibt nie: gibt es den Namen
/// schon, wird „ (2)", „ (3)" … angehängt.
/// </summary>
public static class OutputNaming
{
    public const string DefaultPattern = "{name}_{breite}x{höhe}";

    /// <summary>Unterordner neben dem Original, wenn kein Zielordner gewählt ist.</summary>
    public const string DefaultSubfolder = "konvertiert";

    public static string BuildFileName(string pattern, string sourcePath, int width, int height,
        int counter, DateTime date, string extension)
    {
        if (string.IsNullOrWhiteSpace(pattern)) pattern = DefaultPattern;

        var name = pattern
            .Replace("{name}", Path.GetFileNameWithoutExtension(sourcePath), StringComparison.OrdinalIgnoreCase)
            .Replace("{breite}", width.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{höhe}", height.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{hoehe}", height.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{n}", counter.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{datum}", date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);

        // Zeichen, die Windows oder Linux in Dateinamen nicht mögen
        foreach (var c in Path.GetInvalidFileNameChars().Concat(['/', '\\', ':', '*', '?', '"', '<', '>', '|']))
            name = name.Replace(c, '_');

        name = name.Trim().TrimEnd('.');
        if (name.Length == 0) name = "bild";

        return $"{name}.{extension}";
    }

    /// <summary>Zielordner: gewählter Ordner oder Unterordner „konvertiert" neben dem Original.</summary>
    public static string ResolveFolder(string? chosenFolder, string sourcePath)
    {
        if (!string.IsNullOrWhiteSpace(chosenFolder))
            return ExpandHome(chosenFolder);

        var dir = Path.GetDirectoryName(Path.GetFullPath(sourcePath)) ?? ".";
        return Path.Combine(dir, DefaultSubfolder);
    }

    /// <summary>Freier Pfad: hängt bei Bedarf „ (2)", „ (3)" … an, damit nichts überschrieben wird.</summary>
    public static string MakeUnique(string folder, string fileName)
    {
        var path = Path.Combine(folder, fileName);
        if (!File.Exists(path)) return path;

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (var i = 2; ; i++)
        {
            path = Path.Combine(folder, $"{stem} ({i}){ext}");
            if (!File.Exists(path)) return path;
        }
    }

    private static string ExpandHome(string path) =>
        path.StartsWith('~')
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path.TrimStart('~', '/', '\\'))
            : path;
}
