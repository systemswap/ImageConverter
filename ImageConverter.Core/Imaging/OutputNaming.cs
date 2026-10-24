using System.Globalization;

namespace ImageConverter.Core.Imaging;

/// <summary>
/// Builds export file names from a pattern with placeholders and finds the target folder.
/// </summary>
/// <remarks>
/// Placeholders (case-insensitive): <c>{name}</c> (source file name without extension), <c>{breite}</c> (width),
/// <c>{höhe}</c> or <c>{hoehe}</c> (height), <c>{n}</c> (counter), <c>{datum}</c> (date as yyyy-MM-dd).
/// <see cref="MakeUnique"/> never overwrites: if the name already exists, " (2)", " (3)" … is appended.
/// </remarks>
public static class OutputNaming
{
    /// <summary>Pattern used when the given pattern is empty: source name, width and height.</summary>
    public const string DefaultPattern = "{name}_{breite}x{höhe}";

    /// <summary>Subfolder next to the original that is used when no target folder is chosen.</summary>
    public const string DefaultSubfolder = "konvertiert";

    /// <summary>Builds a file name from a pattern.</summary>
    /// <param name="pattern">Pattern with placeholders; empty or white space uses <see cref="DefaultPattern"/>.</param>
    /// <param name="sourcePath">Path of the source file; its name without extension replaces <c>{name}</c>.</param>
    /// <param name="width">Width in pixels for <c>{breite}</c>.</param>
    /// <param name="height">Height in pixels for <c>{höhe}</c>/<c>{hoehe}</c>.</param>
    /// <param name="counter">Value for <c>{n}</c>.</param>
    /// <param name="date">Date for <c>{datum}</c>.</param>
    /// <param name="extension">File extension without the leading dot.</param>
    /// <returns>The file name with extension. Characters that are invalid on Windows or Linux are replaced by "_",
    /// leading/trailing white space and trailing dots are removed; an empty result becomes "bild".</returns>
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

        // Characters that Windows or Linux do not allow in file names.
        foreach (var c in Path.GetInvalidFileNameChars().Concat(['/', '\\', ':', '*', '?', '"', '<', '>', '|']))
            name = name.Replace(c, '_');

        name = name.Trim().TrimEnd('.');
        if (name.Length == 0) name = "bild";

        return $"{name}.{extension}";
    }

    /// <summary>Determines the target folder: the chosen folder or the subfolder <see cref="DefaultSubfolder"/> next to
    /// the original.</summary>
    /// <param name="chosenFolder">Folder chosen by the user; null or white space means none. A leading "~" is expanded to
    /// the home folder.</param>
    /// <param name="sourcePath">Path of the source file.</param>
    /// <returns>The folder path; it is not created here.</returns>
    public static string ResolveFolder(string? chosenFolder, string sourcePath)
    {
        if (!string.IsNullOrWhiteSpace(chosenFolder))
            return ExpandHome(chosenFolder);

        var dir = Path.GetDirectoryName(Path.GetFullPath(sourcePath)) ?? ".";
        return Path.Combine(dir, DefaultSubfolder);
    }

    /// <summary>Returns a path that does not exist yet, appending " (2)", " (3)" … if needed so that nothing is
    /// overwritten.</summary>
    /// <param name="folder">Target folder.</param>
    /// <param name="fileName">Desired file name with extension.</param>
    /// <returns>The first free path.</returns>
    /// <remarks>Only checks for existing files at the time of the call; it does not reserve the name.</remarks>
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
