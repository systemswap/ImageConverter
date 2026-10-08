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
        // Farbe deutlich zurück, aber nicht ganz bis zum Modell: die Buntheit ist auf das begrenzt,
        // was der geschätzte Dunst genommen hat (sonst wurde Grün ocker, siehe ThinHaze_…)
        Assert.True(Chroma(result) > 1.8 * Chroma(hazy),
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

    /// <summary>
    /// Regler bis 300: stufenlos kräftiger, ohne Stillstand und ohne Sprung; kaum ein Kanal
    /// säuft ins Schwarz ab, der bei 100 noch Zeichnung hatte — ohne Grenze gab es schwarze und
    /// rote Bänder, mit einer harten Grenze blieb der Regler ab 150 wirkungslos.
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

            // Abgesoffen: Kanal schwarz, der bei 100 noch Zeichnung hatte
            var crushed = Enumerable.Range(0, hazy.Length).Count(i => i % 4 != 3 && result[i] < 8 && at100[i] >= 8)
                          / (W * H * 3.0);
            Assert.True(crushed < 0.03, $"{amount}: {crushed:P1} der Kanäle abgesoffen");
        }
        // Hier rechnet 100 schon fast alles frei (jede Fläche hat einen dunklen Kanal); in Fotos
        // bleibt mehr Spielraum, weil Himmelsschutz und Glättung bei 100 zurückhalten
        Assert.True(previous > Contrast(at100) * 1.03, $"Kontrast 100: {Contrast(at100):F1}, 300: {previous:F1}");
    }

    /// <summary>
    /// Klares Bild: Wie in Referenzwerkzeug wirken Kontrast, Klarheit und Schärfe auch ohne Dunst —
    /// aber maßvoll (kein HDR-Look), die Helligkeit bleibt.
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
    /// Farbton bleibt: Dünner Dunst über olivgrünem Feld (wenig Blau) — das Modell rechnete
    /// Blau gegen null, Felder wurden ocker und braun.
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
            (scene[i], scene[i + 1], scene[i + 2]) = (0.05f * shade, 0.20f * shade, 0.16f * shade);   // B, G, R
            if ((x + y) % 23 == 0) scene[i] = scene[i + 1] = scene[i + 2] = 0.005f;                  // Schattenpunkte
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
    /// Durchläufe (Beta): Jeder Durchlauf schätzt am Zwischenstand neu — mehr Durchläufe, mehr
    /// Kontrast, ohne dass das Bild kippt; die Feineinstellung zählt nur mit Dunst.
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
    /// Treppen (0.4.43: bei 3 Durchläufen im Himmel): Ein weicher 8-Bit-Verlauf bleibt nach mehreren
    /// Durchläufen weich — gerechnet in Fließkomma, entstuft, am Schluss mit Dither gerundet. Gemessen
    /// am Zeilenmittel: Der größte Sprung von Zeile zu Zeile gegenüber dem mittleren Anstieg.
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
        Assert.All(air, a => Assert.InRange(a, Air - 0.03f, Air + 0.03f));   // aus dem Himmel

        Assert.All(Dehaze.AirLight(Linear(Haze(Scene(), 1, 0)), W, H), a => Assert.True(a >= 0.25f));   // Untergrenze
    }

    /// <summary>
    /// Luftlicht je Richtung: links bläulich-dunkler Dunst, rechts heller warmer (zur Sonne
    /// hin) — jede Seite bekommt ihren. Ein Streifen nur aus Bäumen (kein Himmel) fällt auf
    /// den Wert für das ganze Bild zurück, statt Baumgrün als „Dunst“ zu nehmen.
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
                x < W / 8 ? (0.01f, 0.03f, 0.01f)                        // nur Bäume
                : y >= SkyRows ? (0.05f, 0.15f, 0.08f)                   // Landschaft
                : x < W / 2 ? (0.45f, 0.40f, 0.38f) : (0.80f, 0.85f, 0.95f);   // Dunst links / rechts
            (lin[i], lin[i + 1], lin[i + 2]) = c;
        }

        var air = Dehaze.AirLightColumns(lin, W, H);
        var global = Dehaze.AirLight(lin, W, H);
        float At(int x, int c) => air[x * 3 + c];

        Assert.InRange(At(W * 5 / 16, 2), 0.36f, 0.42f);   // links: R des bläulichen Dunstes
        Assert.InRange(At(W * 13 / 16, 2), 0.90f, 1.00f);  // rechts: R des warmen Dunstes
        for (var c = 0; c < 3; c++)
            Assert.InRange(At(0, c), global[c] - 0.02f, global[c] + 0.02f);   // Bäume: Gesamtwert
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
