using ImageConverter.Core.Ai;
using ImageConverter.Core.Ai.Upscale;
using ImageConverter.Core.Imaging;
using ImageMagick;

namespace ImageConverter.Tests;

/// <summary>Tests background removal (mask, strokes, edges, colours) with a stand-in model instead of BiRefNet.</summary>
public class BackgroundTests
{
    private const int W = 600, H = 400;
    private static readonly (float R, float G, float B) Green = (0.15f, 0.6f, 0.15f);

    /// <summary>Takes everything that is not green as subject (hard threshold, at its coarse size as a real model would).</summary>
    private sealed class ColourModel(bool seeBlue = true) : ISegmentModel
    {
        public int Size => 128;
        public int Calls;
        public float[] Run(float[] rgb, CancellationToken ct)
        {
            Calls++;
            var s = Size * Size;
            var output = new float[s];
            for (var i = 0; i < s; i++)
                output[i] = rgb[i] > 0.4f || (seeBlue && rgb[2 * s + i] > 0.4f) ? 1 : 0;
            return output;
        }
    }

    /// <summary>Green background with a fine pattern, a red disc at (200, 200) r 80 and a blue disc at (460, 200) r 50.</summary>
    private static MagickImage Scene()
    {
        var img = new MagickImage(MagickColors.Black, W, H) { Depth = 16 };
        var data = new ushort[W * H * 3];
        for (var y = 0; y < H; y++)
        for (var x = 0; x < W; x++)
        {
            var (r, g, b) = Green;
            if ((x / 4 + y / 4) % 2 == 0) g += 0.1f;   // pattern, so that blurring is measurable
            // soft edge over 3 px: the colour of the edge pixels is a mix with the background
            var red = Math.Clamp(80.5 - Math.Sqrt((x - 200.0) * (x - 200) + (y - 200.0) * (y - 200)), 0, 3) / 3;
            var blue = Math.Clamp(50.5 - Math.Sqrt((x - 460.0) * (x - 460) + (y - 200.0) * (y - 200)), 0, 3) / 3;
            r = (float)(r * (1 - red) + 0.9 * red);
            g = (float)(g * (1 - red) + 0.1 * red);
            b = (float)(b * (1 - red) + 0.1 * red);
            r = (float)(r * (1 - blue) + 0.1 * blue);
            g = (float)(g * (1 - blue) + 0.1 * blue);
            b = (float)(b * (1 - blue) + 0.9 * blue);
            var i = (y * W + x) * 3;
            (data[i], data[i + 1], data[i + 2]) = ((ushort)(r * 65535), (ushort)(g * 65535), (ushort)(b * 65535));
        }
        using (var pixels = img.GetPixelsUnsafe()) pixels.SetArea(0, 0, W, H, data);
        return img;
    }

    private static readonly SourceRect Whole = new(0, 0, W, H);

    private static ushort[] Rgba(MagickImage img)
    {
        using var p = img.GetPixelsUnsafe();
        return p.ToShortArray(0, 0, img.Width, img.Height, "RGBA")!;
    }

    private static float AlphaAt(ushort[] rgba, int x, int y) => rgba[(y * W + x) * 4 + 3] / 65535f;

    private static MagickImage Remove(MagickImage img, ISegmentModel model, IReadOnlyList<BrushStroke> strokes,
        BackgroundMode mode = BackgroundMode.Transparent, double blur = 0.5)
    {
        var mask = BackgroundRemover.Detect(img, Whole, model, CancellationToken.None);
        var alpha = BackgroundRemover.Edit(mask, strokes, r => BackgroundRemover.Detect(img, r, model, CancellationToken.None));
        return BackgroundRemover.Compose(img, mask, alpha, mode, blur, CancellationToken.None);
    }

