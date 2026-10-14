using ImageConverter.Core.Ai;
using ImageConverter.Core.Ai.Onnx;
using ImageConverter.Core.Ai.SyntheticDoc;
using ImageConverter.Core.Imaging;
using ImageMagick;
using Microsoft.ML.OnnxRuntime;
using Xunit.Abstractions;

namespace ImageConverter.Tests;

/// <summary>
/// SyntheticDoc ohne Python: PyTorch-Checkpoint lesen, ONNX schreiben, das Netz nachbauen. Die volle Umwandlung prüft gegen
/// den echten Checkpoint, wenn er auf dem Rechner liegt (sonst nur der Hinweis in der Ausgabe).
/// </summary>
public class SyntheticDocTests(ITestOutputHelper output)
{
    /// <summary>
    /// Mit torch.save geschrieben (PyTorch 2.14): Lightning-artig, mit state_dict, hyper_parameters und epoch; darin ein
    /// nicht zusammenhängender Tensor (transponiert) und ein Ausschnitt.
    /// </summary>
    private const string SmallCheckpoint =
        "UEsDBAAACAgAAAAAAAAAAAAAAAAAAAAAAAAOABQAa2xlaW4vZGF0YS5wa2xGQhAAWlpaWlpaWlpaWlpaWlpaWoACfXEAKFgFAAAA" +
        "ZXBvY2hxAUsDWAoAAABzdGF0ZV9kaWN0cQJjY29sbGVjdGlvbnMKT3JkZXJlZERpY3QKcQMpUnEEKFgOAAAAbW9kZWwuYS53ZWln" +
        "aHRxBWN0b3JjaC5fdXRpbHMKX3JlYnVpbGRfdGVuc29yX3YyCnEGKChYBwAAAHN0b3JhZ2VxB2N0b3JjaApGbG9hdFN0b3JhZ2UK" +
        "cQhYAQAAADBxCVgDAAAAY3B1cQpLBnRxC1FLAEsCSwOGcQxLA0sBhnENiWgDKVJxDnRxD1JxEFgMAAAAbW9kZWwuYS5iaWFzcRFo" +
        "BigoaAdoCFgBAAAAMXESaApLAnRxE1FLAEsChXEUSwGFcRWJaAMpUnEWdHEXUnEYWBwAAABtb2RlbC5ibi5udW1fYmF0Y2hlc190" +
        "cmFja2VkcRloBigoaAdjdG9yY2gKTG9uZ1N0b3JhZ2UKcRpYAQAAADJxG2gKSwF0cRxRSwApKYloAylScR10cR5ScR9YDgAAAG1v" +
        "ZGVsLnQud2VpZ2h0cSBoBigoaAdoCFgBAAAAM3EhaApLDHRxIlFLAEsESwOGcSNLAUsEhnEkiWgDKVJxJXRxJlJxJ1gOAAAAbW9k" +
        "ZWwucy53ZWlnaHRxKGgGKChoB2gIWAEAAAA0cSloCksEdHEqUUsASwJLAoZxK0sCSwGGcSyJaAMpUnEtdHEuUnEvdVgQAAAAaHlw" +
        "ZXJfcGFyYW1ldGVyc3EwfXExKFgEAAAAZGF0YXEyfXEzKFgLAAAAYm1fb3V0X3NpemVxNF1xNShLeUuxZVgKAAAAaW1hZ2Vfc2l6" +
        "ZXE2XXE3KE2gBU0ABGV1WAUAAABtb2RlbHE4fXE5WAoAAABudW1fZmlsdGVycTpLIHN1dS5QSwcI79Gt8pgCAACYAgAAUEsDBAAA" +
        "CAgAAAAAAAAAAAAAAAAAAAAAAAAVACUAa2xlaW4vLmZvcm1hdF92ZXJzaW9uRkIhAFpaWlpaWlpaWlpaWlpaWlpaWlpaWlpaWlpa" +
        "WlpaWlpaWjFQSwcIt+/cgwEAAAABAAAAUEsDBAAACAgAAAAAAAAAAAAAAAAAAAAAAAAYADkAa2xlaW4vLnN0b3JhZ2VfYWxpZ25t" +
        "ZW50RkI1AFpaWlpaWlpaWlpaWlpaWlpaWlpaWlpaWlpaWlpaWlpaWlpaWlpaWlpaWlpaWlpaWlpaWlpaNjRQSwcIP3dx6QIAAAAC" +
        "AAAAUEsDBAAACAgAAAAAAAAAAAAAAAAAAAAAAAAPAEEAa2xlaW4vYnl0ZW9yZGVyRkI9AFpaWlpaWlpaWlpaWlpaWlpaWlpaWlpa" +
        "WlpaWlpaWlpaWlpaWlpaWlpaWlpaWlpaWlpaWlpaWlpaWlpaWlpsaXR0bGVQSwcIhT3jGQYAAAAGAAAAUEsDBAAACAgAAAAAAAAA" +
        "AAAAAAAAAAAAAAAMAEAAa2xlaW4vZGF0YS8wRkI8AFpaWlpaWlpaWlpaWlpaWlpaWlpaWlpaWlpaWlpaWlpaWlpaWlpaWlpaWlpa" +
        "WlpaWlpaWlpaWlpaWlpaWgAAAAAAAIA/AAAAQAAAQEAAAIBAAACgQFBLBwgXkOeRGAAAABgAAABQSwMEAAAICAAAAAAAAAAAAAAA" +
        "AAAAAAAAAAwALgBrbGVpbi9kYXRhLzFGQioAWlpaWlpaWlpaWlpaWlpaWlpaWlpaWlpaWlpaWlpaWlpaWlpaWlpaWlpaAAAAPwAA" +
        "wL9QSwcIbYl31ggAAAAIAAAAUEsDBAAACAgAAAAAAAAAAAAAAAAAAAAAAAAMAD4Aa2xlaW4vZGF0YS8yRkI6AFpaWlpaWlpaWlpa" +
        "WlpaWlpaWlpaWlpaWlpaWlpaWlpaWlpaWlpaWlpaWlpaWlpaWlpaWlpaWlpaWloHAAAAAAAAAFBLBwhw1udvCAAAAAgAAABQSwME" +
        "AAAICAAAAAAAAAAAAAAAAAAAAAAAAAwAPgBrbGVpbi9kYXRhLzNGQjoAWlpaWlpaWlpaWlpaWlpaWlpaWlpaWlpaWlpaWlpaWlpa" +
        "WlpaWlpaWlpaWlpaWlpaWlpaWlpaWlpaWgAAAAAAAIA/AAAAQAAAQEAAAIBAAACgQAAAwEAAAOBAAAAAQQAAEEEAACBBAAAwQVBL" +
        "Bwh4fWY+MAAAADAAAABQSwMEAAAICAAAAAAAAAAAAAAAAAAAAAAAAAwAFgBrbGVpbi9kYXRhLzRGQhIAWlpaWlpaWlpaWlpaWlpa" +
        "WlpaAACgQAAAwEAAABBBAAAgQVBLBwikZhZLEAAAABAAAABQSwMEAAAICAAAAAAAAAAAAAAAAAAAAAAAAA0ANQBrbGVpbi92ZXJz" +
        "aW9uRkIxAFpaWlpaWlpaWlpaWlpaWlpaWlpaWlpaWlpaWlpaWlpaWlpaWlpaWlpaWlpaWlpaWlozClBLBwjRnmdVAgAAAAIAAABQ" +
        "SwMEAAAICAAAAAAAAAAAAAAAAAAAAAAAABwANABrbGVpbi8uZGF0YS9zZXJpYWxpemF0aW9uX2lkRkIwAFpaWlpaWlpaWlpaWlpa" +
        "WlpaWlpaWlpaWlpaWlpaWlpaWlpaWlpaWlpaWlpaWlpaWjE2MjIwNjI5NTA3MTgwNDg3NTU1MTU4NzQ5NzE3NTI2NzU1OTUzNDRQ" +
        "SwcIH75+5igAAAAoAAAAUEsBAgAAAAAICAAAAAAAAO/RrfKYAgAAmAIAAA4AAAAAAAAAAAAAAAAAAAAAAGtsZWluL2RhdGEucGts" +
        "UEsBAgAAAAAICAAAAAAAALfv3IMBAAAAAQAAABUAAAAAAAAAAAAAAAAA6AIAAGtsZWluLy5mb3JtYXRfdmVyc2lvblBLAQIAAAAA" +
        "CAgAAAAAAAA/d3HpAgAAAAIAAAAYAAAAAAAAAAAAAAAAAFEDAABrbGVpbi8uc3RvcmFnZV9hbGlnbm1lbnRQSwECAAAAAAgIAAAA" +
        "AAAAhT3jGQYAAAAGAAAADwAAAAAAAAAAAAAAAADSAwAAa2xlaW4vYnl0ZW9yZGVyUEsBAgAAAAAICAAAAAAAABeQ55EYAAAAGAAA" +
        "AAwAAAAAAAAAAAAAAAAAVgQAAGtsZWluL2RhdGEvMFBLAQIAAAAACAgAAAAAAABtiXfWCAAAAAgAAAAMAAAAAAAAAAAAAAAAAOgE" +
        "AABrbGVpbi9kYXRhLzFQSwECAAAAAAgIAAAAAAAAcNbnbwgAAAAIAAAADAAAAAAAAAAAAAAAAABYBQAAa2xlaW4vZGF0YS8yUEsB" +
        "AgAAAAAICAAAAAAAAHh9Zj4wAAAAMAAAAAwAAAAAAAAAAAAAAAAA2AUAAGtsZWluL2RhdGEvM1BLAQIAAAAACAgAAAAAAACkZhZL" +
        "EAAAABAAAAAMAAAAAAAAAAAAAAAAAIAGAABrbGVpbi9kYXRhLzRQSwECAAAAAAgIAAAAAAAA0Z5nVQIAAAACAAAADQAAAAAAAAAA" +
        "AAAAAADgBgAAa2xlaW4vdmVyc2lvblBLAQIAAAAACAgAAAAAAAAfvn7mKAAAACgAAAAcAAAAAAAAAAAAAAAAAFIHAABrbGVpbi8u" +
        "ZGF0YS9zZXJpYWxpemF0aW9uX2lkUEsGBiwAAAAAAAAAHgMtAAAAAAAAAAAACwAAAAAAAAALAAAAAAAAAKkCAAAAAAAA+AcAAAAA" +
        "AABQSwYHAAAAAKEKAAAAAAAAAQAAAFBLBQYAAAAACwALAKkCAAD4BwAAAAA=";

