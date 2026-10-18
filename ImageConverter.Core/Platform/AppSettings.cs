using System.Text.Json;

namespace ImageConverter.Core.Platform;

/// <summary>
/// Dauerhafte Einstellungen des Benutzers (Sprache; bei „KI begradigen“, ob Schatten herausgerechnet werden; letzter
/// Geschwindigkeitstest Grafikkarte/CPU für Bildfilter und KI; Obergrenze der CPU und niedrige Priorität) in
/// ~/.local/share/ImageConverter/settings.json bzw. %LOCALAPPDATA%\ImageConverter\settings.json.
/// Fehlt die Datei oder ist sie kaputt, gelten die Voreinstellungen.
/// </summary>
/// <summary>
/// Gemessene Zeiten der Upscaler (Sekunden je Aufwandseinheit, <see cref="Ai.Upscale.Upscalers.Work"/>) für einen Rechner
/// (<see cref="SpeedTest.AiMachine"/>).
/// </summary>
public sealed record UpscaleTimes(string Machine, Dictionary<string, double> SecondsPerWork);

public sealed record AppSettings(string? Language = null, bool AiRemoveShadows = true,
    SpeedMeasure? FilterSpeed = null, SpeedMeasure? AiSpeed = null, UpscaleTimes? UpscaleWork = null,
    int CpuPercent = 100, bool LowPriority = true)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string DefaultFile => Path.Combine(ImageMagickPolicy.UserDataRoot(), "settings.json");

    public static AppSettings Load(string? file = null)
    {
        try
        {
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(file ?? DefaultFile), Json) ?? new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new();
        }
    }

    private static readonly Lock Gate = new();

    /// <summary>
    /// Lesen, ändern, schreiben in einem Zug — zwei Stellen, die gleichzeitig speichern (Upscaler im Hintergrund, ein
    /// Regler), überschreiben sich sonst gegenseitig.
    /// </summary>
    public static AppSettings Update(Func<AppSettings, AppSettings> change, string? file = null)
    {
        lock (Gate)
        {
            var next = change(Load(file));
            next.Save(file);
            return next;
        }
    }

    /// <summary>
    /// Speichern: erst in eine Zwischendatei, dann umbenennen — ein Absturz mitten im Schreiben hinterlässt nie eine halbe
    /// Datei (die beim nächsten Start still durch die Vorgaben ersetzt würde, samt Geschwindigkeitstest).
    /// </summary>
    public void Save(string? file = null)
    {
        file ??= DefaultFile;
        lock (Gate)
        {
            var temp = file + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(temp, JsonSerializer.Serialize(this, Json));
                File.Move(temp, file, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Nicht speicherbar (schreibgeschützt …): gilt dann nur bis zum Beenden
                try { File.Delete(temp); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
    }
}
