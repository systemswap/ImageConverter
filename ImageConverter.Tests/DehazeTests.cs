using ImageConverter.Core.Imaging;

namespace ImageConverter.Tests;

/// <summary>Tests haze removal (dark channel prior), the „Dunst“ slider in the Tools tile.</summary>
public class DehazeTests
{
    private const int W = 240, H = 160;

    /// <summary>
    /// Air light of the test scene. The clear scene consists of saturated colour areas with shading down to black,
    /// so every area has a dark channel. A strip of sky in the colour of the air at the top lets the method estimate
    /// the air light from it, as in real photos.
    /// </summary>
    private const float Air = 0.85f;
    private const int SkyRows = 20;

    private static float[] Scene()
    {
        var rgb = new float[W * H * 3];
        for (var y = 0; y < H; y++)
        for (var x = 0; x < W; x++)
        {
            var i = (y * W + x) * 3;
            if (y < SkyRows) { rgb[i] = rgb[i + 1] = rgb[i + 2] = Air; continue; }
            var block = (x / 20 + y / 20) % 4;
            var shade = ((x * 7 + y * 3) % 20) / 19f;   // 0 (shadow) … 1
            (rgb[i], rgb[i + 1], rgb[i + 2]) = block switch
            {
                0 => (0.8f * shade, 0.2f * shade, 0),
                1 => (0, 0.6f * shade, 0.2f * shade),
                2 => (0.15f * shade, 0.25f * shade, 0.8f * shade),
                _ => (0.1f * shade, 0.1f * shade, 0.1f * shade),
            };
        }
        return rgb;
    }

    /// <summary>Adds haze following the physical model in linear light: image = scene · t + air · (1 − t).</summary>
    private static byte[] Haze(float[] scene, float t, float air)
    {
        var bgra = new byte[W * H * 4];
        for (var p = 0; p < W * H; p++)
        {
            for (var c = 0; c < 3; c++)
                bgra[p * 4 + (2 - c)] = Srgb.ToByte(scene[p * 3 + c] * t + air * (1 - t));
            bgra[p * 4 + 3] = 255;
        }
        return bgra;
    }

    private static float[] Linear(byte[] bgra) =>
        Enumerable.Range(0, W * H * 3).Select(i => Srgb.ToLinear(bgra[i / 3 * 4 + i % 3])).ToArray();

    private static double Contrast(byte[] bgra)
    {
        var lum = Enumerable.Range(0, W * H).Select(p => 0.2126 * bgra[p * 4 + 2] + 0.7152 * bgra[p * 4 + 1] + 0.0722 * bgra[p * 4]).ToArray();
        var mean = lum.Average();
        return Math.Sqrt(lum.Average(v => (v - mean) * (v - mean)));
    }

    private static double Distance(byte[] a, byte[] b) =>
        Enumerable.Range(0, W * H * 4).Where(i => i % 4 != 3).Average(i => Math.Abs(a[i] - b[i]));

    /// <summary>
    /// GPU (if available) and CPU compute the same dehazing (all passes of the chain): on average less than 1/100 of a
    /// level, single pixels at most a few levels (where rounding tips a threshold). Without a GPU the test only
    /// compares the CPU with itself.
    /// </summary>
    [Fact]
    public void Dehaze_GpuAndCpu_Agree()
    {
        var px = Pixels.FromBytes(Haze(Scene(), 0.5f, Air));
        var a = Adjustments.None with { Dehaze = 60, Shadows = 20 };
        float[] cpu, gpu;
        using (GpuFilters.CpuOnly()) cpu = Adjust.Dehazed(px, W, H, a);
        gpu = Adjust.Dehazed(px, W, H, a);
        var diffs = cpu.Zip(gpu).Select(p => Math.Abs(p.First - p.Second) * 255).ToArray();
        Assert.True(diffs.Average() < 0.01, $"im Mittel {diffs.Average():0.0000} Stufen ({GpuFilters.DeviceName ?? "keine Grafikkarte"})");
        Assert.True(diffs.Max() < 6, $"höchstens {diffs.Max():0.00} Stufen");
    }