    [Fact]
    public void Automatic_KeepsSubjects_AndMakesBackgroundTransparent()
    {
        using var img = Scene();
        var model = new ColourModel();
        using var result = Remove(img, model, []);
        var px = Rgba(result);

        Assert.Equal(1, model.Calls);
        Assert.True(AlphaAt(px, 200, 200) > 0.99f, $"Mitte rot: {AlphaAt(px, 200, 200)}");
        Assert.True(AlphaAt(px, 460, 200) > 0.99f, $"Mitte blau: {AlphaAt(px, 460, 200)}");
        Assert.True(AlphaAt(px, 20, 20) < 0.01f, $"Hintergrund: {AlphaAt(px, 20, 20)}");
        // Edges at full resolution (the model only sees 128 px): sharp within a few pixels of the real edge
        Assert.True(AlphaAt(px, 200 + 74, 200) > 0.9f, $"6 px innen: {AlphaAt(px, 274, 200)}");
        Assert.True(AlphaAt(px, 200 + 87, 200) < 0.1f, $"6 px außen: {AlphaAt(px, 287, 200)}");
    }

    [Fact]
    public void RedStroke_RemovesTheWholeObjectItTouches()
    {
        using var img = Scene();
        using var result = Remove(img, new ColourModel(), [new BrushStroke([(455, 195), (470, 205)], 6)]);
        var px = Rgba(result);

        Assert.True(AlphaAt(px, 200, 200) > 0.99f, "rote Scheibe bleibt");
        foreach (var (x, y) in new[] { (460, 160), (420, 200), (500, 200), (460, 240) })
            Assert.True(AlphaAt(px, x, y) < 0.05f, $"blaue Scheibe weg bei {x},{y}: {AlphaAt(px, x, y)}");
    }

    [Fact]
    public void GreenStroke_ProtectsAnObjectFromRed()
    {
        using var img = Scene();
        using var result = Remove(img, new ColourModel(),
            [new BrushStroke([(455, 195), (470, 205)], 6), new BrushStroke([(460, 180)], 6, Keep: true)]);
        Assert.True(AlphaAt(Rgba(result), 460, 230) > 0.99f, "grün geschützte Scheibe bleibt");
    }

    [Fact]
    public void GreenStroke_AddsWhatTheZoomedRunFinds()
    {
        using var img = Scene();
        var coarse = new ColourModel(seeBlue: false);   // the first run misses the blue disc
        var mask = BackgroundRemover.Detect(img, Whole, coarse, CancellationToken.None);
        SourceRect? asked = null;
        var alpha = BackgroundRemover.Edit(mask, [new BrushStroke([(460, 200)], 10, Keep: true)], r =>
        {
            asked = r;
            return BackgroundRemover.Detect(img, r, new ColourModel(), CancellationToken.None);
        });
        using var result = BackgroundRemover.Compose(img, mask, alpha, BackgroundMode.Transparent, 0, CancellationToken.None);
        var px = Rgba(result);

        Assert.NotNull(asked);
        Assert.True(asked.Value.X <= 450 && asked.Value.X + asked.Value.Width >= 470, $"Zoom um den Strich: {asked}");
        Assert.True(AlphaAt(px, 460, 200) > 0.99f && AlphaAt(px, 460, 235) > 0.9f, "blaue Scheibe ergänzt");
        Assert.True(AlphaAt(px, 200, 200) > 0.99f, "rote Scheibe bleibt");
    }

    [Fact]
    public void SoftEdge_LosesTheBackgroundColour()
    {
        using var img = Scene();
        using var result = Remove(img, new ColourModel(), []);
        var before = Rgba(img.Clone() is MagickImage c ? AddAlpha(c) : throw new InvalidOperationException());
        var after = Rgba(result);
        // Edge pixel half way: originally half green; the foreground colour estimate brings it close to the red of the disc
        var i = (200 * W + 200 + 79) * 4;
        Assert.True(after[i + 1] < before[i + 1] - 6000, $"Grün am Rand: vorher {before[i + 1]}, nachher {after[i + 1]}");
    }

    private static MagickImage AddAlpha(MagickImage img)
    {
        img.Alpha(AlphaOption.Opaque);
        return img;
    }

