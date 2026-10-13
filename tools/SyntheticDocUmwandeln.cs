#:project ../ImageConverter.Core/ImageConverter.Core.csproj
// SyntheticDoc-Checkpoint (PyTorch, z. B. selbst trainiert) → ONNX für die App, mit Probelauf. Ohne Python.
// Aufruf (Bauausgabe außerhalb des Projekts, sonst entstehen bin/ und obj/ in ImageConverter.Core):
//   dotnet run tools/SyntheticDocUmwandeln.cs --property:UseArtifactsOutput=true --property:ArtifactsPath=<ordner> -- \
//       <checkpoint.ckpt> [ausgabe.onnx] [--schatten]
// Ohne Ausgabe: syntheticdoc.onnx neben den Checkpoint (wie die App). --schatten baut die Schattenkarte mit ein
// (die App braucht sie nicht; sie verdoppelt die Rechenzeit).
using ImageConverter.Core.Ai.SyntheticDoc;
using Microsoft.ML.OnnxRuntime;

var files = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();
if (files.Length == 0)
{
    Console.Error.WriteLine("Aufruf: SyntheticDocUmwandeln.cs <checkpoint.ckpt> [ausgabe.onnx] [--schatten]");
    return 1;
}
var shadow = args.Contains("--schatten");
var target = files.Length > 1 ? files[1] : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(files[0]))!, SyntheticDocModel.OnnxFile);

var clock = System.Diagnostics.Stopwatch.StartNew();
var ckpt = TorchCheckpoint.Load(files[0]);
var (rows, cols) = SyntheticDocNet.GridSize(ckpt);
var (ih, iw) = SyntheticDocNet.TrainedInput(ckpt);
Console.WriteLine($"Checkpoint: {ckpt.StateDict.Count} Gewichte, Epoche {ckpt.Get("epoch") ?? "?"}, Gitter {rows} × {cols}, trainiert auf {ih} × {iw}");
File.WriteAllBytes(target, SyntheticDocNet.ToOnnx(ckpt, shadow));
Console.WriteLine($"ONNX: {target} ({new FileInfo(target).Length / 1_000_000.0:0.0} MB) in {clock.ElapsedMilliseconds} ms");

// Probelauf auf einem grauen Bild in der trainierten Größe: Gitter muss in −1 … 1 liegen und von links nach rechts laufen
using var session = new InferenceSession(target);
var input = Enumerable.Repeat(0.4f, 3 * ih * iw).ToArray();
using var value = OrtValue.CreateTensorValueFromMemory(input, [1, 3, ih, iw]);
clock.Restart();
using var outputs = session.Run(new RunOptions(), ["image"], [value], ["bm_map"]);
var bm = outputs[0].GetTensorDataAsSpan<float>().ToArray();
var ok = bm.All(v => v is >= -1 and <= 1) && bm[(rows / 2) * cols] < bm[(rows / 2) * cols + cols - 1];
Console.WriteLine($"Probelauf: {clock.ElapsedMilliseconds} ms, Gitter {(ok ? "plausibel" : "UNPLAUSIBEL")} (Werte {bm.Min():0.000} … {bm.Max():0.000})");
return ok ? 0 : 2;
