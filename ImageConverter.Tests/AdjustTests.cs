using ImageConverter.Core.Imaging;
using ImageConverter.ViewModels;
using ImageMagick;

namespace ImageConverter.Tests;

/// <summary>Tests for the light and colour tools of the „Tools“ tile (core: <see cref="Adjust"/>).</summary>
public class AdjustTests
{
    /// <summary>Horizontal black-to-white ramp; a single row is enough to test tone curves.</summary>
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
    /// Shadows brightens only where the surroundings are dark: with a dark left half and a bright right half,
    /// the dark grey on the left becomes clearly brighter while the bright right half stays as it is.
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

        Assert.True(dark > 65, $"dunkler Bereich nur auf {dark}");   // 40 → ~70: monotonic curve, see Raise
        Assert.InRange(bright, 228, 233);
    }

    /// <summary>
    /// No halo: bright pixels directly at a hard edge next to a dark area (sky against mountains) stay
    /// as they are. A plain Gaussian blur as the surround would brighten them as well.
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
        // Dark red stays red and only becomes brighter.
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
        var bgra = new byte[] { 30, 140, 220, 255 };   // saturated orange (B, G, R, A)
        var result = Adjust.Apply(bgra, 1, 1, new Adjustments(Saturation: saturation, Tint: tint));

        Assert.InRange(result[2] - result[0], -1, 1);
        Assert.InRange(result[1] - result[0], -1, 1);
        Assert.Equal(255, result[3]);   // alpha is kept
    }

    [Fact]
    public void TintColor_DefaultIsWarmSepia_AndKeepsBlackAndWhite()
    {
        var result = Adjust.Apply(Ramp(), 256, 1, new Adjustments(Tint: Tint.Color));

        Assert.True(result[128 * 4 + 2] > result[128 * 4], "Mitte soll warm sein (R > B)");
        Assert.Equal(0, Gray(result, 0));
        Assert.Equal(255, Gray(result, 255));
    }

    [Fact]
    public void TintHue_ChoosesTheColour()
    {
        var blue = Adjust.Apply(Ramp(), 256, 1, new Adjustments(Tint: Tint.Color, TintHue: 210));
        Assert.True(blue[128 * 4] > blue[128 * 4 + 2], "bei 210° soll die Mitte blau sein (B > R)");
    }

    [Fact]
    public void TintHue_DoesNotCount_WithoutTinting()
    {
        Assert.True(new Adjustments(TintHue: 200).IsNone);
        Assert.False(new Adjustments(Tint: Tint.Color, TintHue: 200).IsNone);
    }

    private static double Chroma(byte[] bgra) =>
        bgra[..3].Max() - (double)bgra[..3].Min();

    /// <summary>Vibrance boosts muted colours a lot and strong colours little, unlike saturation.</summary>
    [Fact]
    public void Vibrance_BoostsMutedColours_MoreThanStrongOnes()
    {
        byte[] muted = [150, 135, 120, 255];    // muted blue-grey (B, G, R)
        byte[] strong = [20, 60, 220, 255];     // saturated red

        var a = new Adjustments(Vibrance: 80);
        var mutedGain = Chroma(Adjust.Apply(muted, 1, 1, a)) / Chroma(muted);
        var strongGain = Chroma(Adjust.Apply(strong, 1, 1, a)) / Chroma(strong);

        Assert.True(mutedGain > 1.4, $"blass nur ×{mutedGain:F2}");
        Assert.True(strongGain < 1.15, $"kräftig ×{strongGain:F2}");
    }

    /// <summary>
    /// Saturation and vibrance change only the chroma (Oklab); hue and lightness stay. Boosting in RGB
    /// would shift blue towards violet and make yellow brighter.
    /// </summary>
    [Theory]
    [InlineData(200, 90, 40)]     // blue (B, G, R)
    [InlineData(40, 200, 220)]    // yellow
    [InlineData(150, 135, 120)]   // muted blue-grey
    public void ColourBoost_KeepsHueAndLightness(byte b, byte g, byte r)
    {
        byte[] px = [b, g, r, 255];
        foreach (var a in new[] { new Adjustments(Saturation: 50), new Adjustments(Vibrance: 80) })
        {
            var o = Adjust.Apply(px, 1, 1, a);
            var before = Oklab.FromSrgb(r / 255f, g / 255f, b / 255f);
            var after = Oklab.FromSrgb(o[2] / 255f, o[1] / 255f, o[0] / 255f);

            Assert.InRange(Oklab.Hue(after.A, after.B) - Oklab.Hue(before.A, before.B), -2.5, 2.5);
            Assert.InRange(after.L - before.L, -0.015, 0.015);
            Assert.True(Math.Sqrt(after.A * after.A + after.B * after.B) >= Math.Sqrt(before.A * before.A + before.B * before.B));
        }
    }

    /// <summary>Match colour, luminance: multiplies the lightness; midtones become clearly brighter without clipping white.</summary>
    [Fact]
    public void MatchLuminance_ScalesBrightness_WithoutClippingWhite()
    {
        var result = Adjust.Apply(Ramp(), 256, 1, new Adjustments(MatchLuminance: 50));

        var mid = Oklab.FromSrgb(Gray(result, 100) / 255f, Gray(result, 100) / 255f, Gray(result, 100) / 255f).L;
        var midBefore = Oklab.FromSrgb(100 / 255f, 100 / 255f, 100 / 255f).L;
        Assert.InRange(mid / midBefore, 1.4, 1.55);              // ×1.5 (luminance 150)
        Assert.True(Gray(result, 200) < Gray(result, 255));       // bright tones stay distinguishable
        Assert.Equal(0, Gray(result, 0));                        // black stays black
    }

    /// <summary>Builds a float buffer (no dithering) whose left and right halves each have one colour.</summary>
    internal static float[] Halves(int w, int h, (float R, float G, float B) left, (float R, float G, float B) right)
    {
        var px = new float[w * h * 4];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var c = x < w / 2 ? left : right;
            var i = (y * w + x) * 4;
            (px[i], px[i + 1], px[i + 2], px[i + 3]) = (c.B, c.G, c.R, 1);
        }
        return px;
    }

    internal static float At(float[] px, int w, int x, int y, int channel = 1) => px[(y * w + x) * 4 + channel];

    /// <summary>
    /// Regional luminance with a threshold: a dark region becomes as bright as without the limit, the bright region
    /// next to it stays unchanged, also directly at the edge (no halo, unlike a blurred mask).
    /// </summary>
    [Fact]
    public void MatchLimit_LiftsDarkRegionsFully_AndLeavesBrightOnesIncludingTheEdge()
    {
        const int w = 400, h = 200;
        var src = Halves(w, h, (0.18f, 0.2f, 0.15f), (0.8f, 0.85f, 0.9f));   // dark forest | bright sky
        var free = Adjust.Apply(src, w, h, new Adjustments(MatchLuminance: 100));
        var limited = Adjust.Apply(src, w, h, new Adjustments(MatchLuminance: 100, MatchLimit: 45));

        Assert.True(At(limited, w, 50, 100) > At(src, w, 50, 100) + 0.1f, "dunkler Bereich nicht aufgehellt");
        Assert.Equal(At(free, w, 50, 100), At(limited, w, 50, 100), 3);              // as bright as without the limit
        Assert.True(At(free, w, 300, 100) > At(src, w, 300, 100) + 0.02f);           // without the limit the sky would become brighter
        for (var x = w / 2; x < w; x += 1)                                           // sky unchanged right up to the edge
            Assert.InRange(At(limited, w, x, 100), At(src, w, x, 100) - 0.004f, At(src, w, x, 100) + 0.004f);
    }

    /// <summary>
    /// Laplacian pyramid blending: far from the mask edge the result is exactly the respective version, at the edge
    /// there is a soft, wide transition without a step, and edges in the image stay sharp.
    /// </summary>
    [Fact]
    public void PyramidBlend_GivesSoftTransition_ButKeepsImageEdgesSharp()
    {
        const int w = 512, h = 64;
        var a = new float[w * h];
        var b = new float[w * h];
        var weight = new float[w * h];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var i = y * w + x;
            var edge = x % 64 < 32 ? 0f : 0.2f;            // fine stripe pattern (edges) in both versions
            a[i] = 0.3f + edge;
            b[i] = 0.6f + edge;
            weight[i] = x < w / 2 ? 0 : 1;                  // hard mask edge in the middle
        }
        var r = PyramidBlend.Blend(a, b, weight, w, h);
        float At(int x) => r[32 * w + x];

        Assert.Equal(a[32 * w + 10], At(10), 2);           // far left: version a
        Assert.Equal(b[32 * w + 500], At(500), 2);         // far right: version b
        // The stripe edge at x = 96 (left) remains a step of 0.2, so edges are not washed out.
        Assert.InRange(At(96) - At(95), 0.15f, 0.25f);
        // At the mask edge there is no step of the full difference (0.3); the transition is spread out.
        var maxStep = Enumerable.Range(200, 112).Where(x => x % 32 != 0).Max(x => Math.Abs(At(x) - At(x - 1)));
        Assert.True(maxStep < 0.12f, $"Sprung {maxStep:0.000} am Maskenrand");
    }

    /// <summary>
    /// Sky gaps: at the edge of a dark tree, small patches of sky show through that are darker than the threshold of
    /// the fixed protection for bright pixels. They stay unchanged anyway because they look like the sky behind.
    /// Sunlit spots of the same size and lightness in yellow-green are brightened (different colour from the sky).
    /// </summary>
    [Fact]
    public void MatchLimit_KeepsSkyGapsAtTreeEdge_ButLiftsSunlitLeaves()
    {
        const int w = 400, h = 300;
        var sky = (0.42f, 0.5f, 0.62f);
        var src = Halves(w, h, (0.07f, 0.13f, 0.05f), sky);              // dark tree | sky
        void Spot(int x, int y, (float R, float G, float B) c)
        {
            for (var dy = 0; dy < 2; dy++)
            for (var dx = 0; dx < 2; dx++)
            {
                var i = ((y + dy) * w + x + dx) * 4;
                (src[i], src[i + 1], src[i + 2]) = (c.B, c.G, c.R);
            }
        }
        for (var y = 20; y < h - 20; y += 12)
        {
            Spot(w / 2 - 6, y, sky);                                       // sky shows through
            Spot(w / 2 - 6, y + 6, (0.45f, 0.48f, 0.12f));                 // sunlit spot on needles
        }
        Assert.True(Oklab.FromSrgb(sky.Item1, sky.Item2, sky.Item3).L < 0.62f, "Testhimmel zu hell — der feste Schutz würde greifen");

        var r = Adjust.Apply(src, w, h, new Adjustments(MatchLuminance: 100, MatchLimit: 45));
        int gap = w / 2 - 6, row = 152;
        Assert.InRange(At(r, w, gap, row), At(src, w, gap, row) - 0.02f, At(src, w, gap, row) + 0.02f);     // gap stays
        Assert.True(At(r, w, gap, row + 6) > At(src, w, gap, row + 6) + 0.05f, "Sonnenfleck nicht aufgehellt");
        Assert.True(At(r, w, 60, 150) > At(src, w, 60, 150) + 0.05f, "Baum nicht aufgehellt");
    }

    /// <summary>
    /// Target value: a dark region is lifted to about the target, a midtone region above it stays unchanged. Without
    /// a target it would be lifted by the same ratio and blow out.
    /// </summary>
    [Fact]
    public void MatchTarget_LiftsDarkAreasToTarget_AndLeavesMidtonesAlone()
    {
        const int w = 400, h = 200;
        var src = Halves(w, h, (0.08f, 0.12f, 0.06f), (0.42f, 0.5f, 0.28f));   // dark tree | sunny meadow
        var free = Adjust.Apply(src, w, h, new Adjustments(MatchLuminance: 100));
        var target = Adjust.Apply(src, w, h, new Adjustments(MatchLuminance: 100, MatchTarget: 40));
        float L(float[] px, int x) => Oklab.FromSrgb(px[(100 * w + x) * 4 + 2], px[(100 * w + x) * 4 + 1], px[(100 * w + x) * 4]).L;

        Assert.True(L(free, 300) > L(src, 300) + 0.15f, "ohne Zielwert würde die Wiese stark aufhellen");
        Assert.InRange(L(target, 300), L(src, 300) - 0.01f, L(src, 300) + 0.01f);   // meadow stays
        Assert.InRange(L(target, 60), 0.36f, 0.44f);                              // tree lifted to about the target (40 %)
        Assert.True(L(target, 60) > L(src, 60) + 0.1f);
    }

    /// <summary>
    /// Target value only (without threshold, as used in practice): the tree becomes brighter towards the target, the
    /// sky and the sky gaps at the tree edge stay (the region there is already as bright as the target), and nothing
    /// becomes darker.
    /// </summary>
    [Fact]
    public void MatchTarget_Alone_LiftsTree_AndLeavesSkyAndGaps()
    {
        const int w = 400, h = 300;
        var sky = (0.42f, 0.5f, 0.62f);
        var src = Halves(w, h, (0.07f, 0.13f, 0.05f), sky);
        void Spot(int x, int y, (float R, float G, float B) c)
        {
            for (var dy = 0; dy < 2; dy++)
            for (var dx = 0; dx < 2; dx++)
            {
                var i = ((y + dy) * w + x + dx) * 4;
                (src[i], src[i + 1], src[i + 2]) = (c.B, c.G, c.R);
            }
        }
        for (var y = 20; y < h - 30; y += 24)
        {
            Spot(w / 2 - 6, y, sky);
            Spot(w / 2 - 6, y + 12, (0.45f, 0.48f, 0.12f));
        }
        var r = Adjust.Apply(src, w, h, new Adjustments(MatchLuminance: 100, MatchTarget: 40));
        int gap = w / 2 - 6, row = 140, sun = 152;
        Assert.InRange(At(r, w, gap, row), At(src, w, gap, row) - 0.02f, At(src, w, gap, row) + 0.02f);     // gap stays
        Assert.True(At(r, w, gap, sun) >= At(src, w, gap, sun) - 0.005f, "Sonnenfleck dunkler geworden");
        Assert.True(At(r, w, 60, 150) > At(src, w, 60, 150) + 0.05f, "Baum nicht aufgehellt");
        Assert.InRange(At(r, w, 300, 150), At(src, w, 300, 150) - 0.01f, At(src, w, 300, 150) + 0.01f);     // sky stays
    }

    /// <summary>Bright gaps inside a dark region (sky between branches) do not light up.</summary>
    [Fact]
    public void MatchLimit_ProtectsBrightGapsInsideDarkRegions()
    {
        const int w = 300, h = 300;
        var src = Halves(w, h, (0.15f, 0.18f, 0.12f), (0.15f, 0.18f, 0.12f));
        for (var y = 20; y < h; y += 30)
        for (var x = 20; x < w; x += 30)
        for (var dy = 0; dy < 2; dy++)
        for (var dx = 0; dx < 2; dx++)
        {
            var i = ((y + dy) * w + x + dx) * 4;
            (src[i], src[i + 1], src[i + 2]) = (0.95f, 0.9f, 0.85f);
        }
        var limited = Adjust.Apply(src, w, h, new Adjustments(MatchLuminance: 100, MatchLimit: 45));
        Assert.True(At(limited, w, 35, 35) > At(src, w, 35, 35) + 0.1f);                                 // forest brighter
        Assert.InRange(At(limited, w, 140, 140), At(src, w, 140, 140) - 0.01f, At(src, w, 140, 140) + 0.01f);   // gap stays
    }


    /// <summary>
    /// Luminance up to 300 %: with a target value a deep shadow reaches the target (with 200 % it does not), midtones
    /// above it stay unchanged; values above 300 % are clamped (overall brightening is done by „Gesamt“).
    /// </summary>
    [Fact]
    public void MatchLuminance_UpTo300_LetsDeepShadowsReachTheTarget()
    {
        const int w = 400, h = 200;
        var src = Halves(w, h, (0.03f, 0.05f, 0.025f), (0.42f, 0.5f, 0.28f));   // deep shadow | meadow
        float L(float[] px, int x) => Oklab.FromSrgb(px[(100 * w + x) * 4 + 2], px[(100 * w + x) * 4 + 1], px[(100 * w + x) * 4]).L;
        var at200 = Adjust.Apply(src, w, h, new Adjustments(MatchLuminance: 100, MatchTarget: 40));
        var at300 = Adjust.Apply(src, w, h, new Adjustments(MatchLuminance: 200, MatchTarget: 40));
        var above = Adjust.Apply(src, w, h, new Adjustments(MatchLuminance: 400, MatchTarget: 60));
        var at300Target60 = Adjust.Apply(src, w, h, new Adjustments(MatchLuminance: 200, MatchTarget: 60));

        Assert.True(L(at200, 60) < 0.34f, $"Mit 200 % schon {L(at200, 60):0.00} — Testschatten nicht dunkel genug");
        Assert.InRange(L(at300, 60), 0.36f, 0.44f);                              // with 300 % up to about the target
        Assert.InRange(L(at300, 300), L(src, 300) - 0.01f, L(src, 300) + 0.01f); // meadow stays
        Assert.Equal(L(at300Target60, 60), L(above, 60), 3);                      // above 300 % is clamped
    }

    /// <summary>
    /// Luminance „Gesamt“ acts on the whole image after the regional luminance: what the target value has evened out
    /// becomes brighter overall, including the meadow that the target value left unchanged.
    /// </summary>
    [Fact]
    public void MatchGlobal_BrightensWholeImage_AfterRegionalLuminance()
    {
        const int w = 400, h = 200;
        var src = Halves(w, h, (0.08f, 0.12f, 0.06f), (0.42f, 0.5f, 0.28f));   // dark tree | meadow
        float L(float[] px, int x) => Oklab.FromSrgb(px[(100 * w + x) * 4 + 2], px[(100 * w + x) * 4 + 1], px[(100 * w + x) * 4]).L;
        var even = Adjust.Apply(src, w, h, new Adjustments(MatchLuminance: 100, MatchTarget: 40));
        var brighter = Adjust.Apply(src, w, h, new Adjustments(MatchLuminance: 100, MatchTarget: 40, MatchGlobal: 25));

        Assert.InRange(L(even, 300), L(src, 300) - 0.01f, L(src, 300) + 0.01f);     // the target value leaves the meadow unchanged
        Assert.InRange(L(brighter, 300) / L(even, 300), 1.2f, 1.3f);                  // „Gesamt“ +25 %: the meadow too
        Assert.InRange(L(brighter, 60) / L(even, 60), 1.2f, 1.3f);                    // and the evened-out tree
        Assert.False(new Adjustments(MatchGlobal: 10).IsNone);
        Assert.Equal(Adjustments.None.PrepareKey, new Adjustments(MatchGlobal: 40).PrepareKey);   // no expensive preparation step
    }

    /// <summary>Luminance mask: dark tones get the most, midtones a share, nothing from „Auslaufen“ upwards; a steeper curve gives midtones less.</summary>
    [Fact]
    public void LumMask_WeightsDarkMost_AndFadesOutAtTheEnd()
    {
        float W(float l, double curve) => LumMask.Weight(l, l, 0.75f, LumMask.Power(curve));
        Assert.Equal(1f, W(0, 30), 3);
        Assert.True(W(0.2f, 30) > W(0.4f, 30) && W(0.4f, 30) > W(0.6f, 30));
        Assert.Equal(0f, W(0.75f, 30));
        Assert.Equal(0f, W(0.9f, 30));
        Assert.True(W(0.4f, 100) < W(0.4f, 0), "steiler Verlauf soll Mitteltönen weniger geben");

        // Applied to an image: dark tones become much brighter, tones above „Auslaufen“ stay (grey 0..255).
        var ramp = Pixels.FromBytes(Ramp());
        var r = Adjust.Apply(ramp, 256, 1, new Adjustments(MaskAmount: 100, MaskEnd: 60));
        float L(float[] px, int x) => Oklab.FromSrgb(px[x * 4 + 2], px[x * 4 + 1], px[x * 4]).L;
        Assert.True(L(r, 40) - L(ramp, 40) > L(r, 120) - L(ramp, 120), "Dunkles soll mehr bekommen als Mitteltöne");
        for (var x = 160; x < 256; x++) Assert.Equal(L(ramp, x), L(r, x), 3);   // unchanged above 60 %
    }

    /// <summary>The luminance mask never reverses tones, even in the worst case (softest curve, +200 %).</summary>
    [Theory]
    [InlineData(0, 200, 100)]
    [InlineData(0, 200, 50)]
    [InlineData(100, 200, 75)]
    [InlineData(30, 150, 30)]
    public void LumMask_NeverReversesTones(double curve, double amount, double end)
    {
        var ramp = Pixels.FromBytes(Ramp());
        var r = Adjust.Apply(ramp, 256, 1, new Adjustments(MaskAmount: amount, MaskEnd: end, MaskCurve: curve));
        for (var x = 1; x < 256; x++)
            Assert.True(r[x * 4 + 1] >= r[(x - 1) * 4 + 1] - 1e-4f, $"Umkehr bei {x}: {r[(x - 1) * 4 + 1]:0.0000} → {r[x * 4 + 1]:0.0000}");
    }

    /// <summary>„Maske zeigen“ shows the mask in grey instead of the image: white where the full strength applies, black where nothing does.</summary>
    [Fact]
    public void LumMask_Preview_ShowsTheMaskInGrey()
    {
        var ramp = Pixels.FromBytes(Ramp());
        var r = Adjust.Apply(ramp, 256, 1, new Adjustments(MaskAmount: 80, MaskEnd: 75, MaskPreview: true));
        Assert.True(r[1] > 0.9f, $"Schwarz → Maske fast weiß: {r[1]:0.000}");   // 30 % region: just below white
        Assert.Equal(0f, r[255 * 4 + 1], 3);                         // white → mask black
        Assert.Equal(r[100 * 4], r[100 * 4 + 2], 4);                 // grey (R = B)
        Assert.False(new Adjustments(MaskPreview: true).IsNone);
        Assert.True(new Adjustments(MaskEnd: 40, MaskCurve: 90).IsNone);   // without strength nothing applies
    }

    /// <summary>Match colour, neutralise: a yellowish cast on a grey wall becomes neutral.</summary>
    [Fact]
    public void Neutralize_RemovesColourCast_ButKeepsStrongColours()
    {
        const int w = 64, h = 64;
        var bgra = new byte[w * h * 4];
        for (var p = 0; p < w * h; p++)
        {
            // Wall with an artificial-light cast, a strongly blue cushion at the bottom left
            var cushion = p % w < 16 && p / w > 48;
            (bgra[p * 4], bgra[p * 4 + 1], bgra[p * 4 + 2]) = cushion ? ((byte)200, (byte)60, (byte)30) : ((byte)150, (byte)180, (byte)195);
            bgra[p * 4 + 3] = 255;
        }

        var result = Adjust.Apply(bgra, w, h, new Adjustments(Neutralize: 100));
        var wall = Oklab.FromSrgb(result[2] / 255f, result[1] / 255f, result[0] / 255f);
        var i = (60 * w + 5) * 4;
        var blue = Oklab.FromSrgb(result[i + 2] / 255f, result[i + 1] / 255f, result[i] / 255f);

        Assert.True(Math.Sqrt(wall.A * wall.A + wall.B * wall.B) < 0.01, "Wand soll grau sein");
        Assert.True(Math.Sqrt(blue.A * blue.A + blue.B * blue.B) > 0.12, "Kissen bleibt kräftig blau");
    }

    /// <summary>Neutralise at 50 % removes half of the colour cast.</summary>
    [Fact]
    public void Neutralize_Strength_RemovesPartOfTheCast()
    {
        var bgra = new byte[32 * 32 * 4];
        for (var p = 0; p < 32 * 32; p++) (bgra[p * 4], bgra[p * 4 + 1], bgra[p * 4 + 2], bgra[p * 4 + 3]) = (150, 180, 195, 255);

        double Cast(byte[] px) { var c = Oklab.FromSrgb(px[2] / 255f, px[1] / 255f, px[0] / 255f); return Math.Sqrt(c.A * c.A + c.B * c.B); }
        var half = Adjust.Apply(bgra, 32, 32, new Adjustments(Neutralize: 50));

        Assert.InRange(Cast(half) / Cast(bgra), 0.4, 0.6);
    }

    /// <summary>Warmth: positive values shift towards yellow, negative values towards blue; black stays neutral.</summary>
    [Fact]
    public void Warmth_ShiftsTowardsYellowOrBlue_ButKeepsBlackNeutral()
    {
        byte[] grey = [128, 128, 128, 255];
        var warm = Adjust.Apply(grey, 1, 1, new Adjustments(Warmth: 80));
        var cool = Adjust.Apply(grey, 1, 1, new Adjustments(Warmth: -80));
        var black = Adjust.Apply([0, 0, 0, 255], 1, 1, new Adjustments(Warmth: 80));

        Assert.True(warm[2] > warm[0], "warm: R > B");
        Assert.True(cool[0] > cool[2], "kühl: B > R");
        Assert.Equal([0, 0, 0, 255], black);
    }

    [Fact]
    public void Vibrance_ProtectsSkinTones()
    {
        byte[] skin = [140, 170, 215, 255];     // light skin tone (B, G, R)
        byte[] sky = [215, 170, 140, 255];      // equally muted, but blue

        var a = new Adjustments(Vibrance: 80);
        var skinGain = Chroma(Adjust.Apply(skin, 1, 1, a)) / Chroma(skin);
        var skyGain = Chroma(Adjust.Apply(sky, 1, 1, a)) / Chroma(sky);

        Assert.True(skinGain < skyGain - 0.2, $"Haut ×{skinGain:F2}, Himmel ×{skyGain:F2}");
    }

    /// <summary>
    /// Shadows leaves midtones alone: faces in indoor photos lie around 0.4–0.6 (measured on an indoor
    /// family photo in which the cheeks and eye area were brightened as well).
    /// </summary>
    [Fact]
    public void Shadows_LeaveMidtonesLikeFaces()
    {
        const int w = 200, h = 200;
        var bgra = new byte[w * h * 4];
        for (var i = 0; i < w * h; i++)
        {
            bgra[i * 4] = bgra[i * 4 + 1] = bgra[i * 4 + 2] = 102;   // ≈ 0.4
            bgra[i * 4 + 3] = 255;
        }

        var result = Adjust.Apply(bgra, w, h, new Adjustments(Shadows: 100));

        Assert.InRange(result[(100 * w + 100) * 4 + 1], 100, 104);
    }

    /// <summary>
    /// Tones must never reverse: what was darker stays darker, for every slider position. An
    /// exposure-style shadow curve (x·2^…) reverses at strong settings.
    /// </summary>
    [Theory]
    [InlineData(1f)]
    [InlineData(0.5f)]
    [InlineData(-0.5f)]
    [InlineData(-1f)]
    public void ShadowAndHighlightCurves_AreMonotonic(float amount)
    {
        float prevS = -1, prevH = -1;
        for (var i = 0; i <= 1000; i++)
        {
            var x = i / 1000f;
            var s = Adjust.ShadowCurve(x, amount);
            var h = Adjust.HighlightCurve(x, amount);
            Assert.True(s >= prevS - 1e-6f, $"Tiefen {amount}: kippt bei {x}");
            Assert.True(h >= prevH - 1e-6f, $"Lichter {amount}: kippt bei {x}");
            (prevS, prevH) = (s, h);
        }
    }

    /// <summary>A pupil in a bright face stays dark; only large dark areas count.</summary>
    [Fact]
    public void Shadows_LeaveSmallDarkSpotsInBrightAreas()
    {
        const int w = 400, h = 400;
        var bgra = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var spot = Math.Abs(x - 200) < 3 && Math.Abs(y - 200) < 3;
            var i = (y * w + x) * 4;
            bgra[i] = bgra[i + 1] = bgra[i + 2] = (byte)(spot ? 25 : 170);
            bgra[i + 3] = 255;
        }

        var result = Adjust.Apply(bgra, w, h, new Adjustments(Shadows: 100));

        Assert.InRange(result[(200 * w + 200) * 4 + 1], 20, 35);
    }

    /// <summary>
    /// Buffer pool: intermediate buffers go back to the pool, the input never does. If it were returned and
    /// reused, the image in the preview cache would change. Two consecutive runs must give the same result and
    /// leave the input unchanged.
    /// </summary>
    [Fact]
    public void PooledBuffers_NeverTouchTheInput()
    {
        const int w = 160, h = 120;
        var px = new float[w * h * 4];
        for (var p = 0; p < w * h; p++)
        {
            px[p * 4] = 0.55f + 0.2f * (p % w) / w;
            px[p * 4 + 1] = 0.5f + 0.3f * (p / w) / (float)h;
            px[p * 4 + 2] = 0.45f;
            px[p * 4 + 3] = 1;
        }
        var copy = (float[])px.Clone();
        var a = new Adjustments(Dehaze: 120, DehazePasses: 4, DehazeEdge: 40, Shadows: 30, Contrast: 10);

        var first = Adjust.Apply(px, w, h, a);
        var second = Adjust.Apply(px, w, h, a);
        Assert.Equal(copy, px);
        Assert.Equal(first, second);

        var prepared = Adjust.Prepare(px, w, h, a);
        var source = (float[])prepared.Source.Clone();
        Adjust.Apply(px, w, h, a);   // uses the pool again
        Assert.Equal(source, prepared.Source);
    }

    /// <summary>Preview: a cancelled plan stops the computation at the next step; the input stays unchanged.</summary>
    [Fact]
    public void CancelledPlan_StopsBetweenSteps()
    {
        const int w = 64, h = 48;
        var px = Enumerable.Repeat(0.5f, w * h * 4).ToArray();
        var copy = (float[])px.Clone();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var plan = new WorkPlan(null, cts.Token);
        Assert.Throws<OperationCanceledException>(() => Adjust.Prepare(px, w, h, new Adjustments(Dehaze: 80), plan));
        Assert.Equal(copy, px);
    }

    [Fact]
    public void Export_AppliesAdjustments_LikeThePreview()
    {
        using var src = new MagickImage(new MagickColor("#606060"), 300, 200);
        var options = new ExportOptions(OutputFormat.Png, 90, false, 300, 200, ResampleFilter.Lanczos3, false,
            0, 0, 1, 96, MetadataMode.KeepAll, ColorMode.KeepProfile, new Adjustments(Brightness: 50));

        using var rendered = ImageExporter.Render(src, options);
        // Same as the preview: 32-bit float buffer, rounded only at the end (here without dithering so that pixels are comparable).
        var expected = Pixels.ToBytes(Adjust.Apply(Pixels.FromMagick(src), 300, 200, options.Adjustments!), dither: false);

        Assert.Equal(expected[(100 * 300 + 150) * 4 + 1], ImageOps.ToBgra(rendered)[(100 * 300 + 150) * 4 + 1]);
    }

    [Fact]
    public async Task ToolSlider_IsUndoable_AndReachesTheExport()
    {
        var vm = new MainViewModel { DeferHistoryTurns = false };
        vm.Shadows = 40;
        vm.SelectedTint = "Farbe";
        vm.TintHue = 210;
        vm.Vibrance = 25;
        vm.MatchLuminance = 30;
        vm.Neutralize = 80;
        vm.Warmth = -20;
        vm.CommitPendingSettings();

        var options = MainViewModel.OptionsFor(vm.CaptureSettings());
        Assert.Equal(new Adjustments(Shadows: 40, Tint: Tint.Color, Vibrance: 25, TintHue: 210, MatchLuminance: 30, Neutralize: 80, Warmth: -20),
            options.Adjustments);

        await vm.UndoCommand.ExecuteAsync(null);
        Assert.Equal(0, vm.Warmth);
        await vm.UndoCommand.ExecuteAsync(null);
        Assert.Equal(0, vm.Neutralize);
        await vm.UndoCommand.ExecuteAsync(null);
        Assert.Equal(0, vm.MatchLuminance);
        await vm.UndoCommand.ExecuteAsync(null);
        Assert.Equal(0, vm.Vibrance);
        await vm.UndoCommand.ExecuteAsync(null);
        Assert.Equal(Adjustments.SepiaHue, vm.TintHue);
        await vm.UndoCommand.ExecuteAsync(null);
        Assert.Equal("Aus", vm.SelectedTint);
        await vm.UndoCommand.ExecuteAsync(null);
        Assert.Equal(0, vm.Shadows);
    }
}