    [Fact]
    public void Blur_KeepsSubject_BlursBackground_StaysOpaque()
    {
        using var img = Scene();
        using var result = Remove(img, new ColourModel(), [], BackgroundMode.Blur, 0.5);
        var before = Rgba(AddAlpha((MagickImage)img.Clone()));
        var after = Rgba(result);

        Assert.False(result.HasAlpha && AlphaAt(after, 20, 20) < 0.99f, "bleibt deckend");
        var centre = (200 * W + 200) * 4;
        Assert.InRange(after[centre], before[centre] - 300, before[centre] + 300);
        // The 4-px pattern is gone in the background
        static int Spread(ushort[] p, int y) =>
            Enumerable.Range(20, 40).Select(x => (int)p[(y * W + x) * 4 + 1]).Max() - Enumerable.Range(20, 40).Select(x => (int)p[(y * W + x) * 4 + 1]).Min();
        Assert.True(Spread(after, 30) < Spread(before, 30) / 5, $"Muster: vorher {Spread(before, 30)}, nachher {Spread(after, 30)}");
        // The red of the subject does not bleed into the background next to it
        var near = (200 * W + 200 + 95) * 4;
        Assert.True(after[near] < 0.3 * 65535, $"Rot neben dem Motiv: {after[near]}");
    }

    /// <summary>The removed background must not stay under alpha 0: it would be in the saved file and every filter would compute from it.</summary>
    [Fact]
    public void InvisiblePixels_KeepNothingOfTheOldBackground()
    {
        using var img = Scene();
        using var result = Remove(img, new ColourModel(), []);
        var px = Rgba(result);
        foreach (var (x, y) in new[] { (5, 5), (330, 30), (590, 390) })
        {
            var i = (y * W + x) * 4;
            Assert.Equal(0, px[i + 3]);
            Assert.True(px[i + 1] < 0.3 * 65535, $"Grün des alten Hintergrunds bei {x},{y}: {px[i + 1]}");
        }

        // Also outside the computed region
        var model = new ColourModel();
        var mask = BackgroundRemover.Detect(img, new SourceRect(100, 100, 220, 200), model, CancellationToken.None);
        using var part = BackgroundRemover.Compose(img, mask, BackgroundRemover.Edit(mask, [], null), BackgroundMode.Transparent, 0, CancellationToken.None);
        var outside = (20 * W + 20) * 4;
        var inPart = Rgba(part);
        Assert.Equal(0, inPart[outside + 3]);
        Assert.True(inPart[outside + 1] < 0.3 * 65535, $"Grün außerhalb des Ausschnitts: {inPart[outside + 1]}");
    }

    /// <summary>Filters after the step see only the subject: a different background far from it must not change the subject.</summary>
    [Fact]
    public void Filters_DoNotSeeTheRemovedBackground()
    {
        float[] Filtered(MagickImage source)
        {
            var mask = BackgroundRemover.Detect(source, Whole, new FixedDiscs(), CancellationToken.None);
            using var cut = BackgroundRemover.Compose(source, mask, BackgroundRemover.Edit(mask, [], null), BackgroundMode.Transparent, 0, CancellationToken.None);
            var px = Pixels.FromMagick(cut);
            return Adjust.Apply(px, W, H, new Adjustments(Brightness: 10, Shadows: 30, Dehaze: 40));
        }

        using var a = Scene();
        using var b = Scene();
        using (var pixels = b.GetPixelsUnsafe())
        {
            var data = pixels.ToShortArray(0, 0, W, H, "RGB")!;
            for (var y = 0; y < H; y++)
            for (var x = 0; x < W; x++)
            {
                var near = Math.Min(Math.Sqrt((x - 200.0) * (x - 200) + (y - 200.0) * (y - 200)) - 80, Math.Sqrt((x - 460.0) * (x - 460) + (y - 200.0) * (y - 200)) - 50);
                if (near > 40) data[(y * W + x) * 3] = data[(y * W + x) * 3 + 1] = data[(y * W + x) * 3 + 2] = 65535;
            }
            pixels.SetArea(0, 0, W, H, data);
        }

        var (fa, fb) = (Filtered(a), Filtered(b));
        var max = 0f;
        foreach (var (cx, cy, r) in new[] { (200, 200, 70), (460, 200, 40) })
        for (var y = cy - r; y <= cy + r; y++)
        for (var x = cx - r; x <= cx + r; x++)
        {
            if ((x - cx) * (x - cx) + (y - cy) * (y - cy) > r * r) continue;
            for (var c = 0; c < 3; c++) max = Math.Max(max, Math.Abs(fa[(y * W + x) * 4 + c] - fb[(y * W + x) * 4 + c]));
        }
        Assert.True(max * 255 < 1, $"Motiv hängt vom entfernten Hintergrund ab: {max * 255:0.00} Stufen");
    }

