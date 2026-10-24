#:project ../ImageConverter.Core/ImageConverter.Core.csproj
// Compares two SyntheticDoc checkpoints (e.g. the weights in use against new weights from the authors or an own training):
// for each image, AI dewarping as in the app, the results old | new side by side as JPEG, plus how far the grids
// differ and the computing time. No Python needed; writes nothing next to the checkpoints.
// Usage (build output outside the project, otherwise bin/ and obj/ are created in ImageConverter.Core):
//   dotnet run tools/SyntheticDocVergleich.cs --property:UseArtifactsOutput=true --property:ArtifactsPath=<ordner> -- \
//       <alt.ckpt> <neu.ckpt> <ausgabeordner> <bild> [<bild> …]
// Assessment: see docs/KI-BEGRADIGEN.md, section "Updates des Originals".
using ImageConverter.Core.Ai.Onnx;
using ImageConverter.Core.Ai.SyntheticDoc;
using ImageConverter.Core.Imaging;
using ImageMagick;
using Microsoft.ML.OnnxRuntime;

if (args.Length < 4)
{
    Console.Error.WriteLine("Aufruf: SyntheticDocVergleich.cs <alt.ckpt> <neu.ckpt> <ausgabeordner> <bild> [<bild> …]");
    return 1;
}
using var options = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
(InferenceSession Grid, InferenceSession Shadow) Open(string ckptPath)
{
    var ckpt = TorchCheckpoint.Load(ckptPath);
    var (rows, cols) = SyntheticDocNet.GridSize(ckpt);
    Console.WriteLine($"{Path.GetFileName(ckptPath)}: Epoche {ckpt.Get("epoch") ?? "?"}, Gitter {rows} × {cols}");
    if ((rows, cols) != (SyntheticDocDewarp.GridHeight, SyntheticDocDewarp.GridWidth))
        throw new InvalidOperationException($"Gitter {rows} × {cols} – die App rechnet mit 121 × 177.");
    return (new InferenceSession(SyntheticDocNet.ToOnnx(ckpt, shadow: false), options),
            new InferenceSession(SyntheticDocNet.ToOnnx(ckpt, shadow: true), options));
}
var old = Open(args[0]);
var neu = Open(args[1]);
Directory.CreateDirectory(args[2]);
Console.WriteLine();
Console.WriteLine($"{"Bild",-28} {"Abstand der Gitter",20} {"Zeit alt",9} {"Zeit neu",9}");
var all = new List<double>();
foreach (var file in args.Skip(3))
{
    using var img = new MagickImage(file);
    img.AutoOrient();
    int w = (int)img.Width, h = (int)img.Height;
    var focal = Dewarp.Focal35Of(img);
    var clock = System.Diagnostics.Stopwatch.StartNew();
    var a = SyntheticDocDewarp.Run(old.Grid, old.Shadow, img, focal, true, CancellationToken.None);
    var ta = clock.ElapsedMilliseconds;
    clock.Restart();
    var b = SyntheticDocDewarp.Run(neu.Grid, neu.Shadow, img, focal, true, CancellationToken.None);
    var tb = clock.ElapsedMilliseconds;

    // Distance: per grid point, in pixels of the photo — how much the result changes (not which one is better)
    double sum = 0, max = 0;
    for (var k = 0; k < a.Sx.Length; k++)
    {
        var d = Math.Sqrt(Math.Pow((a.Sx[k] - b.Sx[k]) * w, 2) + Math.Pow((a.Sy[k] - b.Sy[k]) * h, 2));
        (sum, max) = (sum + d, Math.Max(max, d));
    }
    var mean = sum / a.Sx.Length;
    all.Add(mean);

    var recipe = new GeometryRecipe();
    using var ra = recipe.AiDewarpedInView(a, w, h, focal).Apply(img)!;
    using var rb = recipe.AiDewarpedInView(b, w, h, focal).Apply(img)!;
    rb.Resize(0, ra.Height);
    using var both = new MagickImageCollection { ra.Clone(), rb.Clone() };
    using var row = both.AppendHorizontally();
    if (row.Height > 1600) row.Resize(0, 1600);
    row.Quality = 85;
    var name = Path.GetFileNameWithoutExtension(file).Replace(' ', '_');
    row.Write(Path.Combine(args[2], $"{name}-alt-neu.jpg"));
    Console.WriteLine($"{name,-28} {$"Ø {mean:0.0} px, max {max:0}",20} {ta,6} ms {tb,6} ms");
}
Console.WriteLine();
Console.WriteLine($"Mittel über {all.Count} Bilder: {all.Average():0.0} px. Bilder (links alt, rechts neu): {Path.GetFullPath(args[2])}");
return 0;
