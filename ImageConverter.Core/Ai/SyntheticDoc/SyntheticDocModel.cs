namespace ImageConverter.Core.Ai.SyntheticDoc;

/// <summary>
/// Das Modell auf der Platte: heruntergeladen (oder selbst trainiert) wird der PyTorch-Checkpoint
/// (<see cref="AiModelCatalog.SyntheticDoc"/>, <c>highres_121x177.ckpt</c>); die zwei ONNX-Dateien daneben baut
/// <see cref="SyntheticDocNet"/> beim ersten Gebrauch — und neu, wenn der Checkpoint jünger ist (eigenes Training: den neuen
/// Checkpoint unter demselben Namen ablegen). Zwei Dateien, weil ONNX Runtime immer das ganze Netz rechnet: der grobe Lauf
/// braucht nur das Gitter (halb so lang), der genaue auch die Schattenkarte.
/// </summary>
public static class SyntheticDocModel
{
    /// <summary>Nur das Gitter, offene Eingabegröße (grober Lauf).</summary>
    public const string OnnxFile = "syntheticdoc.onnx";

    /// <summary>Gitter und Schattenkarte (genauer Lauf).</summary>
    public const string ShadowOnnxFile = "syntheticdoc-schatten.onnx";

    /// <summary>Müssen die ONNX-Dateien zum Checkpoint (neu) gebaut werden?</summary>
    public static bool NeedsBuild(string checkpointPath, string? fallbackFolder = null) =>
        Existing(checkpointPath, fallbackFolder) is null;

    /// <summary>
    /// Die ONNX-Dateien zum Checkpoint; fehlen sie oder sind sie älter, werden sie gebaut (gut zwei Sekunden). Neben den
    /// Checkpoint, wenn dort nicht geschrieben werden kann, nach <paramref name="fallbackFolder"/>.
    /// </summary>
    public static (string Grid, string Shadow) OnnxFor(string checkpointPath, string? fallbackFolder = null)
    {
        if (Existing(checkpointPath, fallbackFolder) is { } done) return done;
        var ckpt = TorchCheckpoint.Load(checkpointPath);
        var grid = SyntheticDocNet.ToOnnx(ckpt, shadow: false);
        var shadow = SyntheticDocNet.ToOnnx(ckpt, shadow: true);
        foreach (var dir in Candidates(checkpointPath, fallbackFolder))
        {
            try
            {
                Directory.CreateDirectory(dir);
                return (Save(dir, OnnxFile, grid), Save(dir, ShadowOnnxFile, shadow));
            }
            catch (Exception e) when (e is UnauthorizedAccessException or IOException)
            {
                // nächster Ordner
            }
        }
        throw new IOException("Die ONNX-Dateien für SyntheticDoc ließen sich nirgends speichern.");
    }

    private static string Save(string dir, string name, byte[] bytes)
    {
        var target = Path.Combine(dir, name);
        var part = target + ".part";
        File.WriteAllBytes(part, bytes);
        File.Move(part, target, overwrite: true);
        return target;
    }

    private static (string Grid, string Shadow)? Existing(string checkpointPath, string? fallbackFolder)
    {
        var stamp = File.GetLastWriteTimeUtc(checkpointPath);
        bool Fresh(string p) => File.Exists(p) && File.GetLastWriteTimeUtc(p) >= stamp;
        foreach (var dir in Candidates(checkpointPath, fallbackFolder))
        {
            string grid = Path.Combine(dir, OnnxFile), shadow = Path.Combine(dir, ShadowOnnxFile);
            if (Fresh(grid) && Fresh(shadow)) return (grid, shadow);
        }
        return null;
    }

    private static IEnumerable<string> Candidates(string checkpointPath, string? fallbackFolder)
    {
        yield return Path.GetDirectoryName(checkpointPath)!;
        if (fallbackFolder is not null) yield return fallbackFolder;
    }
}
