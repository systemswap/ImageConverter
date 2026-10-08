using ImageConverter.Core.Imaging;
using ImageConverter.ViewModels;
using ImageMagick;

namespace ImageConverter.Tests;

/// <summary>Kachel „Tools“: Licht und Farbe (Core: <see cref="Adjust"/>).</summary>
public class AdjustTests
{
    /// <summary>Waagerechter Verlauf schwarz → weiß, eine Zeile reicht für Tonkurven.</summary>
    private static byte[] Ramp(int width = 256)
    {
        var bgra = new byte[width * 4];
        for (var x = 0; x < width; x++)
        {
            var v = (byte)(x * 255 / (width - 1));
            bgra[x * 4] = bgra[x * 4 + 1] = bgra[x * 4 + 2] = v;
            bgra[x * 4 + 3] = 255;
        }
        return bgra;
    }

    private static byte Gray(byte[] bgra, int x) => bgra[x * 4 + 1];

    [Fact]
    public void Nothing_Set_LeavesPixelsUnchanged()
    {
        var src = Ramp();
        Assert.Equal(src, Adjust.Apply(src, 256, 1, Adjustments.None));
    }

    [Theory]
    [InlineData(60, 0)]
    [InlineData(-60, 0)]
    [InlineData(0, 70)]
    [InlineData(0, -70)]
    public void BrightnessAndContrast_KeepBlackAndWhite(double brightness, double contrast)
    {
        var result = Adjust.Apply(Ramp(), 256, 1, new Adjustments(Brightness: brightness, Contrast: contrast));

        Assert.Equal(0, Gray(result, 0));
        Assert.Equal(255, Gray(result, 255));
    }

    [Fact]
    public void Brightness_LiftsMidtones()
    {
        var result = Adjust.Apply(Ramp(), 256, 1, new Adjustments(Brightness: 50));
        Assert.True(Gray(result, 128) > 150, $"Mitte {Gray(result, 128)}");
    }

    [Fact]
    public void Contrast_SpreadsAroundTheMiddle()
    {
        var result = Adjust.Apply(Ramp(), 256, 1, new Adjustments(Contrast: 60));
        Assert.True(Gray(result, 64) < 64 && Gray(result, 192) > 192);
    }

    /// <summary>
    /// Tiefen hellt nur auf, wo die Umgebung dunkel ist: Linke Hälfte dunkel, rechte hell —
    /// gleiches dunkles Grau links wird deutlich heller, rechts im Hellen bleibt Weiß weiß.
    /// </summary>
    [Fact]
    public void Shadows_LiftDarkAreas_LeaveBrightAreas()
    {
        const int w = 400, h = 100;
        var bgra = new byte[w * h * 4];
        for (var i = 0; i < w * h; i++)
        {
            var v = (byte)(i % w < w / 2 ? 40 : 230);
            bgra[i * 4] = bgra[i * 4 + 1] = bgra[i * 4 + 2] = v;
            bgra[i * 4 + 3] = 255;
        }

        var result = Adjust.Apply(bgra, w, h, new Adjustments(Shadows: 80));
        var dark = result[(50 * w + 50) * 4 + 1];
        var bright = result[(50 * w + 350) * 4 + 1];

        Assert.True(dark > 80, $"dunkler Bereich nur auf {dark}");
        Assert.InRange(bright, 228, 233);
    }

    /// <summary>
    /// Kein Lichthof: Helle Pixel direkt an einer harten Kante zu einer dunklen Fläche
    /// (Himmel an Bergen) bleiben, wie sie sind. Mit einfachem Gauß als Umgebung wurden
    /// sie mit aufgehellt.
    /// </summary>
    [Fact]
    public void Shadows_NoHaloAtHardEdges()
    {
        const int w = 400, h = 100;
        var bgra = new byte[w * h * 4];
        for (var i = 0; i < w * h; i++)
        {
            var v = (byte)(i % w < w / 2 ? 40 : 200);
            bgra[i * 4] = bgra[i * 4 + 1] = bgra[i * 4 + 2] = v;
            bgra[i * 4 + 3] = 255;
        }

        var result = Adjust.Apply(bgra, w, h, new Adjustments(Shadows: 80));

        for (var x = w / 2; x < w / 2 + 6; x++)
            Assert.InRange(result[(50 * w + x) * 4 + 1], 198, 206);
    }