    [Fact]
    public void Checkpoint_IsRead_WithoutPython()
    {
        using var stream = new MemoryStream(Convert.FromBase64String(SmallCheckpoint));
        var ckpt = TorchCheckpoint.Load(stream);
        Assert.Equal(3L, ckpt.Get("epoch"));
        Assert.Equal((121, 177), SyntheticDocNet.GridSize(ckpt));
        Assert.Equal((1440, 1024), SyntheticDocNet.TrainedInput(ckpt));

        var sd = ckpt.StateDict;
        Assert.Equal([2, 3], sd["model.a.weight"].Shape);
        Assert.Equal([0f, 1, 2, 3, 4, 5], sd["model.a.weight"].Data);
        Assert.Equal([0.5f, -1.5f], sd["model.a.bias"].Data);
        Assert.Equal([7f], sd["model.bn.num_batches_tracked"].Data);   // int64, Form ()
        // t = arange(12).reshape(3, 4); transponiert (4 × 3, Schritte 1, 4) — zusammenhängend gelesen
        Assert.Equal([4, 3], sd["model.t.weight"].Shape);
        Assert.Equal([0f, 4, 8, 1, 5, 9, 2, 6, 10, 3, 7, 11], sd["model.t.weight"].Data);
        Assert.Equal([5f, 6, 9, 10], sd["model.s.weight"].Data);
    }

