using System.Text.Json;

namespace ImageConverter.Core.Platform;

/// <summary>
/// Measured times of the upscalers on one machine.
/// </summary>
/// <param name="Machine">Identifier of the machine the times were measured on (<see cref="SpeedTest.AiMachine"/>).</param>
/// <param name="SecondsPerWork">
/// Seconds per unit of work (<see cref="Ai.Upscale.Upscalers.Work"/>), keyed by upscaling method and
/// device ("&lt;method&gt;|gpu" or "&lt;method&gt;|cpu").
/// </param>
public sealed record UpscaleTimes(string Machine, Dictionary<string, double> SecondsPerWork);

/// <summary>
/// Persistent settings, stored in ~/.local/share/ImageConverter/settings.json or
/// %LOCALAPPDATA%\ImageConverter\settings.json.
/// </summary>
/// <remarks>If the file is missing or cannot be read, the defaults apply.</remarks>
/// <param name="Language">Code of the user interface language; <see langword="null"/> if none has been chosen.</param>
/// <param name="AiRemoveShadows">Whether the AI dewarping also removes shadows.</param>
/// <param name="FilterSpeed">Last speed test of graphics card versus CPU for the image filters.</param>
/// <param name="AiSpeed">Last speed test of graphics card versus CPU for the AI models.</param>
/// <param name="UpscaleWork">Measured upscaler times, see <see cref="UpscaleTimes"/>.</param>
/// <param name="CpuPercent">Upper limit of CPU usage in percent.</param>
/// <param name="LowPriority">Whether the program lowers its process priority.</param>
public sealed record AppSettings(string? Language = null, bool AiRemoveShadows = true,
    SpeedMeasure? FilterSpeed = null, SpeedMeasure? AiSpeed = null, UpscaleTimes? UpscaleWork = null,
    int CpuPercent = 100, bool LowPriority = true, string Theme = "System")
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>Path of the settings file in the user data folder.</summary>
    public static string DefaultFile => Path.Combine(ImageMagickPolicy.UserDataRoot(), "settings.json");

    /// <summary>Reads the settings.</summary>
    /// <param name="file">Settings file; <see langword="null"/> for <see cref="DefaultFile"/>.</param>
    /// <returns>The stored settings, or the defaults if the file is missing, unreadable or invalid.</returns>
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
    /// Reads, changes and writes the settings in one locked step.
    /// </summary>
    /// <remarks>
    /// Two callers that save at the same time (an upscaler in the background, a slider) would otherwise
    /// overwrite each other's changes.
    /// </remarks>
    /// <param name="change">Returns the changed settings from the current ones.</param>
    /// <param name="file">Settings file; <see langword="null"/> for <see cref="DefaultFile"/>.</param>
    /// <returns>The settings as saved.</returns>
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
    /// Saves the settings: first to a temporary file, which is then renamed.
    /// </summary>
    /// <remarks>
    /// A crash during writing therefore never leaves a partial file, which would otherwise be replaced by
    /// the defaults on the next start, including the speed test results. If the file cannot be written
    /// (for example because it is read-only), the settings apply only until the program exits; no
    /// exception is thrown.
    /// </remarks>
    /// <param name="file">Settings file; <see langword="null"/> for <see cref="DefaultFile"/>.</param>
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
                // Cannot be saved (read-only …): the settings apply only until the program exits.
                try { File.Delete(temp); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
    }
}