    /// <summary>A transparent image with one visible block, whose position and size are chosen freely.</summary>
    private static MagickImage VisibleBlock(int imageW, int imageH, int x0, int y0, int bw, int bh)
    {
        var img = new MagickImage(MagickColors.Black, (uint)imageW, (uint)imageH);
        img.Alpha(AlphaOption.Set);
        img.Depth = 16;
        var data = new ushort[imageW * imageH * 4];
        for (var y = 0; y < imageH; y++)
        for (var x = 0; x < imageW; x++)
        {
            var i = (y * imageW + x) * 4;
            var inside = x >= x0 && x < x0 + bw && y >= y0 && y < y0 + bh;
            // Relative to the block, so that a crop of it holds exactly the same pixels
            int bx = x - x0, by = y - y0;
            data[i] = (ushort)(((bx * 37 + by * 11) % 200 + 30) * 257);
            data[i + 1] = (ushort)(((bx * 13 + by * 29) % 180 + 40) * 257);
            data[i + 2] = (ushort)(((bx + by) % 150 + 50) * 257);
            data[i + 3] = inside ? (ushort)65535 : (ushort)0;
        }
        using (var pixels = img.GetPixelsUnsafe()) pixels.SetArea(0, 0, (uint)imageW, (uint)imageH, data);
        return img;
    }

    private static ExportOptions Look(int width, int height) => new(OutputFormat.Png, 90, true, width, height, ResampleFilter.Lanczos3, false,
        0.3, 0.5, 1, 96, MetadataMode.KeepAll, ColorMode.KeepProfile, new Adjustments(Brightness: 10, Shadows: 20, Highlights: 20, Dehaze: 30), HighBitDepth: true, ColorDenoise: 0.3);

    /// <summary>What lies under fully transparent pixels does not go into the file; what is visible stays exactly as it is.</summary>
    [Theory]
    [InlineData(OutputFormat.Png)]
    [InlineData(OutputFormat.Tiff)]
    public void SavedFile_HoldsNothingUnderFullyTransparentPixels(OutputFormat format)
    {
        using var img = VisibleBlock(320, 240, 100, 60, 120, 90);
        var before = Rgba(img);
        var options = Look(320, 240) with { Format = format, Lossless = true, Adjustments = null, Denoise = 0, ColorDenoise = 0, SharpenAmount = 0 };
        using var ms = new MemoryStream();
        ImageExporter.Encode(img, options, ms);
        ms.Position = 0;
        using var saved = new MagickImage(ms);
        var after = Rgba(saved);
        for (var i = 0; i < after.Length; i += 4)
        {
            if (before[i + 3] == 0)
            {
                Assert.Equal(0, after[i + 3]);
                Assert.Equal(0, after[i]);
                Assert.Equal(0, after[i + 1]);
                Assert.Equal(0, after[i + 2]);
            }
            else
            {
                for (var c = 0; c < 4; c++) Assert.Equal(before[i + c], after[i + c]);
            }
        }
    }

    [Fact]
    public void ClearInvisible_LeavesOpaqueImagesAlone()
    {
        using var opaque = Scene();
        var before = Rgba(opaque);
        Pixels.ClearInvisible(opaque);
        Assert.Equal(before, Rgba(opaque));
        Assert.False(opaque.HasAlpha);
    }

