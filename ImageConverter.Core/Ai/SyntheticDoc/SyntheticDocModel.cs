namespace ImageConverter.Core.Ai.SyntheticDoc;

/// <summary>
/// Das Modell auf der Platte: heruntergeladen (oder selbst trainiert) wird der PyTorch-Checkpoint
/// (<see cref="AiModelCatalog.SyntheticDoc"/>, <c>highres_121x177.ckpt</c>); die ONNX-Datei daneben baut
/// <see cref="SyntheticDocNet"/> beim ersten Gebrauch — und neu, wenn der Checkpoint jünger ist (eigenes Training: den neuen
/// Checkpoint unter demselben Namen ablegen).
/// </summary>
public static class SyntheticDocModel
{
    /// <summary>Name der gebauten ONNX-Datei (nur das Gitter, offene Eingabegröße).</summary>
    public const string OnnxFile = "syntheticdoc.onnx";

    /// <summary>Muss die ONNX-Datei zum Checkpoint (neu) gebaut werden?</summary>
    public static bool NeedsBuild(string checkpointPath, string? fallbackFolder = null) =>
        Existing(checkpointPath, fallbackFolder) is null;

    /// <summary>
    /// Die ONNX-Datei zum Checkpoint; fehlt sie oder ist sie älter, wird sie gebaut (gut eine Sekunde). Neben den
    /// Checkpoint, wenn dort nicht geschrieben werden kann, nach <paramref name="fallbackFolder"/>.
    /// </summary>
    public static string OnnxFor(string checkpointPath, string? fallbackFolder = null)
    {
        if (Existing(checkpointPath, fallbackFolder) is { } done) return done;
        var bytes = SyntheticDocNet.ToOnnx(TorchCheckpoint.Load(checkpointPath), shadow: false);
        foreach (var dir in Candidates(checkpointPath, fallbackFolder))
        {
            try
            {
                Directory.CreateDirectory(dir);
                var target = Path.Combine(dir, OnnxFile);
                var part = target + ".part";
                File.WriteAllBytes(part, bytes);
                File.Move(part, target, overwrite: true);
                return target;
            }
            catch (Exception e) when (e is UnauthorizedAccessException or IOException)
            {
                // nächster Ordner
            }
        }
        throw new IOException("Die ONNX-Datei für SyntheticDoc ließ sich nirgends speichern.");
    }

    private static string? Existing(string checkpointPath, string? fallbackFolder)
    {
        var stamp = File.GetLastWriteTimeUtc(checkpointPath);
        return Candidates(checkpointPath, fallbackFolder)
            .Select(d => Path.Combine(d, OnnxFile))
            .FirstOrDefault(p => File.Exists(p) && File.GetLastWriteTimeUtc(p) >= stamp);
    }

    private static IEnumerable<string> Candidates(string checkpointPath, string? fallbackFolder)
    {
        yield return Path.GetDirectoryName(checkpointPath)!;
        if (fallbackFolder is not null) yield return fallbackFolder;
    }
}
