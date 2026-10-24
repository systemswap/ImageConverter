using ImageMagick;
using ImageMagick.Configuration;

namespace ImageConverter.Core.Platform;

/// <summary>
/// Security policy for ImageMagick: no network access and no external programs.
/// </summary>
/// <remarks>
/// Without this policy, ImageMagick resolves a path such as "https://…" with a DNS query and
/// downloads it with the external program curl (see docs/FEHLERGEDAECHTNIS.md, No. 4).
/// ImageConverter needs neither; all image formats are built into ImageMagick.
/// <para>
/// In addition to the default policy of Magick.NET (which blocks MVG, MSL, EPHEMERAL and paths
/// starting with @ or |), this policy blocks the network coders HTTP, HTTPS, FTP and URL and all
/// delegates (external programs).
/// </para>
/// </remarks>
public static class ImageMagickPolicy
{
    internal const string ExtraRules =
        "  <policy domain=\"coder\" rights=\"none\" pattern=\"{HTTP,HTTPS,FTP,URL}\" />\n" +
        "  <policy domain=\"delegate\" rights=\"none\" pattern=\"*\" />\n";

    private static readonly Lock Gate = new();
    private static bool _applied;

    /// <summary>
    /// Initialises ImageMagick with the restricted policy and the resource limits.
    /// </summary>
    /// <remarks>
    /// Must run before the first use of ImageMagick (<see cref="NativeLibraries.Configure"/>).
    /// ImageMagick reads its configuration from files; they are written to a dedicated folder
    /// (<see cref="Folder"/>) on every start. Thread-safe; only the first call has an effect.
    /// </remarks>
    public static void Apply()
    {
        lock (Gate)
        {
            if (_applied) return;
            _applied = true;

            var files = ConfigurationFiles.Default;
            files.Policy.Data = files.Policy.Data.Replace("</policymap>", ExtraRules + "</policymap>");

            var folder = Folder();
            Directory.CreateDirectory(folder);
            MagickNET.Initialize(files, folder);

            // Limits against crafted files (measured: a 1.5 MB PNG with 40000 × 40000 pixels was loaded and filled memory
            // or swap). Set them only here at start-up; never change ImageMagick limits while it may be processing
            // (see docs/FEHLERGEDAECHTNIS.md, No. 77). The actual check of the image size is done by ImageLoader before
            // decoding (ImageLoader.MaxPixels).
            ResourceLimits.Width = MaxEdge;
            ResourceLimits.Height = MaxEdge;
            ResourceLimits.ListLength = 4096;                 // multi-frame files (GIF, TIFF): images per file
            ResourceLimits.Disk = 1024UL * 1024 * 1024;       // at most 1 GB of disk cache (on Linux often located in RAM)
        }
    }

    /// <summary>Largest edge length in pixels that ImageMagick accepts (the same as the JPEG limit).</summary>
    public const uint MaxEdge = 65535;

    /// <summary>
    /// Folder for the ImageMagick configuration files: ~/.local/share/ImageConverter/ImageMagick or
    /// %LOCALAPPDATA%\ImageConverter\ImageMagick; below the temporary folder if .NET returns no user
    /// folder (for example when the XDG directory is missing).
    /// </summary>
    internal static string Folder() => Path.Combine(UserDataRoot(), "ImageMagick");

    /// <summary>
    /// User data folder of the program: ~/.local/share/ImageConverter or %LOCALAPPDATA%\ImageConverter
    /// (below the temporary folder if .NET returns no user folder).
    /// </summary>
    internal static string UserDataRoot()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var root = string.IsNullOrEmpty(local) ? Path.GetTempPath() : local;
        return Path.Combine(root, "ImageConverter");
    }
}