    /// <summary>The rule: the filters behave as if the image had been cropped to its visible pixels.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Filters_OnTransparentImage_EqualACropToTheVisiblePixels(bool cached)
    {
        const int W2 = 640, H2 = 480, X0 = 200, Y0 = 120, Bw = 240, Bh = 180;
        using var whole = VisibleBlock(W2, H2, X0, Y0, Bw, Bh);
        using var crop = VisibleBlock(Bw, Bh, 0, 0, Bw, Bh);
        using var cache = new LookCache();
        using var fromWhole = ImageExporter.Render(whole, Look(W2, H2), null, cached ? cache : null);
        using var fromCrop = ImageExporter.Render(crop, Look(Bw, Bh));

        var a = Rgba(fromWhole);
        var b = Rgba(fromCrop);
        for (var y = 0; y < Bh; y++)
        for (var x = 0; x < Bw * 4; x++)
            Assert.Equal(b[y * Bw * 4 + x], a[((y + Y0) * W2 + X0) * 4 + x]);
        // Outside the rectangle nothing was computed
        using var before = whole.Clone();
        var untouched = Rgba((MagickImage)before);
        var outside = (10 * W2 + 10) * 4;
        for (var c = 0; c < 4; c++) Assert.Equal(untouched[outside + c], a[outside + c]);
    }

    /// <summary>Size estimate and export must see the same image.</summary>
    [Fact]
    public void CachedAndPlainRender_AgreeOnTransparentImages()
    {
        using var img = VisibleBlock(640, 480, 200, 120, 240, 180);
        using var cache = new LookCache();
        using var plain = ImageExporter.Render(img, Look(320, 240));
        using var viaCache = ImageExporter.Render(img, Look(320, 240), null, cache);
        Assert.Equal(Rgba(plain), Rgba(viaCache));
    }

    [Theory]
    [InlineData(false, 0, 0, 40, 30, null)]              // opaque: whole image
    [InlineData(true, 10, 5, 20, 20, "10,5,20,20")]      // a block
    [InlineData(true, 0, 0, 40, 30, null)]               // visible up to all four edges
    [InlineData(true, 39, 29, 1, 1, "24,14,16,16")]      // one pixel in the corner: at least 16 × 16, within the image
    [InlineData(true, 15, 15, 1, 1, "7,7,16,16")]        // one pixel in the middle
    public void VisibleArea_FindsTheRectangle(bool transparent, int x0, int y0, int bw, int bh, string? expected)
    {
        const int W2 = 40, H2 = 30;
        var px = new float[W2 * H2 * 4];
        for (var y = 0; y < H2; y++)
        for (var x = 0; x < W2; x++)
            px[(y * W2 + x) * 4 + 3] = !transparent || (x >= x0 && x < x0 + bw && y >= y0 && y < y0 + bh) ? 1 : 0;
        var area = VisibleArea.Of(px, W2, H2);
        Assert.Equal(expected, area is { } a ? $"{a.X},{a.Y},{a.Width},{a.Height}" : null);
    }

    [Fact]
    public void VisibleArea_NothingVisible_LeavesTheImageToTheFilters() =>
        Assert.Null(VisibleArea.Of(new float[40 * 30 * 4], 40, 30));

    /// <summary>Always the same two discs, whatever the image shows.</summary>
    private sealed class FixedDiscs : ISegmentModel
    {
        public int Size => 128;
        public float[] Run(float[] rgb, CancellationToken ct)
        {
            var output = new float[Size * Size];
            for (var y = 0; y < Size; y++)
            for (var x = 0; x < Size; x++)
            {
                double px = (x + 0.5) * W / Size, py = (y + 0.5) * H / Size;
                var inside = Math.Sqrt((px - 200) * (px - 200) + (py - 200) * (py - 200)) < 80 || Math.Sqrt((px - 460) * (px - 460) + (py - 200) * (py - 200)) < 50;
                output[y * Size + x] = inside ? 1 : 0;
            }
            return output;
        }
    }