    [Fact]
    public void OnnxGraph_RunsInOnnxRuntime()
    {
        // 1 × 1 × 3 × 3 Eingabe, Faltung 1 → 2 Kanäle (Kern 1, Gewichte 2 und −1, Bias 1), dann Relu
        var g = new OnnxGraph();
        var x = g.Input("x", 1, 1, "h", "w");
        var conv = g.Node("Conv", [x, g.Weight([2f, -1f], 2, 1, 1, 1), g.Weight([1f, 1f], 2)],
            ("kernel_shape", new long[] { 1, 1 }), ("pads", new long[] { 0, 0, 0, 0 }));
        g.Output(g.Node("Relu", [conv]), "y", 1, 2, "h", "w");
        using var session = new InferenceSession(g.ToModel());
        float[] input = [0, 1, 2, -1, -2, 3, 0.5f, -0.5f, 4];
        using var value = OrtValue.CreateTensorValueFromMemory(input, [1, 1, 3, 3]);
        using var outs = session.Run(new RunOptions(), ["x"], [value], ["y"]);
        var y = outs[0].GetTensorDataAsSpan<float>().ToArray();
        Assert.Equal(input.Select(v => Math.Max(0, 2 * v + 1)).Concat(input.Select(v => Math.Max(0, -v + 1))), y);
    }