    /// <summary>
    /// Contrast and colours come back while the brightness stays (brightness
    /// compensation); without the compensation the result would become dark.
    /// </summary>
    [Fact]
    public void HazyImage_GetsContrastAndColourBack_WithoutGettingDark()
    {
        var scene = Scene();
        var clear = Haze(scene, 1, 0);
        var hazy = Haze(scene, 0.5f, Air);

        var result = Adjust.Apply(hazy, W, H, new Adjustments(Dehaze: 100));

        Assert.True(Contrast(result) > Contrast(hazy) * 1.5, $"Kontrast {Contrast(hazy):F1} → {Contrast(result):F1}");
        // Colour clearly comes back, but not fully to the model: the chroma is limited to what the
        // estimated haze has taken away (otherwise green turns ochre, see ThinHaze_…).
        Assert.True(Chroma(result) > 1.8 * Chroma(hazy),
            $"Buntheit {Chroma(hazy):F3} → {Chroma(result):F3} (klar {Chroma(clear):F3})");
        Assert.True(Mean(result) > Mean(hazy) * 0.85, $"Helligkeit {Mean(hazy):F1} → {Mean(result):F1}");
    }

    private static double Mean(byte[] bgra) =>
        Enumerable.Range(0, W * H).Average(p => 0.2126 * bgra[p * 4 + 2] + 0.7152 * bgra[p * 4 + 1] + 0.0722 * bgra[p * 4]);

    /// <summary>Mean Oklab chroma of the landscape (without the sky strip).</summary>
    private static double Chroma(byte[] bgra) => Enumerable.Range(SkyRows * W, (H - SkyRows) * W).Average(p =>
    {
        var c = Oklab.FromSrgb(bgra[p * 4 + 2] / 255f, bgra[p * 4 + 1] / 255f, bgra[p * 4] / 255f);
        return Math.Sqrt(c.A * c.A + c.B * c.B);
    });

    /// <summary>
    /// Slider up to 300: the effect grows steadily, without standing still and without jumps; hardly any channel
    /// that still had detail at 100 is crushed to black. Without a limit, black and red bands appear; with a hard
    /// limit, the slider has no effect above 150.
    /// </summary>
    [Fact]
    public void UpTo300_GetsStrongerSteadily_ButNothingCrushes()
    {
        var scene = Scene();
        for (var i = SkyRows * W * 3; i < scene.Length; i++) scene[i] = 0.15f + 0.6f * scene[i];
        var hazy = Haze(scene, 0.6f, Air);
        var at100 = Adjust.Apply(hazy, W, H, new Adjustments(Dehaze: 100, DehazePasses: 1));

        var previous = Contrast(at100);
        foreach (var amount in new[] { 150, 200, 250, 300 })
        {
            var result = Adjust.Apply(hazy, W, H, new Adjustments(Dehaze: amount, DehazePasses: 1));
            var contrast = Contrast(result);
            Assert.True(contrast > previous, $"Kontrast bei {amount}: {contrast:F1}, davor {previous:F1}");
            Assert.True(contrast < previous * 1.5, $"Sprung bei {amount}: {previous:F1} → {contrast:F1}");
            previous = contrast;

            // Crushed: channel black that still had detail at 100
            var crushed = Enumerable.Range(0, hazy.Length).Count(i => i % 4 != 3 && result[i] < 8 && at100[i] >= 8)
                          / (W * H * 3.0);
            Assert.True(crushed < 0.03, $"{amount}: {crushed:P1} der Kanäle abgesoffen");
        }
        // Here 100 already removes almost all haze (every area has a dark channel); in photos there is
        // more headroom because sky protection and smoothing hold back at 100.
        Assert.True(previous > Contrast(at100) * 1.03, $"Kontrast 100: {Contrast(at100):F1}, 300: {previous:F1}");
    }

