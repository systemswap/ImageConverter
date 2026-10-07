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
        }
    }

    /// <summary>
    /// ~/.local/share/ImageConverter/ImageMagick bzw. %LOCALAPPDATA%\ImageConverter\ImageMagick;
    /// liefert .NET keinen Benutzerordner (z. B. fehlendes XDG-Verzeichnis), der Temp-Ordner.
    /// </summary>
    internal static string Folder()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var root = string.IsNullOrEmpty(local) ? Path.GetTempPath() : local;
        return Path.Combine(root, "ImageConverter", "ImageMagick");
    }
}
