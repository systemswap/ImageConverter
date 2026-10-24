using ImageConverter.Core.Ai.Onnx;

namespace ImageConverter.Core.Ai.Upscale;

/// <summary>
/// PiSA-SR on disk: the downloaded weights and the ONNX files built from them.
/// </summary>
/// <remarks>
/// The weights are <see cref="AiModelCatalog.PiSaSr"/> with the parts of Stable Diffusion 2.1. On first use the app builds
/// the empty-prompt embedding and the three ONNX files next to the LoRA (just under a minute, 3.6 GB), and again whenever
/// one of the sources is newer. Works like <see cref="SyntheticDoc.SyntheticDocModel"/>.
/// </remarks>
public static class PiSaSrModel
{
    /// <summary>File name of the empty-prompt embedding (1 × 77 × 1024), computed from the text encoder or taken from a saved copy.</summary>
    public const string EmbeddingFile = "leere-beschreibung.safetensors";

    /// <summary>Paths of the source files.</summary>
    /// <param name="Lora">PiSA-SR checkpoint with the LoRA weights.</param>
    /// <param name="Unet">SD 2.1 UNet weights.</param>
    /// <param name="Vae">SD 2.1 VAE weights.</param>
    /// <param name="Text">SD 2.1 text encoder weights, or null if not installed.</param>
    public sealed record Sources(string Lora, string Unet, string Vae, string? Text);

    /// <summary>Returns the source files if all needed ones are installed.</summary>
    /// <param name="store">Model store to search.</param>
    /// <returns>
    /// The sources, or null if one is missing. The text encoder is required only as long as the empty-prompt embedding
    /// does not exist.
    /// </returns>
    public static Sources? Find(AiModelStore store)
    {
        if (store.PathOf(AiModelCatalog.PiSaSr) is not { } lora
            || store.PathOf(AiModelCatalog.Sd21Unet) is not { } unet
            || store.PathOf(AiModelCatalog.Sd21Vae) is not { } vae) return null;
        var text = store.PathOf(AiModelCatalog.Sd21Text);
        if (text is null && !File.Exists(Path.Combine(Path.GetDirectoryName(lora)!, EmbeddingFile))) return null;
        return new Sources(lora, unet, vae, text);
    }

    /// <summary>Returns the folder of the ONNX files: the folder of the LoRA.</summary>
    /// <param name="s">Source files.</param>
    /// <returns>The folder path.</returns>
    public static string FolderOf(Sources s) => Path.GetDirectoryName(s.Lora)!;

    /// <summary>Returns whether the ONNX files have to be built (again).</summary>
    /// <param name="s">Source files.</param>
    /// <param name="half">Half precision (graphics card): separate "-fp16" files, built only when needed.</param>
    /// <returns>True if one of the four files (encoder, decoder, UNet and its external data) is missing or older than LoRA, UNet or VAE.</returns>
    public static bool NeedsBuild(Sources s, bool half = false)
    {
        var dir = FolderOf(s);
        var stamp = new[] { s.Lora, s.Unet, s.Vae }.Max(File.GetLastWriteTimeUtc);
        return new[] { PiSaSrNet.EncoderFile, PiSaSrNet.DecoderFile, PiSaSrNet.UnetFile, PiSaSrNet.UnetFile + ".data" }
            .Select(f => Path.Combine(dir, PiSaSrNet.FileName(f, half)))
            .Any(p => !File.Exists(p) || File.GetLastWriteTimeUtc(p) < stamp);
    }

    /// <summary>Returns the three ONNX files (encoder, UNet, decoder), building them if necessary.</summary>
    /// <param name="s">Source files.</param>
    /// <param name="progress">Receives the build progress, 0…1; not called if nothing needs to be built.</param>
    /// <param name="ct">Cancels the build between its stages.</param>
    /// <param name="half">Half precision (graphics card): separate "-fp16" files.</param>
    /// <returns>Paths of the VAE encoder, the UNet and the VAE decoder.</returns>
    /// <exception cref="FileNotFoundException">The embedding is missing and the text encoder is not installed.</exception>
    public static (string Encoder, string Unet, string Decoder) OnnxFor(Sources s, IProgress<double>? progress = null,
        CancellationToken ct = default, bool half = false)
    {
        var dir = FolderOf(s);
        if (NeedsBuild(s, half))
        {
            var embedding = Path.Combine(dir, EmbeddingFile);
            float[] context;
            if (File.Exists(embedding))
            {
                using var e = new SafeTensors(embedding);
                context = e.Get("prompt_embeds").Data;
            }
            else
            {
                using var text = new SafeTensors(s.Text ?? throw new FileNotFoundException("Text-Encoder von Stable Diffusion 2.1 fehlt."));
                context = ClipText.EmptyPrompt(text);
                SafeTensors.Write(embedding + ".part", "prompt_embeds", context, 1, 77, 1024);
                File.Move(embedding + ".part", embedding, overwrite: true);
            }
            progress?.Report(0.02);
            PiSaSrNet.BuildAll(s.Unet, s.Vae, s.Lora, context, dir, new PiSaSrNet.Part(progress, 0.02, 0.98), ct, half);
        }
        string F(string file) => Path.Combine(dir, PiSaSrNet.FileName(file, half));
        return (F(PiSaSrNet.EncoderFile), F(PiSaSrNet.UnetFile), F(PiSaSrNet.DecoderFile));
    }
}