    [Fact]
    public void OutsideTheRegion_IsBackground()
    {
        using var img = Scene();
        var model = new ColourModel();
        var mask = BackgroundRemover.Detect(img, new SourceRect(100, 100, 220, 200), model, CancellationToken.None);
        var alpha = BackgroundRemover.Edit(mask, [], null);
        using var result = BackgroundRemover.Compose(img, mask, alpha, BackgroundMode.Transparent, 0, CancellationToken.None);
        var px = Rgba(result);

        Assert.Equal((uint)W, result.Width);
        Assert.True(AlphaAt(px, 200, 200) > 0.99f);
        Assert.True(AlphaAt(px, 460, 200) < 0.01f, "blaue Scheibe liegt außerhalb");
    }
    [Fact]
    public void GreyscaleImage_GetsTransparentBackground_WithItsGreyValues()
    {
        // Black-and-white photos and scans stay greyscale when loaded; Compose needs colour channels (No. 87)
        using var img = Scene();
        img.ColorSpace = ColorSpace.Gray;
        img.ColorType = ColorType.Grayscale;
        var grey = Rgba(AddAlpha((MagickImage)img.Clone()));
        // In grey both discs are darker than the background (luma about 0.2–0.3 against 0.5)
        using var result = Remove(img, new FixedModelOn(rgb => rgb < 0.38f), []);
        var px = Rgba(result);

        Assert.True(result.HasAlpha);
        Assert.True(AlphaAt(px, 20, 20) < 0.01f, $"Hintergrund: {AlphaAt(px, 20, 20)}");
        var centre = (200 * W + 200) * 4;
        Assert.True(AlphaAt(px, 200, 200) > 0.99f);
        Assert.InRange(px[centre], grey[centre] - 300, grey[centre] + 300);   // grey value kept, R = G = B
        Assert.Equal(px[centre], px[centre + 1]);
        Assert.Equal(px[centre], px[centre + 2]);
    }

    /// <summary>Takes as subject what a test on the red plane says (hard threshold at the model size).</summary>
    private sealed class FixedModelOn(Func<float, bool> subject) : ISegmentModel
    {
        public int Size => 128;
        public float[] Run(float[] rgb, CancellationToken ct) =>
            Enumerable.Range(0, Size * Size).Select(i => subject(rgb[i]) ? 1f : 0f).ToArray();
    }

    /// <summary>A model that returns what it is given, regardless of the image.</summary>
    private sealed class FixedModel(Func<int, float[]> output) : ISegmentModel
    {
        public int Size => 128;
        public float[] Run(float[] rgb, CancellationToken ct) => output(Size * Size);
    }

    [Theory]
    [InlineData("kurz")]
    [InlineData("leer")]
    [InlineData("nan")]
    public void UnusableModelOutput_IsReportedClearly(string kind)
    {
        // A mask of the wrong size failed deep in the refinement with dozens of index errors; NaN made everything
        // silently transparent (No. 87)
        using var img = Scene();
        var model = new FixedModel(n => kind switch
        {
            "kurz" => new float[n / 2],
            "leer" => [],
            _ => Enumerable.Range(0, n).Select(i => i % 7 == 0 ? float.NaN : 1f).ToArray(),
        });
        var e = Assert.Throws<InvalidDataException>(() => BackgroundRemover.Detect(img, Whole, model, CancellationToken.None));
        Assert.DoesNotContain("Index", e.Message);
    }
    [Fact]
    public void BackgroundModels_RunWithoutTheMemoryArena_OnTheCpu()
    {
        // With the arena the runtime kept 7–12 GB after a background run as long as the session was open (No. 88)
        using var background = OnnxSessions.CpuOptions(AiModelCatalog.BiRefNetLite);
        using var other = OnnxSessions.CpuOptions(AiModelCatalog.Allround);
        Assert.False(background.EnableCpuMemArena);
        Assert.True(other.EnableCpuMemArena);
    }
}
