using System.Text.Json;

namespace ImageConverter.Viewer3D;

/// <summary>
/// How the 3D viewer was last used, kept for the next start in its own file next to the program settings
/// (viewer3d.json), so that the viewer stays independent of the rest of the program.
/// </summary>
/// <param name="Version">Version of the stored format.</param>
/// <param name="Mode">0 = solid, 1 = wireframe, 2 = blueprint.</param>
/// <param name="Edges">Sharp edges drawn as lines in the solid view.</param>
/// <param name="Parts">Connected parts in their own colours.</param>
/// <param name="Orthographic">Orthographic rather than perspective projection.</param>
/// <param name="PivotAtCentre">Turn around the vertical centre axis of the model instead of the point under the mouse.</param>
/// <param name="Inches">Show lengths in inches instead of millimetres.</param>
/// <param name="Brightness">Brightness of the lighting in percent (<see cref="MinBrightness"/> to <see cref="MaxBrightness"/>).</param>
/// <param name="Quality">Drawing quality: 0 fast, 1 medium, 2 high.</param>
public sealed record ViewerSettings(
    int Version = 1, int Mode = 0, bool Edges = true, bool Parts = false, bool Orthographic = true,
    bool PivotAtCentre = false, bool Inches = false, int Brightness = 100, int Quality = 2)
{
    public const int MinBrightness = 50, MaxBrightness = 150;

    /// <summary>A brightness in the allowed range (a hand-edited file or a slider beyond its ends).</summary>
    public static int ClampBrightness(int percent) => Math.Clamp(percent, MinBrightness, MaxBrightness);

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly Lock Gate = new();

    private static string File => Path.Combine(Path.GetDirectoryName(AppSettings.DefaultFile) ?? ".", "viewer3d.json");

    /// <summary>The stored settings, or the defaults if there are none or they cannot be read.</summary>
    public static ViewerSettings Load()
    {
        try
        {
            var info = new FileInfo(File);
            if (!info.Exists || info.Length > 64 * 1024) return new();   // a settings file is tiny; anything else is not ours
            var s = JsonSerializer.Deserialize<ViewerSettings>(System.IO.File.ReadAllText(File), Json) ?? new();
            return s with { Mode = Math.Clamp(s.Mode, 0, 2), Brightness = ClampBrightness(s.Brightness), Quality = Math.Clamp(s.Quality, 0, 2) };
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return new();
        }
    }

    /// <summary>Changes and stores the settings; a file that cannot be written is ignored (the change applies until exit).</summary>
    public static void Update(Func<ViewerSettings, ViewerSettings> change)
    {
        lock (Gate)
        {
            var next = change(Load());
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(File)!);
                var tmp = File + ".tmp";
                System.IO.File.WriteAllText(tmp, JsonSerializer.Serialize(next, Json));
                System.IO.File.Move(tmp, File, overwrite: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                AppLog.Write("3D: settings not saved", e.Message);
            }
        }
    }
}
