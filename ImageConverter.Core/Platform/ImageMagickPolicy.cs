using ImageMagick;
using ImageMagick.Configuration;

namespace ImageConverter.Core.Platform;

/// <summary>
/// Sicherheitsrichtlinie für ImageMagick: keine Netzzugriffe, keine externen Programme.
/// Ohne sie lädt ImageMagick bei einem Pfad wie „https://…“ über eine DNS-Anfrage und das
/// externe Programm curl nach (siehe docs/FEHLERGEDAECHTNIS.md). ImageConverter braucht weder
/// das eine noch das andere — alle Bildformate sind in ImageMagick eingebaut.
/// <para>
/// Zusätzlich zur Standardrichtlinie von Magick.NET (MVG, MSL, EPHEMERAL, @- und |-Pfade gesperrt):
/// die Netz-Formate HTTP, HTTPS, FTP, URL und alle Delegates (externe Programme).
/// </para>
/// </summary>
public static class ImageMagickPolicy
{
    internal const string ExtraRules =
        "  <policy domain=\"coder\" rights=\"none\" pattern=\"{HTTP,HTTPS,FTP,URL}\" />\n" +
        "  <policy domain=\"delegate\" rights=\"none\" pattern=\"*\" />\n";

    private static readonly Lock Gate = new();
    private static bool _applied;

    /// <summary>
    /// Muss vor dem ersten Zugriff auf ImageMagick laufen (<see cref="NativeLibraries.Configure"/>).
    /// ImageMagick liest seine Konfiguration aus Dateien; sie kommen in einen eigenen Ordner
    /// und werden bei jedem Start neu geschrieben.
    /// </summary>
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

            // Grenzen gegen präparierte Dateien (gemessen: eine 1,5-MB-PNG mit 40000 × 40000 Pixeln wurde geladen und füllte
            // Speicher bzw. Auslagerung). Nur hier beim Start setzen — Grenzen von ImageMagick nie ändern, während es
            // rechnen kann (siehe docs/FEHLERGEDAECHTNIS.md #77). Die eigentliche Prüfung der Bildgröße macht
            // ImageLoader vor dem Entpacken (ImageLoader.MaxPixels).
            ResourceLimits.Width = MaxEdge;
            ResourceLimits.Height = MaxEdge;
            ResourceLimits.ListLength = 4096;                 // mehrteilige Dateien (GIF, TIFF): Bilder je Datei
            ResourceLimits.Disk = 1024UL * 1024 * 1024;       // Auslagerung höchstens 1 GB (unter Linux oft im Arbeitsspeicher)
        }
    }

    /// <summary>Größte Kantenlänge, die ImageMagick annimmt (wie JPEG).</summary>
    public const uint MaxEdge = 65535;

    /// <summary>
    /// ~/.local/share/ImageConverter/ImageMagick bzw. %LOCALAPPDATA%\ImageConverter\ImageMagick;
    /// liefert .NET keinen Benutzerordner (z. B. fehlendes XDG-Verzeichnis), der Temp-Ordner.
    /// </summary>
    internal static string Folder() => Path.Combine(UserDataRoot(), "ImageMagick");

    /// <summary>Benutzerordner des Programms: ~/.local/share/ImageConverter bzw. %LOCALAPPDATA%\ImageConverter.</summary>
    internal static string UserDataRoot()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var root = string.IsNullOrEmpty(local) ? Path.GetTempPath() : local;
        return Path.Combine(root, "ImageConverter");
    }
}
