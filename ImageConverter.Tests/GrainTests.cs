using ImageConverter.Core.Imaging;
using ImageConverter.ViewModels;

namespace ImageConverter.Tests;

/// <summary>
/// Gruppe „Körnung“: Korn wie bei Film (Mitteltöne am meisten), fest je Pixel, streifenweise gleich wie am Stück,
/// Vorschau wie der verkleinerte Export, Messen findet das Korn wieder, im Export zuletzt, in der Liste ausschaltbar.
/// </summary>
public class GrainTests
{
    private static float[] Flat(int w, int h, float v)
    {
        var px = new float[w * h * 4];
        for (var i = 0; i < w * h; i++) (px[i * 4], px[i * 4 + 1], px[i * 4 + 2], px[i * 4 + 3]) = (v, v, v, 1);
        return px;
    }

    /// <summary>Streuung eines Kanals (0 = B, 1 = G, 2 = R) über das ganze Bild.</summary>
    private static double Std(float[] px, int channel = 1)
    {
        var n = px.Length / 4;
        double sum = 0, sum2 = 0;
        for (var i = 0; i < n; i++) { double v = px[i * 4 + channel]; sum += v; sum2 += v * v; }
        var mean = sum / n;
        return Math.Sqrt(Math.Max(0, sum2 / n - mean * mean));
    }

    private static double Correlation(float[] a, float[] b, int ca, int cb, float baseline)
    {
        var n = a.Length / 4;
        double sa = 0, sb = 0, saa = 0, sbb = 0, sab = 0;
        for (var i = 0; i < n; i++)
        {
            double x = a[i * 4 + ca] - baseline, y = b[i * 4 + cb] - baseline;
            sa += x; sb += y; saa += x * x; sbb += y * y; sab += x * y;
        }
        var cov = sab / n - sa / n * (sb / n);
        return cov / Math.Sqrt((saa / n - sa * sa / n / n) * (sbb / n - sb * sb / n / n));
    }

    [Fact]
    public void WithoutAmount_ThePixelsStay()
    {
        var px = Flat(64, 48, 0.5f);
        Assert.Same(px, Grain.Apply(px, 64, 48, new GrainSettings(0, Size: 80, Roughness: 90, Color: 100)));
        Assert.Same(px, Grain.Apply(px, 64, 48, null));
    }

    [Fact]
    public void Strength_IsLikeFilm_MostInTheMidTones()
    {
        var g = new GrainSettings(60, Size: 40, Roughness: 0);
        var mid = Std(Grain.Apply(Flat(256, 256, 0.5f), 256, 256, g));
        var dark = Std(Grain.Apply(Flat(256, 256, 0.1f), 256, 256, g));
        var black = Std(Grain.Apply(Flat(256, 256, 0f), 256, 256, g));

        Assert.InRange(mid / g.MidStd, 0.9, 1.1);            // Stärke = Streuung im Mittelton
        Assert.InRange(dark / mid, 0.45, 0.75);              // √(4 · 0,1 · 0,9) = 0,6
        Assert.True(black < 1e-6, $"Schwarz {black}");      // reines Schwarz bekommt nichts
    }

    [Fact]
    public void Grain_IsTheSameEveryTime()
    {
        var g = new GrainSettings(50, Size: 55, Roughness: 60, Color: 40);
        var a = Grain.Apply(Flat(300, 200, 0.4f), 300, 200, g);
        var b = Grain.Apply(Flat(300, 200, 0.4f), 300, 200, g);
        Assert.Equal(a, b);
    }

    [Theory]
    [InlineData(1.0)]   // echter Gauß
    [InlineData(6.0)]   // Boxfilter
    public void Strips_GiveTheSameFieldAsOnePiece(double sigma)
    {
        const int w = 180;
        var whole = Grain.Field(0, w, 600, sigma, seed: 5);
        var top = Grain.Field(0, w, 256, sigma, seed: 5);
        var rest = Grain.Field(256, w, 344, sigma, seed: 5);
        for (var i = 0; i < top.Length; i++) Assert.True(Math.Abs(whole[i] - top[i]) < 1e-4, $"{i}: {whole[i]} ≠ {top[i]}");
        for (var i = 0; i < rest.Length; i++) Assert.True(Math.Abs(whole[top.Length + i] - rest[i]) < 1e-4, $"{i}: {whole[top.Length + i]} ≠ {rest[i]}");

        // auf Streuung 1 gebracht
        double s2 = whole.Sum(v => (double)v * v) / whole.Length;
        Assert.InRange(Math.Sqrt(s2), 0.9, 1.1);
    }

    [Fact]
    public void ColourShare_ZeroIsGrey_FullIsEachChannelOnItsOwn()
    {
        const int w = 200, h = 200;
        var grey = Grain.Apply(Flat(w, h, 0.5f), w, h, new GrainSettings(60, Roughness: 0, Color: 0));
        for (var i = 0; i < w * h; i++)
        {
            Assert.Equal(grey[i * 4], grey[i * 4 + 1], 5);
            Assert.Equal(grey[i * 4 + 2], grey[i * 4 + 1], 5);
        }

        var colour = Grain.Apply(Flat(w, h, 0.5f), w, h, new GrainSettings(60, Roughness: 0, Color: 100));
        Assert.InRange(Correlation(colour, colour, 0, 2, 0.5f), -0.1, 0.1);   // B und R unabhängig
        var half = Grain.Apply(Flat(w, h, 0.5f), w, h, new GrainSettings(60, Roughness: 0, Color: 50));
        Assert.InRange(Correlation(half, half, 0, 2, 0.5f), 0.4, 0.6);        // Korrelation = 1 − Farbanteil
    }

