using ImageConverter.Core.Ai.Onnx;

namespace ImageConverter.Core.Ai.Upscale;

/// <summary>
/// PiSA-SR auf der Platte: geladen werden die Gewichte (<see cref="AiModelCatalog.PiSaSr"/> mit den Teilen von Stable
/// Diffusion 2.1); die leere Beschreibung und die drei ONNX-Dateien baut die App beim ersten Gebrauch (knapp eine Minute,
/// 3,6 GB) neben die LoRA — neu, wenn eine Quelle jünger ist. Wie <see cref="SyntheticDoc.SyntheticDocModel"/>.
/// </summary>
public static class PiSaSrModel
{
    /// <summary>Leere Beschreibung (1 × 77 × 1024), aus dem Text-Encoder gerechnet oder aus einer Sicherung.</summary>
    public const string EmbeddingFile = "leere-beschreibung.safetensors";

    public sealed record Sources(string Lora, string Unet, string Vae, string? Text);

    /// <summary>Die Quelldateien, wenn alle da sind (der Text-Encoder nur, solange die leere Beschreibung fehlt).</summary>
    public static Sources? Find(AiModelStore store)
    {
        if (store.PathOf(AiModelCatalog.PiSaSr) is not { } lora
            || store.PathOf(AiModelCatalog.Sd21Unet) is not { } unet
            || store.PathOf(AiModelCatalog.Sd21Vae) is not { } vae) return null;
        var text = store.PathOf(AiModelCatalog.Sd21Text);
        if (text is null && !File.Exists(Path.Combine(Path.GetDirectoryName(lora)!, EmbeddingFile))) return null;
        return new Sources(lora, unet, vae, text);
    }

    /// <summary>Wo die ONNX-Dateien liegen: neben der LoRA.</summary>
    public static string FolderOf(Sources s) => Path.GetDirectoryName(s.Lora)!;

    public static bool NeedsBuild(Sources s)
    {
        var dir = FolderOf(s);
        var stamp = new[] { s.Lora, s.Unet, s.Vae }.Max(File.GetLastWriteTimeUtc);
        return new[] { PiSaSrNet.EncoderFile, PiSaSrNet.DecoderFile, PiSaSrNet.UnetFile, PiSaSrNet.UnetFile + ".data" }
            .Select(f => Path.Combine(dir, f))
            .Any(p => !File.Exists(p) || File.GetLastWriteTimeUtc(p) < stamp);
    }

    /// <summary>Die drei ONNX-Dateien (Encoder, UNet, Decoder); fehlen sie, werden sie gebaut.</summary>
    public static (string Encoder, string Unet, string Decoder) OnnxFor(Sources s, IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        var dir = FolderOf(s);
        if (NeedsBuild(s))
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
            PiSaSrNet.BuildAll(s.Unet, s.Vae, s.Lora, context, dir, new PiSaSrNet.Part(progress, 0.02, 0.98), ct);
        }
        return (Path.Combine(dir, PiSaSrNet.EncoderFile), Path.Combine(dir, PiSaSrNet.UnetFile), Path.Combine(dir, PiSaSrNet.DecoderFile));
    }
}