    [Fact]
    public void Highlights_Negative_DarkenBrightAreas_LeaveDarkAreas()
    {
        const int w = 400, h = 100;
        var bgra = new byte[w * h * 4];
        for (var i = 0; i < w * h; i++)
        {
            var v = (byte)(i % w < w / 2 ? 40 : 240);
            bgra[i * 4] = bgra[i * 4 + 1] = bgra[i * 4 + 2] = v;
            bgra[i * 4 + 3] = 255;
        }

        var result = Adjust.Apply(bgra, w, h, new Adjustments(Highlights: -80));

        Assert.True(result[(50 * w + 350) * 4 + 1] < 215);
        Assert.InRange(result[(50 * w + 50) * 4 + 1], 38, 42);
    }

    [Fact]
    public void Shadows_KeepTheHue()
    {
        // Dunkles Rot bleibt rot, wird nur heller
        var bgra = new byte[64 * 64 * 4];
        for (var i = 0; i < 64 * 64; i++) { bgra[i * 4 + 2] = 60; bgra[i * 4 + 1] = 20; bgra[i * 4] = 10; bgra[i * 4 + 3] = 255; }

        var result = Adjust.Apply(bgra, 64, 64, new Adjustments(Shadows: 70));
        int r = result[2], g = result[1], b = result[0];

        Assert.True(r > 60);
        Assert.InRange((double)g / r, 20.0 / 60 - 0.05, 20.0 / 60 + 0.05);
        Assert.InRange((double)b / r, 10.0 / 60 - 0.05, 10.0 / 60 + 0.05);
    }

    [Theory]
    [InlineData(-100, Tint.None)]
    [InlineData(0, Tint.BlackWhite)]
    public void FullDesaturation_And_BlackWhite_GiveGray(double saturation, Tint tint)
    {
        var bgra = new byte[] { 30, 140, 220, 255 };   // kräftiges Orange (B, G, R, A)
        var result = Adjust.Apply(bgra, 1, 1, new Adjustments(Saturation: saturation, Tint: tint));

        Assert.InRange(result[2] - result[0], -1, 1);
        Assert.InRange(result[1] - result[0], -1, 1);
        Assert.Equal(255, result[3]);   // Alpha bleibt
    }

    [Fact]
    public void Sepia_IsWarm_AndKeepsBlackAndWhite()
    {
        var result = Adjust.Apply(Ramp(), 256, 1, new Adjustments(Tint: Tint.Sepia));

        Assert.True(result[128 * 4 + 2] > result[128 * 4], "Mitte soll warm sein (R > B)");
        Assert.Equal(0, Gray(result, 0));
        Assert.Equal(255, Gray(result, 255));
    }

    [Fact]
    public void Export_AppliesAdjustments_LikeThePreview()
    {
        using var src = new MagickImage(new MagickColor("#606060"), 300, 200);
        var options = new ExportOptions(OutputFormat.Png, 90, false, 300, 200, ResampleFilter.Lanczos3, false,
            0, 0, 1, 96, MetadataMode.KeepAll, ColorMode.KeepProfile, new Adjustments(Brightness: 50));

        using var rendered = ImageExporter.Render(src, options);
        var expected = Adjust.Apply(ImageOps.ToBgra(src), 300, 200, options.Adjustments!);

        Assert.Equal(expected[(100 * 300 + 150) * 4 + 1], ImageOps.ToBgra(rendered)[(100 * 300 + 150) * 4 + 1]);
    }

    [Fact]
    public async Task ToolSlider_IsUndoable_AndReachesTheExport()
    {
        var vm = new MainViewModel { DeferHistoryTurns = false };
        vm.Shadows = 40;
        vm.SelectedTint = "Sepia";
        vm.CommitPendingSettings();

        var options = MainViewModel.OptionsFor(vm.CaptureSettings());
        Assert.Equal(new Adjustments(Shadows: 40, Tint: Tint.Sepia), options.Adjustments);

        await vm.UndoCommand.ExecuteAsync(null);
        Assert.Equal("Aus", vm.SelectedTint);
        await vm.UndoCommand.ExecuteAsync(null);
        Assert.Equal(0, vm.Shadows);
    }
}