    [Fact]
    public void RealCheckpoint_BecomesAWorkingOnnxModel()
    {
        var path = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ImageConverter", "KI-Modelle", "syntheticdoc", "highres_121x177.ckpt"),
            Path.Combine(AppContext.BaseDirectory, "KI-Modelle", "syntheticdoc", "highres_121x177.ckpt"),
        }.FirstOrDefault(File.Exists);
        if (path is null)
        {
            output.WriteLine("Checkpoint highres_121x177.ckpt nicht vorhanden – Umwandlung nicht geprüft.");
            return;
        }
        var ckpt = TorchCheckpoint.Load(path);
        Assert.Equal(345, ckpt.StateDict.Count);
        using var session = new InferenceSession(SyntheticDocNet.ToOnnx(ckpt, shadow: false));
        Assert.Equal(["bm_map"], session.OutputNames);
        // offene Eingabegröße: ein grober Lauf auf 720 × 512; ein gleichmäßig graues Bild gibt ein Gitter in −1 … 1
        var input = Enumerable.Repeat(0.4f, 3 * 720 * 512).ToArray();
        using var value = OrtValue.CreateTensorValueFromMemory(input, [1, 3, 720, 512]);
        using var outs = session.Run(new RunOptions(), ["image"], [value], ["bm_map"]);
        var bm = outs[0].GetTensorDataAsSpan<float>().ToArray();
        Assert.Equal(2 * 121 * 177, bm.Length);
        Assert.All(bm, v => Assert.InRange(v, -1f, 1f));
        // das Gitter läuft von links nach rechts: x-Werte der ersten Spalte kleiner als die der letzten
        Assert.True(bm[60 * 177] < bm[60 * 177 + 176]);
    }

    private static string? RealCheckpoint() => new[]
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ImageConverter", "KI-Modelle", "syntheticdoc", "highres_121x177.ckpt"),
        Path.Combine(AppContext.BaseDirectory, "KI-Modelle", "syntheticdoc", "highres_121x177.ckpt"),
    }.FirstOrDefault(File.Exists);

    /// <summary>
    /// Ganzer Lauf mit Schattenkarte: ein weißes Blatt mit Textzeilen auf dunklem Grund, die rechte Hälfte des Fotos im
    /// Schatten (60 % Licht, weicher Übergang). Die Karte muss rechts dunkler sein als links, und das Ergebnis mit
    /// „Schatten entfernen“ rechts heller als ohne.
    /// </summary>
    [Fact]
    public void RealModel_FindsTheShadow_AndRenderLightensIt()
    {
        if (RealCheckpoint() is not { } path)
        {
            output.WriteLine("Checkpoint highres_121x177.ckpt nicht vorhanden – Schattenkarte nicht geprüft.");
            return;
        }
        const int w = 900, h = 1200;
        var px = new byte[w * h * 3];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var page = x is >= 120 and < 780 && y is >= 100 and < 1100;
            var ink = page && y >= 180 && y < 1020 && (y - 180) % 40 < 8 && x >= 170 && x < 730;
            double v = !page ? 0.25 : ink ? 0.1 : 0.95;
            var light = 1 - 0.4 / (1 + Math.Exp(-(x - 450) / 25.0));   // links 1, rechts 0,6
            var k = (y * w + x) * 3;
            px[k] = px[k + 1] = px[k + 2] = (byte)Math.Round(255 * v * light);
        }
        using var photo = new MagickImage();
        photo.ReadPixels(px, new PixelReadSettings(w, h, StorageType.Char, "RGB"));

        var ckpt = TorchCheckpoint.Load(path);
        using var grid = new InferenceSession(SyntheticDocNet.ToOnnx(ckpt, shadow: false));
        using var shadow = new InferenceSession(SyntheticDocNet.ToOnnx(ckpt, shadow: true));
        var result = SyntheticDocDewarp.Run(grid, shadow, photo, 28, removeShadows: true, CancellationToken.None);
        var shading = Assert.IsType<DewarpShading>(result.Shading);
        Assert.True(shading.On);
        Assert.InRange(shading.Strength, 0.01, 1);

        double Mean(double u0, double u1)
        {
            double s = 0;
            var n = 0;
            for (var v = 0.2; v <= 0.8; v += 0.05)
            for (var u = u0; u <= u1; u += 0.02, n++) s += shading.Sample(u, v).G;
            return s / n;
        }
        double left = Mean(0.05, 0.3), right = Mean(0.7, 0.95);
        output.WriteLine($"Licht links {left:0.000}, rechts {right:0.000}, Stärke {shading.Strength:0.00}");
        Assert.True(right < left - 0.1, $"Schatten rechts nicht erkannt: links {left:0.000}, rechts {right:0.000}");

        // Rendern: rechts auf dem Papier mit Schattenentfernung heller als ohne
        double PaperRight(DewarpGrid g)
        {
            using var img = Dewarp.MapFor(new DewarpSpec([], Grid: g), w, h)!.Render(photo);
            var rgb = AiEnhancer.ReadPixels(img, "RGB");
            int iw = (int)img.Width, ih = (int)img.Height;
            var values = new List<double>();
            for (var y = ih / 5; y < 4 * ih / 5; y++)
            for (var x = 3 * iw / 4; x < 9 * iw / 10; x++) values.Add(rgb[(y * iw + x) * 3 + 1] / 65535.0);
            values.Sort();
            return values[(int)(values.Count * 0.9)];   // Papier, nicht Tinte
        }
        double with = PaperRight(result), without = PaperRight(result with { Shading = shading with { On = false } });
        output.WriteLine($"Papier rechts: ohne {without:0.000}, mit Schattenentfernung {with:0.000}");
        Assert.True(with > without + 0.05);
    }
}
