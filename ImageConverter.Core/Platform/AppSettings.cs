using System.Text.Json;

namespace ImageConverter.Core.Platform;

/// <summary>
/// Dauerhafte Einstellungen des Benutzers (Sprache; bei „KI begradigen“, ob Schatten herausgerechnet werden; letzter
/// Geschwindigkeitstest Grafikkarte/CPU für Bildfilter und KI) in
/// ~/.local/share/ImageConverter/settings.json bzw. %LOCALAPPDATA%\ImageConverter\settings.json.
/// Fehlt die Datei oder ist sie kaputt, gelten die Voreinstellungen.
/// </summary>
/// <summary>Gemessene Zeiten der Upscaler (Sekunden je Megapixel) für einen Rechner (<see cref="SpeedTest.AiMachine"/>).</summary>
public sealed record UpscaleTimes(string Machine, Dictionary<string, double> SecondsPerMegapixel);

public sealed record AppSettings(string? Language = null, bool AiRemoveShadows = true,
    SpeedMeasure? FilterSpeed = null, SpeedMeasure? AiSpeed = null, UpscaleTimes? UpscaleTimes = null)
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

    public void Save(string? file = null)
    {
        file ??= DefaultFile;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nicht speicherbar (schreibgeschützt …): gilt dann nur bis zum Beenden
        }
    }
}