    [Theory]
    [InlineData(40)]   // 1 px
    [InlineData(70)]   // 2,8 px
    public void Preview_LooksLikeTheDownscaledExport(double size)
    {
        // Export in voller Größe mit Korn, dann auf ein Viertel verkleinert — gegen die Vorschau, die gleich klein rechnet
        const int full = 1024, small = 256;
        var g = new GrainSettings(60, size, Roughness: 0);
        var exported = Grain.Apply(Flat(full, full, 0.5f), full, full, g);
        using var big = Pixels.ToMagick16(exported, full, full, alpha: false);
        using var shrunk = ImageOps.Resize(big, small, small, ResampleFilter.Lanczos3, gammaCorrect: false);
        var expected = Std(Pixels.FromMagick(shrunk));

        var preview = Std(Grain.Apply(Flat(small, small, 0.5f), small, small, g, scale: (double)small / full));
        Console.WriteLine($"Körnung {size}: Vorschau / Export {preview / expected:0.000}");
        Assert.InRange(preview / expected, 0.85, 1.18);
    }

    /// <summary>Weicher Verlauf (ruhig genug, dass nur das Korn auffällt) mit bekanntem Korn.</summary>
    private static float[] GradientWithGrain(GrainSettings g, int w, int h)
    {
        var px = new float[w * h * 4];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var i = (y * w + x) * 4;
            var v = 0.12f + 0.76f * x / w + 0.04f * y / h;
            (px[i], px[i + 1], px[i + 2], px[i + 3]) = (v * 0.95f, v, v * 1.02f, 1);
        }
        return Grain.Apply(px, w, h, g);
    }

    [Fact]
    public void Measure_FindsTheGrainItWasGiven()
    {
        const int w = 1200, h = 900;
        var given = new GrainSettings(40, Size: 40, Roughness: 0, Color: 50);
        var m = Grain.Measure(GradientWithGrain(given, w, h), w, h, outputScale: 1);

        Assert.NotNull(m);
        Assert.InRange(m.Amount, given.Amount - 6, given.Amount + 6);
        Assert.InRange(m.Size, given.Size - 10, given.Size + 10);
        Assert.InRange(m.Color, given.Color - 15, given.Color + 15);
        Assert.Equal(0, m.Roughness);

        // Ausgabe halb so groß: Korn feiner verwischt, also schwächer
        var half = Grain.Measure(GradientWithGrain(given, w, h), w, h, outputScale: 0.5);
        Assert.True(half!.Amount < m.Amount, $"{half.Amount} ≥ {m.Amount}");
    }

    [Fact]
    public void Measure_SmoothPhoto_HasNoGrain()
    {
        const int w = 600, h = 400;
        var px = GradientWithGrain(GrainSettings.None, w, h);
        var m = Grain.Measure(px, w, h, 1);
        Assert.True(m is null || m.Amount < 3, $"{m?.Amount}");
    }

    [Fact]
    public void Export_PutsTheGrainLast_AfterSharpening()
    {
        const int w = 160, h = 120;
        var src = GradientWithGrain(GrainSettings.None, w, h);
        using var image = Pixels.ToMagick16(src, w, h, alpha: false);
        var g = new GrainSettings(50, Size: 30, Roughness: 40, Color: 20);

        using var rendered = ImageExporter.RenderLook(image, 0, 0, null, 1.2, 1.0, grain: g);
        var expected = Grain.Apply(Sharpen.UnsharpMask(Pixels.FromMagick(image), w, h, 1.2, 1.0), w, h, g);
        var actual = Pixels.FromMagick(rendered);
        for (var i = 0; i < actual.Length; i++) Assert.True(Math.Abs(expected[i] - actual[i]) < 1e-4, $"{i}: {expected[i]} ≠ {actual[i]}");   // 16 Bit
    }

    [Fact]
    public void EditList_GrainCanBeSwitchedOff()
    {
        var vm = new MainViewModel { DeferHistoryTurns = false };
        vm.GrainAmount = 35;
        vm.GrainSize = 60;
        vm.GrainColor = 20;
        vm.CommitPendingSettings();

        var shown = MainViewModel.OptionsFor(vm.CaptureSettings());
        Assert.Equal(new GrainSettings(35, 60, 30, 20), shown.Grain);

        vm.HiddenSteps = EditSteps.Grain;
        var s = vm.CaptureSettings();
        Assert.Null(MainViewModel.OptionsFor(s).Grain);
        Assert.Equal(35, vm.GrainAmount);                              // Regler behält seinen Wert
        Assert.Equal(0, MainViewModel.WithoutHidden(s, s.Hidden).GrainAmount);   // „Neu anwenden“ setzt ihn auf null
    }
}
