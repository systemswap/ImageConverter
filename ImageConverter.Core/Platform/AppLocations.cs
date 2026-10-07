namespace ImageConverter.Core.Platform;

/// <summary>
/// Wo das Programm liegt. Neben ihm liegen die fremden Bibliotheken (Tools/…) und die
/// KI-Modelle — bei einer AppImage neben der .AppImage-Datei, denn deren Inneres ist
/// schreibgeschützt und hängt bei jedem Start unter einem anderen Pfad (/tmp/.mount_…).
/// </summary>
public static class AppLocations
{
    public const string ToolsFolderName = "Tools";

    /// <summary>Ordner der .exe bzw. der .AppImage-Datei.</summary>
    public static string ProgramFolder { get; } =
        Resolve(Environment.GetEnvironmentVariable("APPIMAGE"), AppContext.BaseDirectory);

    /// <summary>Eigener Ordner einer mitgelieferten fremden Bibliothek, z. B. Tools/ImageMagick.</summary>
    public static string ToolFolder(string name) => Path.Combine(ProgramFolder, ToolsFolderName, name);

    /// <summary>
    /// Den Pfad der AppImage-Datei setzt deren Laufzeit in $APPIMAGE; ohne AppImage gilt
    /// der Programmordner.
    /// </summary>
    internal static string Resolve(string? appImage, string baseDirectory) =>
        !string.IsNullOrEmpty(appImage) && Path.GetDirectoryName(appImage) is { Length: > 0 } dir
            ? dir
            : baseDirectory;
}