    /// <summary>
    /// Clear image: contrast, clarity and sharpness also act without haze, but moderately
    /// (no HDR look), and the brightness stays.
    /// </summary>
    [Fact]
    public void ClearImage_GetsOnlyModeratelyCrisper()
    {
        var clear = Haze(Scene(), 1, 0);
        var result = Adjust.Apply(clear, W, H, new Adjustments(Dehaze: 100, DehazePasses: 1));
        Assert.InRange(Contrast(result) / Contrast(clear), 0.95, 1.3);
        Assert.InRange(Mean(result) / Mean(clear), 0.9, 1.1);
    }

    /// <summary>
    /// The hue stays: thin haze over a green vegetation field. The plain haze model pushes the weak channel of
    /// such vegetation (blue in olive-green fields) towards zero, which turns fields ochre and brown.
    /// </summary>
    [Fact]
    public void ThinHaze_KeepsTheHueOfVegetation()
    {
        var scene = new float[W * H * 3];
        for (var y = 0; y < H; y++)
        for (var x = 0; x < W; x++)
        {
            var i = (y * W + x) * 3;
            if (y < SkyRows) { scene[i] = scene[i + 1] = scene[i + 2] = Air; continue; }
            var shade = 0.4f + 0.6f * (((x * 7 + y * 3) % 20) / 19f);
            (scene[i], scene[i + 1], scene[i + 2]) = (0.05f * shade, 0.20f * shade, 0.16f * shade);   // R, G, B (the order in which Haze reads the scene)
            if ((x + y) % 23 == 0) scene[i] = scene[i + 1] = scene[i + 2] = 0.005f;                  // shadow points
        }
        var hazy = Haze(scene, 0.85f, Air);

        static float Hue(byte[] px, int from) => Enumerable.Range(from, W * H - from).Select(p =>
        {
            var c = Oklab.FromSrgb(px[p * 4 + 2] / 255f, px[p * 4 + 1] / 255f, px[p * 4] / 255f);
            return (c.A, c.B);
        }).Aggregate((A: 0f, B: 0f), (s, c) => (s.A + c.A, s.B + c.B)) is var (a, b) ? Oklab.Hue(a, b) : 0;

        var before = Hue(hazy, SkyRows * W);
        foreach (var amount in new[] { 100, 300 })
        {
            var after = Hue(Adjust.Apply(hazy, W, H, new Adjustments(Dehaze: amount)), SkyRows * W);
            Assert.True(Math.Abs(after - before) < 6, $"Dunst {amount}: Farbton {before:F0}° → {after:F0}°");
        }
    }

