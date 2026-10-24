using ImageConverter.Core.Ai.Onnx;

namespace ImageConverter.Core.Ai.SyntheticDoc;

/// <summary>
/// The SyntheticDoc model on disk: the downloaded (or self-trained) PyTorch checkpoint and the ONNX files built from it.
/// </summary>
/// <remarks>
/// The checkpoint is <see cref="AiModelCatalog.SyntheticDoc"/> (<c>highres_121x177.ckpt</c>). <see cref="SyntheticDocNet"/>
/// builds the two ONNX files next to it on first use, and again whenever the checkpoint is newer (for own training, place
/// the new checkpoint under the same name). There are two files because ONNX Runtime always computes the whole network:
/// the coarse run needs only the grid (half the time), the accurate run also the shading map.
/// </remarks>
public static class SyntheticDocModel
{
    /// <summary>File name of the grid-only model with dynamic input size (coarse run).</summary>
    public const string OnnxFile = "syntheticdoc.onnx";

    /// <summary>File name of the model with grid and shading map (accurate run).</summary>
    public const string ShadowOnnxFile = "syntheticdoc-schatten.onnx";

    /// <summary>Returns whether the ONNX files for the checkpoint have to be built (again).</summary>
    /// <param name="checkpointPath">Path of the checkpoint.</param>
    /// <param name="fallbackFolder">Alternative folder for the ONNX files, or null.</param>
    /// <returns>True if neither folder holds both ONNX files at least as new as the checkpoint.</returns>
    public static bool NeedsBuild(string checkpointPath, string? fallbackFolder = null) =>
        Existing(checkpointPath, fallbackFolder) is null;

    /// <summary>
    /// Returns the ONNX files for the checkpoint, building them if they are missing or older (a little over two seconds).
    /// </summary>
    /// <param name="checkpointPath">Path of the checkpoint.</param>
    /// <param name="fallbackFolder">Folder used if the checkpoint folder is not writable, or null.</param>
    /// <returns>Paths of the grid-only model and of the model with shading map.</returns>
    /// <remarks>
    /// The files are written next to the checkpoint, or to <paramref name="fallbackFolder"/> if that fails. Each file is
    /// written as ".part" first and then renamed.
    /// </remarks>
    /// <exception cref="IOException">The files could not be saved in any of the folders.</exception>
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
                // try the next folder
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
