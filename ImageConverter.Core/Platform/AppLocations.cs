namespace ImageConverter.Core.Platform;

/// <summary>
/// Locates the program folder. The bundled third-party libraries (Tools/…) and the AI models are
/// stored next to the program; for an AppImage they are stored next to the .AppImage file, because
/// the image contents are read-only and are mounted under a different path on every start
/// (/tmp/.mount_…).
/// </summary>
public static class AppLocations
{
    /// <summary>Name of the folder below <see cref="ProgramFolder"/> that holds the bundled third-party libraries.</summary>
    public const string ToolsFolderName = "Tools";

    /// <summary>Folder that contains the .exe or the .AppImage file.</summary>
    public static string ProgramFolder { get; } =
        Resolve(Environment.GetEnvironmentVariable("APPIMAGE"), AppContext.BaseDirectory);

    /// <summary>Returns the folder of a bundled third-party library, for example Tools/ImageMagick.</summary>
    /// <param name="name">Folder name of the library below <see cref="ToolsFolderName"/>.</param>
    /// <returns>Absolute path; the folder is not required to exist.</returns>
    public static string ToolFolder(string name) => Path.Combine(ProgramFolder, ToolsFolderName, name);

    /// <summary>
    /// The AppImage runtime stores the path of the .AppImage file in $APPIMAGE; without an AppImage
    /// the base directory of the program applies.
    /// </summary>
    internal static string Resolve(string? appImage, string baseDirectory) =>
        !string.IsNullOrEmpty(appImage) && Path.GetDirectoryName(appImage) is { Length: > 0 } dir
            ? dir
            : baseDirectory;
}