    /// <summary>
    /// Haze colour: bluish haze over a green meadow shifts its hue towards blue. With „Dunstfarbe“ at 100 % the
    /// result is closer to the true hue than with 0 % (hue as in the original).
    /// </summary>
    [Fact]
    public void HazeColour_MovesTheHueBackTowardsTheScene()
    {
        var scene = new float[W * H * 3];
        for (var y = 0; y < H; y++)
        for (var x = 0; x < W; x++)
        {
            var i = (y * W + x) * 3;
            var shade = 0.4f + 0.6f * (((x * 7 + y * 3) % 20) / 19f);
            (scene[i], scene[i + 1], scene[i + 2]) = (0.10f * shade, 0.30f * shade, 0.06f * shade);   // R, G, B: meadow
        }
        float[] air = [0.62f, 0.72f, 0.92f];   // R, G, B: bluish haze
        var hazy = new byte[W * H * 4];
        for (var p = 0; p < W * H; p++)
        {
            for (var c = 0; c < 3; c++)
            {
                var t = 0.25f + 0.7f * (p / W) / (H - 1f);   // dense haze at the top, almost clear at the bottom, like a landscape receding into the distance
                hazy[p * 4 + (2 - c)] = Srgb.ToByte(scene[p * 3 + c] * t + air[c] * (1 - t));
            }
            hazy[p * 4 + 3] = 255;
        }

        static double MeanHue(Func<int, (float L, float A, float B)> lab)
        {
            double a = 0, b = 0;
            for (var p = 0; p < W * H; p++) { var c = lab(p); a += c.A; b += c.B; }
            return Oklab.Hue((float)a, (float)b);
        }
        var truth = MeanHue(p => Oklab.FromLinear(scene[p * 3], scene[p * 3 + 1], scene[p * 3 + 2]));
        double HueOf(byte[] px) => MeanHue(p => Oklab.FromSrgb(px[p * 4 + 2] / 255f, px[p * 4 + 1] / 255f, px[p * 4] / 255f));
        static double Off(double h, double t) => Math.Abs(((h - t) % 360 + 540) % 360 - 180);

        var keep = HueOf(Adjust.Apply(hazy, W, H, new Adjustments(Dehaze: 100, DehazeCast: 0)));
        var remove = HueOf(Adjust.Apply(hazy, W, H, new Adjustments(Dehaze: 100, DehazeCast: 100)));
        Assert.True(Off(remove, truth) < Off(keep, truth) - 2,
            $"Wahrheit {truth:F0}°, Dunstfarbe 0 %: {keep:F0}°, 100 %: {remove:F0}°");
    }

    /// <summary>
    /// Passes: each pass estimates again on the intermediate result, so more passes give more contrast without
    /// the image tipping over; the fine-tuning settings count only when dehazing is active.
    /// </summary>
    [Fact]
    public void Passes_RemoveMoreHaze_AndDetailsOnlyCountWithDehaze()
    {
        var hazy = Haze(Scene(), 0.5f, Air);
        var once = Adjust.Apply(hazy, W, H, new Adjustments(Dehaze: 40, DehazePasses: 1));
        var thrice = Adjust.Apply(hazy, W, H, new Adjustments(Dehaze: 40, DehazePasses: 3));
        Assert.True(Contrast(thrice) > Contrast(once) * 1.1, $"Kontrast 1×: {Contrast(once):F1}, 3×: {Contrast(thrice):F1}");
        Assert.InRange(Mean(thrice) / Mean(hazy), 0.6, 1.05);

        Assert.True(new Adjustments(DehazePasses: 5, DehazeClarity: 90, DehazeEdge: 50).IsNone);
        Assert.False(new Adjustments(Dehaze: 10, DehazePasses: 5).IsNone);
        Assert.Equal(new Adjustments(Dehaze: 10, Brightness: 30).PrepareKey, new Adjustments(Dehaze: 10, Contrast: -20).PrepareKey);
        Assert.NotEqual(new Adjustments(Dehaze: 10).PrepareKey, new Adjustments(Dehaze: 10, DehazePasses: 2).PrepareKey);
    }

    /// <summary>
    /// Banding (seen in skies with 3 passes): a smooth 8-bit gradient stays smooth after several passes because it
    /// is computed in floating point, debanded and rounded with dithering at the end. Measured on the row means:
    /// the largest step from row to row relative to the mean slope.
    /// </summary>
    [Fact]
    public void Passes_KeepSkyGradientSmooth()
    {
        const int w = 200, h = 400;
        var sky = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var i = (y * w + x) * 4;
            var v = (double)y / (h - 1);
            (sky[i], sky[i + 1], sky[i + 2], sky[i + 3]) =
                ((byte)Math.Round(235 - 60 * v), (byte)Math.Round(200 - 50 * v), (byte)Math.Round(165 - 40 * v), 255);
        }

        double Steps(byte[] px)
        {
            var rows = new double[h];
            for (var y = 0; y < h; y++)
            {
                double s = 0;
                for (var x = 0; x < w; x++) s += px[(y * w + x) * 4 + 1];
                rows[y] = s / w;
            }
            double worst = 0;
            for (var y = 21; y < h - 20; y++) worst = Math.Max(worst, Math.Abs(rows[y] - rows[y - 1]));
            return worst / (Math.Abs(rows[h - 21] - rows[20]) / (h - 41));
        }

