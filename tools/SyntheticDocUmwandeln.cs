#:project ../ImageConverter.Core/ImageConverter.Core.csproj
// Converts a SyntheticDoc checkpoint (PyTorch, e.g. self-trained) to ONNX for the app, with a test run. No Python needed.
// Usage (build output outside the project, otherwise bin/ and obj/ are created in ImageConverter.Core):
//   dotnet run tools/SyntheticDocUmwandeln.cs --property:UseArtifactsOutput=true --property:ArtifactsPath=<ordner> -- \
//       <checkpoint.ckpt> [ausgabe.onnx] [--schatten]
// Without an output path: syntheticdoc.onnx next to the checkpoint, with --schatten syntheticdoc-schatten.onnx (adds the
// shadow map, twice the computing time) — the app builds both itself.
using ImageConverter.Core.Ai.Onnx;
using ImageConverter.Core.Ai.SyntheticDoc;
using Microsoft.ML.OnnxRuntime;

var files = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();
if (files.Length == 0)
{
    Console.Error.WriteLine("Aufruf: SyntheticDocUmwandeln.cs <checkpoint.ckpt> [ausgabe.onnx] [--schatten]");
    return 1;
}
var shadow = args.Contains("--schatten");
var target = files.Length > 1 ? files[1]
    : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(files[0]))!, shadow ? SyntheticDocModel.ShadowOnnxFile : SyntheticDocModel.OnnxFile);

var clock = System.Diagnostics.Stopwatch.StartNew();
var ckpt = TorchCheckpoint.Load(files[0]);
var (rows, cols) = SyntheticDocNet.GridSize(ckpt);
var (ih, iw) = SyntheticDocNet.TrainedInput(ckpt);
Console.WriteLine($"Checkpoint: {ckpt.StateDict.Count} Gewichte, Epoche {ckpt.Get("epoch") ?? "?"}, Gitter {rows} × {cols}, trainiert auf {ih} × {iw}");
File.WriteAllBytes(target, SyntheticDocNet.ToOnnx(ckpt, shadow));
Console.WriteLine($"ONNX: {target} ({new FileInfo(target).Length / 1_000_000.0:0.0} MB) in {clock.ElapsedMilliseconds} ms");

// Test run on a grey image at the trained size: the grid must lie within −1 … 1 and run from left to right
using var session = new InferenceSession(target);
var input = Enumerable.Repeat(0.4f, 3 * ih * iw).ToArray();
using var value = OrtValue.CreateTensorValueFromMemory(input, [1, 3, ih, iw]);
clock.Restart();
using var runOptions = new RunOptions();   // keep alive until the run has finished
using var outputs = session.Run(runOptions, ["image"], [value], ["bm_map"]);
var bm = outputs[0].GetTensorDataAsSpan<float>().ToArray();
var ok = bm.All(v => v is >= -1 and <= 1) && bm[(rows / 2) * cols] < bm[(rows / 2) * cols + cols - 1];
Console.WriteLine($"Probelauf: {clock.ElapsedMilliseconds} ms, Gitter {(ok ? "plausibel" : "UNPLAUSIBEL")} (Werte {bm.Min():0.000} … {bm.Max():0.000})");
return ok ? 0 : 2;
