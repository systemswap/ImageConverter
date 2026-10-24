#:project ../ImageConverter.Core/ImageConverter.Core.csproj
// Converts PiSA-SR (Stable Diffusion 2.1 + PiSA LoRA) into three ONNX files for the app, without Python — the same step the
// app performs itself on the first upscale. With --pruefen: runs the finished files against reference values from the
// original (PyTorch) (raw float32 data + shapes.json, written by referenz.py in the measurement setup, see docs/KI-UPSCALING.md).
// Usage (build output outside the project, otherwise bin/ and obj/ are created in ImageConverter.Core):
//   dotnet run tools/PiSaSrUmwandeln.cs --property:UseArtifactsOutput=true --property:ArtifactsPath=<ordner> -- \
//       <quellordner> <zielordner> [--pruefen <referenzordner>] [--nur-pruefen]
// Source folder: sd21/unet, sd21/vae (fp16 safetensors), pisa_sr.pkl, plus leere-beschreibung.safetensors or
// sd21/text_encoder/model.fp16.safetensors (then the empty prompt embedding is computed and — if both are present — compared;
// --nur-text: only that, --ohne-text: use the stored one). --fp16: half precision (for the graphics card, files "-fp16");
// --gpu: run the check on the graphics card (DirectML).
using System.Text.Json;
using ImageConverter.Core.Ai.Onnx;
using ImageConverter.Core.Ai.Upscale;
using Microsoft.ML.OnnxRuntime;

var files = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList();
var refIndex = Array.IndexOf(args, "--pruefen");
var refDir = refIndex >= 0 && refIndex + 1 < args.Length ? args[refIndex + 1] : null;
if (refDir is not null) files.Remove(refDir);
if (files.Count < 2)
{
    Console.Error.WriteLine("Aufruf: PiSaSrUmwandeln.cs <quellordner> <zielordner> [--pruefen <referenzordner>] [--nur-pruefen]");
    return 1;
}
var (source, target) = (files[0], files[1]);
var half = args.Contains("--fp16");

if (!args.Contains("--nur-pruefen"))
{
    var clock = System.Diagnostics.Stopwatch.StartNew();
    var last = -1;
    var embedding = Path.Combine(source, "leere-beschreibung.safetensors");
    var textPath = Path.Combine(source, "sd21/text_encoder/model.fp16.safetensors");
    float[]? stored = null, computed = null;
    if (File.Exists(embedding))
    {
        using var e = new SafeTensors(embedding);
        stored = e.Get("prompt_embeds").Data;
    }
    if (File.Exists(textPath) && !args.Contains("--ohne-text"))
    {
        using var text = new SafeTensors(textPath);
        computed = ClipText.EmptyPrompt(text);
        Console.WriteLine($"leere Beschreibung gerechnet in {clock.Elapsed:mm\\:ss}");
    }
    if (stored is not null && computed is not null)
    {
        var max = stored.Zip(computed, (a, b) => Math.Abs(a - b)).Max();
        Console.WriteLine($"leere Beschreibung: gerechnet gegen gespeichert max. Abweichung {max:0.0e0} (Werte bis {stored.Max(Math.Abs):0.0})");
    }
    var context = stored ?? computed ?? throw new FileNotFoundException("Weder leere Beschreibung noch Text-Encoder gefunden.");
    if (args.Contains("--nur-text")) return 0;
    PiSaSrNet.BuildAll(Path.Combine(source, "sd21/unet/diffusion_pytorch_model.fp16.safetensors"),
        Path.Combine(source, "sd21/vae/diffusion_pytorch_model.fp16.safetensors"), Path.Combine(source, "pisa_sr.pkl"), context, target, half: half,
        progress:
        new Reporter(p =>
        {
            var pct = (int)(p * 100);
            if (pct / 10 == last / 10) return;
            last = pct;
            Console.WriteLine($"  {pct,3} %  {clock.Elapsed:mm\\:ss}");
        }));
    foreach (var f in Directory.GetFiles(target, "pisasr-*"))
        Console.WriteLine($"{Path.GetFileName(f),-32} {new FileInfo(f).Length / 1_000_000.0,9:0.0} MB");
    Console.WriteLine($"gebaut in {clock.Elapsed:mm\\:ss}");
}

if (refDir is null) return 0;

// ------------------------------------------------------------ Check against PyTorch
using var shapeDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(refDir, "shapes.json")));
var shapes = shapeDoc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.EnumerateArray().Select(e => e.GetInt32()).ToArray());
float[] Load(string name)
{
    var bytes = File.ReadAllBytes(Path.Combine(refDir, name + ".bin"));
    var data = new float[bytes.Length / 4];
    Buffer.BlockCopy(bytes, 0, data, 0, bytes.Length);
    return data;
}

using var options = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
if (args.Contains("--gpu"))
{
    options.EnableMemoryPattern = false;
    options.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
    options.AppendExecutionProvider_DML(0);
}
var ok = true;
void Check(string file, string inName, string outName, string refIn, string refOut, double tolerance)
{
    var clock = System.Diagnostics.Stopwatch.StartNew();
    using var session = new InferenceSession(Path.Combine(target, file), options);
    var load = clock.ElapsedMilliseconds;
    var input = Load(refIn);
    using var value = OrtValue.CreateTensorValueFromMemory(input, shapes[refIn].Select(d => (long)d).ToArray());
    clock.Restart();
    using var runOptions = new RunOptions();   // keep alive until the run has finished
    using var outputs = session.Run(runOptions, [inName], [value], [outName]);
    var got = outputs[0].GetTensorDataAsSpan<float>().ToArray();
    var want = Load(refOut);
    var shape = outputs[0].GetTensorTypeAndShape().Shape;
    if (got.Length != want.Length)
    {
        Console.WriteLine($"{file}: Form {string.Join("×", shape)} statt {string.Join("×", shapes[refOut])} — FALSCH");
        ok = false;
        return;
    }
    double maxDiff = 0, sumSq = 0, refSq = 0;
    for (var i = 0; i < got.Length; i++)
    {
        var d = Math.Abs(got[i] - want[i]);
        maxDiff = Math.Max(maxDiff, d);
        sumSq += d * d;
        refSq += want[i] * (double)want[i];
    }
    var rel = Math.Sqrt(sumSq / Math.Max(refSq, 1e-20));
    var pass = rel < tolerance;
    ok &= pass;
    Console.WriteLine($"{file,-26} laden {load,6} ms, rechnen {clock.ElapsedMilliseconds,6} ms, max. Abweichung {maxDiff:0.0e0}, relativ {rel:0.0e0} — {(pass ? "stimmt" : "FALSCH")}");
}

// Half precision: about three digits — a relative error up to 1 % counts as equal
var tolerance = half ? 1e-2 : 1e-3;
Check(PiSaSrNet.FileName(PiSaSrNet.EncoderFile, half), "image", "latent", "vae_img", "vae_mean", tolerance);
Check(PiSaSrNet.FileName(PiSaSrNet.DecoderFile, half), "latent", "image", "dec_in", "dec_out", tolerance);
Check(PiSaSrNet.FileName(PiSaSrNet.UnetFile, half), "latent", "pred", "unet_in", "unet_out", tolerance);
return ok ? 0 : 2;

sealed class Reporter(Action<double> a) : IProgress<double>
{
    public void Report(double v) => a(v);
}