        var plain = Steps(Adjust.Apply(sky, w, h, new Adjustments(Dehaze: 100, DehazePasses: 3, DehazeDeband: 0)));
        var debanded = Steps(Adjust.Apply(sky, w, h, new Adjustments(Dehaze: 100, DehazePasses: 3)));
        Assert.True(debanded < plain, $"Sprung/Anstieg ohne Entstufen {plain:F1}, mit {debanded:F1}");
        Assert.True(debanded < 4, $"Sprung/Anstieg mit Entstufen {debanded:F1} (Original {Steps(sky):F1})");
    }

    [Fact]
    public void NegativeAmount_AddsHaze()
    {
        var clear = Haze(Scene(), 1, 0);
        var result = Adjust.Apply(clear, W, H, new Adjustments(Dehaze: -100));
        Assert.True(Contrast(result) < Contrast(clear) * 0.7);
    }

    [Fact]
    public void AirLight_IsTheHazeColour()
    {
        var air = Dehaze.AirLight(Linear(Haze(Scene(), 0.5f, Air)), W, H);
        Assert.All(air, a => Assert.InRange(a, Air - 0.03f, Air + 0.03f));   // from the sky

        Assert.All(Dehaze.AirLight(Linear(Haze(Scene(), 1, 0)), W, H), a => Assert.True(a >= 0.25f));   // lower bound
    }

    /// <summary>
    /// Air light per direction: darker bluish haze on the left, brighter warm haze on the right (towards the sun);
    /// each side gets its own. A strip consisting only of trees (no sky) falls back to the value for the whole
    /// image instead of taking tree green as haze.
    /// </summary>
    [Fact]
    public void AirLight_FollowsTheDirection_AndIgnoresStripsWithoutSky()
    {
        var lin = new float[W * H * 3];
        for (var y = 0; y < H; y++)
        for (var x = 0; x < W; x++)
        {
            var i = (y * W + x) * 3;
            (float B, float G, float R) c =
                x < W / 8 ? (0.01f, 0.03f, 0.01f)                        // trees only
                : y >= SkyRows ? (0.05f, 0.15f, 0.08f)                   // landscape
                : x < W / 2 ? (0.45f, 0.40f, 0.38f) : (0.80f, 0.85f, 0.95f);   // haze left / right
            (lin[i], lin[i + 1], lin[i + 2]) = c;
        }

        var air = Dehaze.AirLightColumns(lin, W, H);
        var global = Dehaze.AirLight(lin, W, H);
        float At(int x, int c) => air[x * 3 + c];

        Assert.InRange(At(W * 5 / 16, 2), 0.36f, 0.42f);   // left: R of the bluish haze
        Assert.InRange(At(W * 13 / 16, 2), 0.90f, 1.00f);  // right: R of the warm haze
        for (var c = 0; c < 3; c++)
            Assert.InRange(At(0, c), global[c] - 0.02f, global[c] + 0.02f);   // trees: value for the whole image
    }

    [Fact]
    public void MinFilter_MatchesTheNaiveMinimum()
    {
        var rnd = new Random(7);
        const int w = 37, h = 23, r = 3;
        var src = Enumerable.Range(0, w * h).Select(_ => (float)rnd.NextDouble()).ToArray();

        var fast = Dehaze.MinFilter(src, w, h, r);

        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var min = float.MaxValue;
            for (var j = Math.Max(0, y - r); j <= Math.Min(h - 1, y + r); j++)
            for (var i = Math.Max(0, x - r); i <= Math.Min(w - 1, x + r); i++)
                min = Math.Min(min, src[j * w + i]);
            Assert.Equal(min, fast[y * w + x]);
        }
    }
}
