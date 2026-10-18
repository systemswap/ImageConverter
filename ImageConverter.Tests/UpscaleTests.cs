using ImageConverter.Core.Ai;
using ImageConverter.Core.Ai.Onnx;
using ImageConverter.Core.Ai.Upscale;
using ImageConverter.Core.Imaging;
using ImageMagick;

namespace ImageConverter.Tests;

public class UpscaleTests
{
    /// <summary>Ersatzmodell ×4: nächster Nachbar, dazu ein Versatz (wie ein Modell, das Helligkeit verschiebt).</summary>
    private sealed class BlockyModel(float shift = 0, int tile = 32) : ITileModel
    {
        public int Scale => 4;
        public int TileSize => tile;
        public int Calls { get; private set; }

        public float[] Run(float[] input, int width, int height, CancellationToken ct)
        {
            Calls++;
            int ow = width * 4, oh = height * 4;
            var output = new float[3 * ow * oh];
            for (var c = 0; c < 3; c++)
            for (var y = 0; y < oh; y++)
            for (var x = 0; x < ow; x++)
                output[c * ow * oh + y * ow + x] = input[c * width * height + y / 4 * width + x / 4] + shift;
            return output;
        }
    }

    private static MagickImage Pattern(int w, int h, bool alpha = false)
    {
        var rng = new Random(3);
        var px = new ushort[w * h * (alpha ? 4 : 3)];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var i = (y * w + x) * (alpha ? 4 : 3);
            // weicher Verlauf mit etwas Struktur
            px[i] = (ushort)(32768 + 20000 * Math.Sin(x * 0.3) * Math.Cos(y * 0.2));
            px[i + 1] = (ushort)(10000 + 400 * x + rng.Next(3000));
            px[i + 2] = (ushort)(50000 - 300 * y);
            if (alpha) px[i + 3] = (ushort)(x < w / 2 ? 65535 : 20000);
        }
        var bytes = new byte[px.Length * 2];
        Buffer.BlockCopy(px, 0, bytes, 0, bytes.Length);
        var image = new MagickImage();
        image.ReadPixels(bytes, new PixelReadSettings((uint)w, (uint)h, StorageType.Short, alpha ? "RGBA" : "RGB"));
        return image;
    }

    [Theory]
    [InlineData(250, 64, 16, 1)]
    [InlineData(150, 96, 16, 1)]
    [InlineData(64, 64, 16, 1)]
    [InlineData(30, 64, 16, 1)]
    [InlineData(1001, 128, 12, 8)]
    public void TileAxis_WeightsAddUpToOne_Everywhere(int length, int tile, int overlap, int align)
    {
        var axis = new TileAxis(length, tile, overlap, align);
        var sum = new double[length];
        for (var k = 0; k < axis.Count; k++)
        {
            Assert.True(axis.Starts[k] >= 0 && axis.Starts[k] + axis.Size <= length);
            for (var i = 0; i < axis.Size; i++) sum[axis.Starts[k] + i] += axis.Weights[k][i];
        }
        Assert.All(sum, s => Assert.Equal(1, s, 5));

        var fine = axis.Scaled(8, overlap);
        var fineSum = new double[fine.Length];
        for (var k = 0; k < fine.Count; k++)
        for (var i = 0; i < fine.Size; i++) fineSum[fine.Starts[k] + i] += fine.Weights[k][i];
        Assert.All(fineSum, s => Assert.Equal(1, s, 5));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Compose_InBands_IsPixelIdentical_ToOnePiece(bool alpha)
    {
        // 1100 Quellzeilen: drei Bänder (512, 512, 76); zufälliges Roh-Ergebnis, Treue und Details zwischen 0 und 1
        const int w = 70, h = 1100;
        var rng = new Random(3);
        var ch = alpha ? 4 : 3;
        var src = new ushort[w * h * ch];
        for (var i = 0; i < src.Length; i++) src[i] = (ushort)rng.Next(65536);
        using var source = ImageConverter.Core.Ai.AiEnhancer.WritePixels(src, w, h, alpha ? "RGBA" : "RGB");
        var result = new UpscaleResult(source, src, ch);
        for (var i = 0; i < result.Raw.Length; i++) result.Raw[i] = (ushort)rng.Next(65536);

        using var bands = result.Compose(0.6, 0.7);
        var all = new SourceRect(0, 0, w, h);
        using var piece = ImageConverter.Core.Ai.AiEnhancer.WritePixels(result.ComposeRegion(all, 0.6, 0.7), w * 4, h * 4,
            alpha ? "RGBA" : "RGB");
        Assert.Equal((piece.Width, piece.Height, piece.HasAlpha, piece.Depth), (bands.Width, bands.Height, bands.HasAlpha, bands.Depth));
        var map = alpha ? "RGBA" : "RGB";
        Assert.Equal(piece.GetPixels().ToShortArray(map), bands.GetPixels().ToShortArray(map));
    }

    [Fact]
    public void BackProject_MakesTheDownscaledResult_TheOriginal()
    {
        const int w = 24, h = 20;
        var rng = new Random(1);
        var s = Enumerable.Range(0, 3 * w * h).Select(_ => (float)rng.NextDouble()).ToArray();
        // Schlechte Vergrößerung: Blöcke, dazu zu hell
        var e = new float[3 * 16 * w * h];
        for (var c = 0; c < 3; c++)
        for (var y = 0; y < 4 * h; y++)
        for (var x = 0; x < 4 * w; x++) e[c * 16 * w * h + y * 4 * w + x] = s[c * w * h + y / 4 * w + x / 4] + 0.1f;

        static double Error(float[] a, float[] b) => Math.Sqrt(a.Zip(b, (x, y) => (x - y) * (x - y)).Average());
        var before = Error(Resample4.Down(e, w, h, 3), s);
        Resample4.BackProject(e, s, w, h);
        var after = Error(Resample4.Down(e, w, h, 3), s);
        Assert.True(after < before / 8, $"vorher {before:0.0000}, nachher {after:0.0000}");
    }

    [Fact]
    public void Up_KeepsFlatAreas_AndSmoothRamps()
    {
        const int w = 10, h = 6;
        var flat = Enumerable.Repeat(0.4f, 3 * w * h).ToArray();
        Assert.All(Resample4.Up(flat, w, h, 3, Resample4.Kernel.Lanczos), v => Assert.Equal(0.4f, v, 4));
        Assert.All(Resample4.Up(flat, w, h, 3, Resample4.Kernel.Bicubic), v => Assert.Equal(0.4f, v, 4));
        Assert.All(Resample4.Down(Resample4.Up(flat, w, h, 3, Resample4.Kernel.Bicubic), w, h, 3), v => Assert.Equal(0.4f, v, 4));
    }

    [Fact]
    public void RunTiles_FillsEveryPixel_AndReportsEveryTile()
    {
        using var image = Pattern(70, 45);
        var model = new BlockyModel();
        var area = 0;
        var result = UpscaleEngine.RunTiles(image, model, null, CancellationToken.None, (_, r) => area += r.Width * r.Height);
        Assert.Equal(70 * 45, area);
        Assert.Equal((280, 180), (result.Width, result.Height));

        // Roh (ohne Treue, nur KI): nächster Nachbar des Originals
        using var raw = result.Compose(0, 1);
        var src = image.GetPixels().ToShortArray("RGB")!;
        var up = raw.GetPixels().ToShortArray("RGB")!;
        for (var y = 0; y < 180; y += 7)
        for (var x = 0; x < 280; x += 5)
        for (var c = 0; c < 3; c++)
            Assert.InRange(up[(y * 280 + x) * 3 + c] - src[(y / 4 * 70 + x / 4) * 3 + c], -2, 2);
    }

    [Fact]
    public void Fidelity_PullsAShiftedModelBack_ToTheOriginal()
    {
        using var image = Pattern(48, 40);
        var result = UpscaleEngine.RunTiles(image, new BlockyModel(shift: 0.08f), null, CancellationToken.None);
        var src = Planes(image);

        double ErrorDown(MagickImage up)
        {
            var e = Planes(up);
            var down = Resample4.Down(e, 48, 40, 3);
            return Math.Sqrt(down.Zip(src, (a, b) => (double)(a - b) * (a - b)).Average());
        }
        using var raw = result.Compose(0, 1);
        using var faithful = result.Compose(1, 1);
        Assert.True(ErrorDown(raw) > 0.05);
        Assert.True(ErrorDown(faithful) < 0.01, $"mit Treue {ErrorDown(faithful):0.0000}");
    }

    [Fact]
    public void ComposeRegion_MatchesTheWholeImage()
    {
        using var image = Pattern(300, 140, alpha: true);
        var result = UpscaleEngine.RunTiles(image, new BlockyModel(shift: 0.05f, tile: 48), null, CancellationToken.None);
        var whole = result.ComposeRegion(new SourceRect(0, 0, 300, 140), 1, 0.7);
        var region = new SourceRect(131, 37, 90, 61);   // über Kachelgrenzen der Zusammensetzung (128)
        var part = result.ComposeRegion(region, 1, 0.7);
        var maxDiff = 0;
        for (var y = 0; y < region.Height * 4; y++)
        for (var x = 0; x < region.Width * 4; x++)
        for (var c = 0; c < 4; c++)
        {
            var a = whole[(((region.Y * 4 + y) * 1200) + region.X * 4 + x) * 4 + c];
            var b = part[((y * region.Width * 4) + x) * 4 + c];
            maxDiff = Math.Max(maxDiff, Math.Abs(a - b));
        }
        // Rückprojektion mit begrenztem Rand: winzige Abweichungen dürfen sein (unter 1/1000)
        Assert.True(maxDiff < 65, $"größte Abweichung {maxDiff}");
    }

    [Fact]
    public void Export_UsesTheUpscaledImage_WhenEnlarging()
    {
        using var image = Pattern(40, 30);
        var result = UpscaleEngine.RunTiles(image, new BlockyModel(), null, CancellationToken.None);
        using var up = result.Compose(1, 1);
        var options = new ExportOptions(OutputFormat.Png, 90, false, 160, 120, ResampleFilter.Lanczos3, true, 0, 0, 1, 72,
            MetadataMode.KeepAll, ColorMode.KeepProfile, Upscaled: up);
        Assert.Same(up, ImageExporter.ResizeBasis(image, options));
        Assert.Same(image, ImageExporter.ResizeBasis(image, options with { Width = 40, Height = 30 }));
        Assert.Same(image, ImageExporter.ResizeBasis(image, options with { Upscaled = null }));

        // Ergebnis in Zielgröße kommt aus dem Bild ×4 (hier pixelgleich zum zusammengesetzten Bild)
        using var rendered = ImageExporter.Render(image, options);
        Assert.Equal((160u, 120u), (rendered.Width, rendered.Height));
    }

    [Fact]
    public void SafeTensors_WriteAndRead_RoundTrip()
    {
        var path = Path.Combine(Path.GetTempPath(), $"st-{Guid.NewGuid():N}.safetensors");
        try
        {
            var data = Enumerable.Range(0, 2 * 3 * 5).Select(i => i * 0.25f - 3).ToArray();
            SafeTensors.Write(path, "prompt_embeds", data, 2, 3, 5);
            using var st = new SafeTensors(path);
            var t = st.Get("prompt_embeds");
            Assert.Equal([2, 3, 5], t.Shape);
            Assert.Equal(data, t.Data);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Catalog_PiSaSr_BringsItsParts()
    {
        var pisa = AiModelCatalog.PiSaSr;
        Assert.All(pisa.Requires, id => Assert.Equal(AiModelKind.Part, AiModelCatalog.Find(id)!.Kind));
        Assert.Equal(AiModelCatalog.Allround, Upscalers.ModelOf(UpscaleMethod.Fast));
        Assert.Null(Upscalers.ModelOf(UpscaleMethod.Filter));
        Assert.True(Upscalers.Estimate(UpscaleMethod.Generative, 500, 300) > Upscalers.Estimate(UpscaleMethod.Medium, 500, 300));
    }

    /// <summary>
    /// Über 4× zwei Durchgänge: das Zwischenbild wird auf ein Viertel des Ziels gebracht (höchstens ×4), damit der zweite
    /// Durchgang genau beim Ziel landet; bis 4× oder abgewählt bleibt es bei einem. Die Zeit zählt beide Durchgänge.
    /// </summary>
    [Fact]
    public void Plan_TwoPassesOnlyBeyondFour_BaseIsQuarterOfTarget()
    {
        Assert.Equal(new UpscalePlan(2, 1000, 600), Upscalers.Plan(300, 180, 4000, 2400, twice: true));
        Assert.Equal(new UpscalePlan(1, 300, 180), Upscalers.Plan(300, 180, 4000, 2400, twice: false));
        Assert.Equal(new UpscalePlan(1, 300, 180), Upscalers.Plan(300, 180, 1200, 720, twice: true));
        Assert.Equal(new UpscalePlan(2, 1200, 720), Upscalers.Plan(300, 180, 6000, 3600, twice: true));   // über 16×: Basis höchstens ×4
        Assert.Equal(new UpscalePlan(2, 301, 180), Upscalers.Plan(300, 180, 1201, 720, twice: true));     // nur knapp darüber
        Assert.True(Upscalers.BeyondOnePass(300, 180, 1201, 720));
        Assert.False(Upscalers.BeyondOnePass(300, 180, 1200, 720));

        var plan = Upscalers.Plan(300, 180, 4000, 2400, twice: true);
        var both = Upscalers.Estimate(UpscaleMethod.Medium, 300, 180, plan);
        Assert.Equal(Upscalers.Estimate(UpscaleMethod.Medium, 300, 180) + Upscalers.Estimate(UpscaleMethod.Medium, 1000, 600), both);
        // Ausschnitt: beide Durchgänge im selben Verhältnis kleiner (seit 0.5.105 nach Kacheln geschätzt, nicht nach
        // Fläche — ein Viertel der Fläche ist daher nicht genau ein Viertel der Zeit)
        var part = Upscalers.Estimate(UpscaleMethod.Medium, 300, 180, plan, 150, 90);
        Assert.Equal(Upscalers.Estimate(UpscaleMethod.Medium, 150, 90) + Upscalers.Estimate(UpscaleMethod.Medium, 500, 300), part);
        Assert.True(part < both);
    }

    [Fact]
    public void OnnxGraph_HalfPrecisionWeights_RunAndStayClose()
    {
        // float32 hinein → float16 rechnen (Gewicht fp16) → float32 heraus, wie PiSA-SR auf der Grafikkarte
        using var g = new OnnxGraph();
        var x = g.Input("x", 1, 4);
        var h = g.Node("Cast", [x], ("to", 10L));
        h = g.Node("Mul", [h, g.WeightHalf([0.5f, 1.25f, -2f, 3.1416f], 4)]);
        g.Output(g.Node("Cast", [h], ("to", 1L)), "y", 1, 4);
        using var session = new Microsoft.ML.OnnxRuntime.InferenceSession(g.ToModel());
        using var input = Microsoft.ML.OnnxRuntime.OrtValue.CreateTensorValueFromMemory(new[] { 2f, 2f, 2f, 2f }, [1, 4]);
        using var outputs = session.Run(new Microsoft.ML.OnnxRuntime.RunOptions(), ["x"], [input], ["y"]);
        var y = outputs[0].GetTensorDataAsSpan<float>().ToArray();
        Assert.Equal([1f, 2.5f, -4f], y[..3]);
        Assert.Equal(6.2832f, y[3], 2);   // halbe Genauigkeit: drei Stellen
    }

    [Fact]
    public void FormatWithEnd_ShowsTheClockTime_FromOneMinute()
    {
        var now = new DateTime(2026, 10, 14, 15, 30, 0);
        Assert.Equal("40 s", Upscalers.FormatWithEnd(TimeSpan.FromSeconds(40), now));
        Assert.Equal("12 min, fertig gegen 15:42", Upscalers.FormatWithEnd(TimeSpan.FromMinutes(12), now));
        Assert.Equal("10 h 00 min, fertig gegen 15.10. 01:30", Upscalers.FormatWithEnd(TimeSpan.FromHours(10), now));
    }

    private static float[] Planes(MagickImage image)
    {
        int w = (int)image.Width, h = (int)image.Height;
        var px = image.GetPixels().ToShortArray("RGB")!;
        var p = new float[3 * w * h];
        for (var i = 0; i < w * h; i++)
        for (var c = 0; c < 3; c++) p[c * w * h + i] = px[i * 3 + c] / 65535f;
        return p;
    }
}
