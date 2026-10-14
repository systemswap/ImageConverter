#:project ../ImageConverter.Core/ImageConverter.Core.csproj
// Upscaler der App an einem Bild messen: Zeit, und — wenn das wahre große Bild dabei ist — wie nah das Ergebnis daran
// liegt (PSNR), roh und mit Treue-Sicherung. Schreibt <name>-roh.png und <name>-treu.png in den Ausgabeordner.
// Aufruf (Bauausgabe außerhalb des Projekts):
//   dotnet run tools/UpscaleMessen.cs --property:UseArtifactsOutput=true --property:ArtifactsPath=<ordner> -- \
//       <pisasr|onnx-datei> <modellordner|-> <bild> <ausgabeordner> [<wahres-bild>] [--gpu]
// --gpu: auf der Grafikkarte (Windows: DirectML). --fp16: PiSA-SR in halber Genauigkeit (Dateien „-fp16“).
// pisasr: die drei ONNX-Dateien im Modellordner; sonst eine ×4-Kachelmodell-Datei (Real-ESRGAN x4v3, UltraSharpV2).
using ImageConverter.Core.Ai;
using ImageConverter.Core.Ai.Upscale;
using ImageMagick;
using Microsoft.ML.OnnxRuntime;

if (args.Length < 4)
{
    Console.Error.WriteLine("Aufruf: UpscaleMessen.cs <pisasr|onnx-datei> <modellordner|-> <bild> <ausgabeordner> [<wahres-bild>]");
    return 1;
}
var gpu = args.Contains("--gpu");
var half = args.Contains("--fp16");
args = args.Where(a => a is not ("--gpu" or "--fp16")).ToArray();
var (method, dir, file, outDir) = (args[0], args[1], args[2], args[3]);
Directory.CreateDirectory(outDir);
using var source = new MagickImage(file);
var name = Path.GetFileNameWithoutExtension(file);
var clock = System.Diagnostics.Stopwatch.StartNew();
using var options = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
if (gpu)
{
    options.EnableMemoryPattern = false;
    options.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
    options.AppendExecutionProvider_DML(0);
}
Console.WriteLine(gpu ? $"Grafikkarte (DirectML), Laufzeit {OrtEnv.Instance().GetVersionString()}" : "CPU");
var last = -1;
var progress = new Progress<double>(p =>
{
    var pct = (int)(p * 100);
    if (pct / 10 == last / 10) return;
    last = pct;
    Console.WriteLine($"  {pct,3} %  {clock.Elapsed:mm\\:ss}");
});

UpscaleResult result;
if (method == "pisasr")
{
    using var enc = new InferenceSession(Path.Combine(dir, PiSaSrNet.FileName(PiSaSrNet.EncoderFile, half)), options);
    using var unet = new InferenceSession(Path.Combine(dir, PiSaSrNet.FileName(PiSaSrNet.UnetFile, half)), options);
    using var dec = new InferenceSession(Path.Combine(dir, PiSaSrNet.FileName(PiSaSrNet.DecoderFile, half)), options);
    Console.WriteLine($"geladen in {clock.Elapsed:mm\\:ss}");
    clock.Restart();
    result = PiSaSrRun.Run(source, enc, unet, dec, progress, CancellationToken.None);
}
else
{
    using var session = new InferenceSession(method, options);
    var model = new AiModel("messen", "Messen", "", "", AiModelKind.Enhance, "", 0, "", false, "", "", false, "", Scale: 4,
        TileSize: 128, FixedTile: true);
    result = UpscaleEngine.RunTiles(source, new OnnxTileModel(session, model), progress, CancellationToken.None);
}
var seconds = clock.Elapsed.TotalSeconds;
Console.WriteLine($"gerechnet in {seconds:0.0} s ({result.Width} × {result.Height})");

using var raw = result.Compose(0, 1);
using var treu = result.Compose(1, 1);
raw.Write(Path.Combine(outDir, name + "-roh.png"));
treu.Write(Path.Combine(outDir, name + "-treu.png"));
if (args.Length > 4)
{
    using var truth = new MagickImage(args[4]);
    double Psnr(MagickImage a)
    {
        var x = a.GetPixels().ToShortArray("RGB")!;
        var y = truth.GetPixels().ToShortArray("RGB")!;
        double s = 0;
        for (var i = 0; i < x.Length; i++)
        {
            var d = (x[i] - y[i]) / 65535.0;
            s += d * d;
        }
        return 10 * Math.Log10(1 / Math.Max(s / x.Length, 1e-12));
    }
    Console.WriteLine($"PSNR zum wahren Bild: roh {Psnr(raw):0.00} dB, mit Treue {Psnr(treu):0.00} dB");
}
return 0;
