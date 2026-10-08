using ImageConverter.Core.Imaging;

namespace ImageConverter.Tests;

/// <summary>Dunst entfernen (Dark Channel Prior), Regler „Dunst“ in der Kachel Tools.</summary>
public class DehazeTests
{
    private const int W = 240, H = 160;

    /// <summary>
    /// Klare Szene: kräftige Farbflächen mit Schatten bis Schwarz — jede Fläche hat einen
    /// dunklen Kanal. Oben ein Streifen Himmel in der Farbe der Luft: Daraus schätzt das
    /// Verfahren das Himmelslicht, wie in echten Fotos.
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
            var shade = ((x * 7 + y * 3) % 20) / 19f;   // 0 (Schatten) … 1
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

    /// <summary>Dunst nach dem physikalischen Modell in linearem Licht: Bild = Szene · t + Luft · (1 − t).</summary>
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
    /// Kontrast und Farben kommen zurück, die Helligkeit bleibt (Helligkeitsausgleich wie bei
    /// Adobe) — ohne ihn wurde das Ergebnis dunkel.
    /// </summary>
    [Fact]
    public void HazyImage_GetsContrastAndColourBack_WithoutGettingDark()
    {
        var scene = Scene();
        var clear = Haze(scene, 1, 0);
        var hazy = Haze(scene, 0.5f, Air);

        var result = Adjust.Apply(hazy, W, H, new Adjustments(Dehaze: 100));

        Assert.True(Contrast(result) > Contrast(hazy) * 1.5, $"Kontrast {Contrast(hazy):F1} → {Contrast(result):F1}");
        Assert.True(Chroma(result) > Chroma(hazy) + 0.5 * (Chroma(clear) - Chroma(hazy)),
            $"Buntheit {Chroma(hazy):F3} → {Chroma(result):F3} (klar {Chroma(clear):F3})");
        Assert.True(Mean(result) > Mean(hazy) * 0.85, $"Helligkeit {Mean(hazy):F1} → {Mean(result):F1}");
    }

    private static double Mean(byte[] bgra) =>
        Enumerable.Range(0, W * H).Average(p => 0.2126 * bgra[p * 4 + 2] + 0.7152 * bgra[p * 4 + 1] + 0.0722 * bgra[p * 4]);

    /// <summary>Mittlere Oklab-Buntheit der Landschaft (ohne Himmelsstreifen).</summary>
    private static double Chroma(byte[] bgra) => Enumerable.Range(SkyRows * W, (H - SkyRows) * W).Average(p =>
    {
        var c = Oklab.FromSrgb(bgra[p * 4 + 2] / 255f, bgra[p * 4 + 1] / 255f, bgra[p * 4] / 255f);
        return Math.Sqrt(c.A * c.A + c.B * c.B);
    });

    [Fact]
    public void ClearImage_StaysAlmostTheSame()
    {
        var clear = Haze(Scene(), 1, 0);
        var result = Adjust.Apply(clear, W, H, new Adjustments(Dehaze: 100));
        Assert.True(Distance(result, clear) < 6, $"mittlere Änderung {Distance(result, clear):F1}");
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
        Assert.All(air, a => Assert.InRange(a, Air - 0.03f, Air + 0.03f));   // aus dem Himmel

        Assert.All(Dehaze.AirLight(Linear(Haze(Scene(), 1, 0)), W, H), a => Assert.True(a >= 0.25f));   // Untergrenze
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
