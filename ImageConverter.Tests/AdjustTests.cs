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

        Assert.True(dark > 65, $"dunkler Bereich nur auf {dark}");   // 40 → ~70: monotone Kurve, siehe Raise
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

    /// <summary>Dynamik: Blasses gewinnt viel, Kräftiges wenig — anders als Sättigung.</summary>
    [Fact]
    public void Vibrance_BoostsMutedColours_MoreThanStrongOnes()
    {
        byte[] muted = [150, 135, 120, 255];    // blasses Blaugrau (B, G, R)
        byte[] strong = [20, 60, 220, 255];     // kräftiges Rot

        var a = new Adjustments(Vibrance: 80);
        var mutedGain = Chroma(Adjust.Apply(muted, 1, 1, a)) / Chroma(muted);
        var strongGain = Chroma(Adjust.Apply(strong, 1, 1, a)) / Chroma(strong);

        Assert.True(mutedGain > 1.4, $"blass nur ×{mutedGain:F2}");
        Assert.True(strongGain < 1.15, $"kräftig ×{strongGain:F2}");
    }

    /// <summary>
    /// Sättigung und Dynamik ändern nur die Buntheit (Oklab): Farbton und Helligkeit bleiben.
    /// In RGB kippte Blau beim Verstärken ins Violette und Gelb wurde heller.
    /// </summary>
    [Theory]
    [InlineData(200, 90, 40)]     // Blau (B, G, R)
    [InlineData(40, 200, 220)]    // Gelb
    [InlineData(150, 135, 120)]   // blasses Blaugrau
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

    /// <summary>Gleiche Farbe, Luminanz: multipliziert die Helligkeit — Mitten deutlich heller, Weiß frisst nicht aus.</summary>
    [Fact]
    public void MatchLuminance_ScalesBrightness_WithoutClippingWhite()
    {
        var result = Adjust.Apply(Ramp(), 256, 1, new Adjustments(MatchLuminance: 50));

        var mid = Oklab.FromSrgb(Gray(result, 100) / 255f, Gray(result, 100) / 255f, Gray(result, 100) / 255f).L;
        var midBefore = Oklab.FromSrgb(100 / 255f, 100 / 255f, 100 / 255f).L;
        Assert.InRange(mid / midBefore, 1.4, 1.55);              // ×1,5 (Referenzwerkzeug: Luminanz 150)
        Assert.True(Gray(result, 200) < Gray(result, 255));       // helle Töne bleiben unterscheidbar
        Assert.Equal(0, Gray(result, 0));                        // Schwarz bleibt
    }

    /// <summary>Zwei Hälften (links/rechts) mit je einer Farbe, als Float-Puffer (ohne Dither).</summary>
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
    /// Luminanz mit Schwellwert nach Bereichen: Ein dunkler Bereich wird so hell wie ohne Grenze, der helle daneben
    /// bleibt — auch direkt an der Kante (kein Hof), anders als mit weichgezeichneter Maske.
    /// </summary>
    [Fact]
    public void MatchLimit_LiftsDarkRegionsFully_AndLeavesBrightOnesIncludingTheEdge()
    {
        const int w = 400, h = 200;
        var src = Halves(w, h, (0.18f, 0.2f, 0.15f), (0.8f, 0.85f, 0.9f));   // dunkler Wald | heller Himmel
        var free = Adjust.Apply(src, w, h, new Adjustments(MatchLuminance: 100));
        var limited = Adjust.Apply(src, w, h, new Adjustments(MatchLuminance: 100, MatchLimit: 45));

        Assert.True(At(limited, w, 50, 100) > At(src, w, 50, 100) + 0.1f, "dunkler Bereich nicht aufgehellt");
        Assert.Equal(At(free, w, 50, 100), At(limited, w, 50, 100), 3);              // so hell wie ohne Grenze
        Assert.True(At(free, w, 300, 100) > At(src, w, 300, 100) + 0.02f);           // ohne Grenze würde der Himmel heller
        for (var x = w / 2; x < w; x += 1)                                           // Himmel bis an die Kante unverändert
            Assert.InRange(At(limited, w, x, 100), At(src, w, x, 100) - 0.004f, At(src, w, x, 100) + 0.004f);
    }

    /// <summary>
    /// Mischen über die Laplace-Pyramide: Weit weg vom Maskenrand genau die jeweilige Fassung, am Rand ein weicher,
    /// breiter Übergang ohne Sprung — und Kanten im Bild bleiben dabei scharf.
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
            var edge = x % 64 < 32 ? 0f : 0.2f;            // feines Streifenmuster (Kanten) in beiden Fassungen
            a[i] = 0.3f + edge;
            b[i] = 0.6f + edge;
            weight[i] = x < w / 2 ? 0 : 1;                  // harter Maskenrand in der Mitte
        }
        var r = PyramidBlend.Blend(a, b, weight, w, h);
        float At(int x) => r[32 * w + x];

        Assert.Equal(a[32 * w + 10], At(10), 2);           // weit links: Fassung a
        Assert.Equal(b[32 * w + 500], At(500), 2);         // weit rechts: Fassung b
        // Streifenkante bei x = 96 (links) bleibt ein Sprung von 0,2 – Kanten werden nicht verwaschen
        Assert.InRange(At(96) - At(95), 0.15f, 0.25f);
        // Am Maskenrand kein Sprung um die volle Differenz (0,3): der Übergang verteilt sich
        var maxStep = Enumerable.Range(200, 112).Where(x => x % 32 != 0).Max(x => Math.Abs(At(x) - At(x - 1)));
        Assert.True(maxStep < 0.12f, $"Sprung {maxStep:0.000} am Maskenrand");
    }

    /// <summary>
    /// Durchblicke: Am Rand eines dunklen Baums scheinen Himmelsstückchen durch, die dunkler sind, als der feste Schutz
    /// heller Pixel greift — sie bleiben trotzdem, weil sie aussehen wie der Himmel dahinter. Gleich große, gleich helle
    /// Sonnenflecken in Gelbgrün werden dagegen aufgehellt (andere Farbe als der Himmel).
    /// </summary>
    [Fact]
    public void MatchLimit_KeepsSkyGapsAtTreeEdge_ButLiftsSunlitLeaves()
    {
        const int w = 400, h = 300;
        var sky = (0.42f, 0.5f, 0.62f);
        var src = Halves(w, h, (0.07f, 0.13f, 0.05f), sky);              // dunkler Baum | Himmel
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
            Spot(w / 2 - 6, y, sky);                                       // Himmel scheint durch
            Spot(w / 2 - 6, y + 6, (0.45f, 0.48f, 0.12f));                 // Sonnenfleck auf Nadeln
        }
        Assert.True(Oklab.FromSrgb(sky.Item1, sky.Item2, sky.Item3).L < 0.62f, "Testhimmel zu hell — der feste Schutz würde greifen");

        var r = Adjust.Apply(src, w, h, new Adjustments(MatchLuminance: 100, MatchLimit: 45));
        int gap = w / 2 - 6, row = 152;
        Assert.InRange(At(r, w, gap, row), At(src, w, gap, row) - 0.02f, At(src, w, gap, row) + 0.02f);     // Lücke bleibt
        Assert.True(At(r, w, gap, row + 6) > At(src, w, gap, row + 6) + 0.05f, "Sonnenfleck nicht aufgehellt");
        Assert.True(At(r, w, 60, 150) > At(src, w, 60, 150) + 0.05f, "Baum nicht aufgehellt");
    }

    /// <summary>
    /// Zielwert: Ein dunkler Bereich wird bis etwa zum Ziel angehoben, ein Bereich im Mittelton darüber bleibt — ohne
    /// Zielwert würde er im selben Verhältnis mit hochgezogen und überstrahlen.
    /// </summary>
    [Fact]
    public void MatchTarget_LiftsDarkAreasToTarget_AndLeavesMidtonesAlone()
    {
        const int w = 400, h = 200;
        var src = Halves(w, h, (0.08f, 0.12f, 0.06f), (0.42f, 0.5f, 0.28f));   // dunkler Baum | sonnige Wiese
        var free = Adjust.Apply(src, w, h, new Adjustments(MatchLuminance: 100));
        var target = Adjust.Apply(src, w, h, new Adjustments(MatchLuminance: 100, MatchTarget: 40));
        float L(float[] px, int x) => Oklab.FromSrgb(px[(100 * w + x) * 4 + 2], px[(100 * w + x) * 4 + 1], px[(100 * w + x) * 4]).L;

        Assert.True(L(free, 300) > L(src, 300) + 0.15f, "ohne Zielwert würde die Wiese stark aufhellen");
        Assert.InRange(L(target, 300), L(src, 300) - 0.01f, L(src, 300) + 0.01f);   // Wiese bleibt
        Assert.InRange(L(target, 60), 0.36f, 0.44f);                              // Baum etwa bis zum Ziel (40 %)
        Assert.True(L(target, 60) > L(src, 60) + 0.1f);
    }

    /// <summary>
    /// Nur Zielwert (ohne Schwellwert, wie in der Praxis benutzt): Der Baum wird zum Ziel hin heller, der Himmel und die
    /// Himmelsstückchen am Baumrand bleiben (dort ist der Bereich schon so hell wie das Ziel), und nichts wird dunkler.
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
        Assert.InRange(At(r, w, gap, row), At(src, w, gap, row) - 0.02f, At(src, w, gap, row) + 0.02f);     // Lücke bleibt
        Assert.True(At(r, w, gap, sun) >= At(src, w, gap, sun) - 0.005f, "Sonnenfleck dunkler geworden");
        Assert.True(At(r, w, 60, 150) > At(src, w, 60, 150) + 0.05f, "Baum nicht aufgehellt");
        Assert.InRange(At(r, w, 300, 150), At(src, w, 300, 150) - 0.01f, At(src, w, 300, 150) + 0.01f);     // Himmel bleibt
    }

    /// <summary>Helle Lücken in einem dunklen Bereich (Himmel zwischen Ästen) leuchten nicht auf.</summary>
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
        Assert.True(At(limited, w, 35, 35) > At(src, w, 35, 35) + 0.1f);                                 // Wald heller
        Assert.InRange(At(limited, w, 140, 140), At(src, w, 140, 140) - 0.01f, At(src, w, 140, 140) + 0.01f);   // Lücke bleibt
    }


    /// <summary>
    /// Luminanz bis 300 %: Mit Zielwert erreicht ein tiefer Schatten das Ziel (mit 200 % nicht), Mitteltöne darüber
    /// bleiben unverändert; mehr als 300 % gibt es nicht (insgesamt heller macht „Gesamt“).
    /// </summary>
    [Fact]
    public void MatchLuminance_UpTo300_LetsDeepShadowsReachTheTarget()
    {
        const int w = 400, h = 200;
        var src = Halves(w, h, (0.03f, 0.05f, 0.025f), (0.42f, 0.5f, 0.28f));   // tiefer Schatten | Wiese
        float L(float[] px, int x) => Oklab.FromSrgb(px[(100 * w + x) * 4 + 2], px[(100 * w + x) * 4 + 1], px[(100 * w + x) * 4]).L;
        var at200 = Adjust.Apply(src, w, h, new Adjustments(MatchLuminance: 100, MatchTarget: 40));
        var at300 = Adjust.Apply(src, w, h, new Adjustments(MatchLuminance: 200, MatchTarget: 40));
        var above = Adjust.Apply(src, w, h, new Adjustments(MatchLuminance: 400, MatchTarget: 60));
        var at300Target60 = Adjust.Apply(src, w, h, new Adjustments(MatchLuminance: 200, MatchTarget: 60));

        Assert.True(L(at200, 60) < 0.34f, $"Mit 200 % schon {L(at200, 60):0.00} — Testschatten nicht dunkel genug");
        Assert.InRange(L(at300, 60), 0.36f, 0.44f);                              // mit 300 % bis etwa zum Ziel
        Assert.InRange(L(at300, 300), L(src, 300) - 0.01f, L(src, 300) + 0.01f); // Wiese bleibt
        Assert.Equal(L(at300Target60, 60), L(above, 60), 3);                      // über 300 % wird begrenzt
    }

    /// <summary>
    /// Luminanz „Gesamt“ wirkt nach der Bereichs-Luminanz auf das ganze Bild: Was der Zielwert angeglichen hat, wird
    /// danach insgesamt heller — die Wiese, die der Zielwert stehen ließ, jetzt auch.
    /// </summary>
    [Fact]
    public void MatchGlobal_BrightensWholeImage_AfterRegionalLuminance()
    {
        const int w = 400, h = 200;
        var src = Halves(w, h, (0.08f, 0.12f, 0.06f), (0.42f, 0.5f, 0.28f));   // dunkler Baum | Wiese
        float L(float[] px, int x) => Oklab.FromSrgb(px[(100 * w + x) * 4 + 2], px[(100 * w + x) * 4 + 1], px[(100 * w + x) * 4]).L;
        var even = Adjust.Apply(src, w, h, new Adjustments(MatchLuminance: 100, MatchTarget: 40));
        var brighter = Adjust.Apply(src, w, h, new Adjustments(MatchLuminance: 100, MatchTarget: 40, MatchGlobal: 25));

        Assert.InRange(L(even, 300), L(src, 300) - 0.01f, L(src, 300) + 0.01f);     // Zielwert lässt die Wiese stehen
        Assert.InRange(L(brighter, 300) / L(even, 300), 1.2f, 1.3f);                  // Gesamt +25 %: auch die Wiese
        Assert.InRange(L(brighter, 60) / L(even, 60), 1.2f, 1.3f);                    // und der angeglichene Baum
        Assert.False(new Adjustments(MatchGlobal: 10).IsNone);
        Assert.Equal(Adjustments.None.PrepareKey, new Adjustments(MatchGlobal: 40).PrepareKey);   // kein teurer Teil
    }

    /// <summary>Luminanzmaske: Dunkles bekommt viel, Mitteltöne anteilig, ab „Auslaufen“ nichts; steiler = Mitte weniger.</summary>
    [Fact]
    public void LumMask_WeightsDarkMost_AndFadesOutAtTheEnd()
    {
        float W(float l, double curve) => LumMask.Weight(l, l, 0.75f, LumMask.Power(curve));
        Assert.Equal(1f, W(0, 30), 3);
        Assert.True(W(0.2f, 30) > W(0.4f, 30) && W(0.4f, 30) > W(0.6f, 30));
        Assert.Equal(0f, W(0.75f, 30));
        Assert.Equal(0f, W(0.9f, 30));
        Assert.True(W(0.4f, 100) < W(0.4f, 0), "steiler Verlauf soll Mitteltönen weniger geben");

        // Über das ganze Bild: Dunkles wird viel heller, Helles über „Auslaufen“ bleibt (Grau 0..255)
        var ramp = Pixels.FromBytes(Ramp());
        var r = Adjust.Apply(ramp, 256, 1, new Adjustments(MaskAmount: 100, MaskEnd: 60));
        float L(float[] px, int x) => Oklab.FromSrgb(px[x * 4 + 2], px[x * 4 + 1], px[x * 4]).L;
        Assert.True(L(r, 40) - L(ramp, 40) > L(r, 120) - L(ramp, 120), "Dunkles soll mehr bekommen als Mitteltöne");
        for (var x = 160; x < 256; x++) Assert.Equal(L(ramp, x), L(r, x), 3);   // über 60 % unverändert
    }

    /// <summary>Luminanzmaske ohne Umkehr der Tonwerte — auch im ungünstigsten Fall (weichster Verlauf, +200 %).</summary>
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

    /// <summary>„Maske zeigen“: Graustufen statt Bild — weiß wo die volle Stärke wirkt, schwarz wo nichts.</summary>
    [Fact]
    public void LumMask_Preview_ShowsTheMaskInGrey()
    {
        var ramp = Pixels.FromBytes(Ramp());
        var r = Adjust.Apply(ramp, 256, 1, new Adjustments(MaskAmount: 80, MaskEnd: 75, MaskPreview: true));
        Assert.True(r[1] > 0.9f, $"Schwarz → Maske fast weiß: {r[1]:0.000}");   // 30 % Bereich: knapp unter Weiß
        Assert.Equal(0f, r[255 * 4 + 1], 3);                         // Weiß → Maske schwarz
        Assert.Equal(r[100 * 4], r[100 * 4 + 2], 4);                 // grau (R = B)
        Assert.False(new Adjustments(MaskPreview: true).IsNone);
        Assert.True(new Adjustments(MaskEnd: 40, MaskCurve: 90).IsNone);   // ohne Stärke wirkt nichts
    }

    /// <summary>Gleiche Farbe, Ausgleichen: ein gelblicher Stich auf grauer Wand wird neutral.</summary>
    [Fact]
    public void Neutralize_RemovesColourCast_ButKeepsStrongColours()
    {
        const int w = 64, h = 64;
        var bgra = new byte[w * h * 4];
        for (var p = 0; p < w * h; p++)
        {
            // Wand mit Kunstlicht-Stich, links unten ein kräftig blaues Kissen
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

    /// <summary>Ausgleichen 50 % nimmt den halben Farbstich weg.</summary>
    [Fact]
    public void Neutralize_Strength_RemovesPartOfTheCast()
    {
        var bgra = new byte[32 * 32 * 4];
        for (var p = 0; p < 32 * 32; p++) (bgra[p * 4], bgra[p * 4 + 1], bgra[p * 4 + 2], bgra[p * 4 + 3]) = (150, 180, 195, 255);

        double Cast(byte[] px) { var c = Oklab.FromSrgb(px[2] / 255f, px[1] / 255f, px[0] / 255f); return Math.Sqrt(c.A * c.A + c.B * c.B); }
        var half = Adjust.Apply(bgra, 32, 32, new Adjustments(Neutralize: 50));

        Assert.InRange(Cast(half) / Cast(bgra), 0.4, 0.6);
    }

    /// <summary>Farbe: rechts wärmer (gelblich), links kühler (bläulich), Schwarz bleibt neutral.</summary>
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
        byte[] skin = [140, 170, 215, 255];     // heller Hautton (B, G, R)
        byte[] sky = [215, 170, 140, 255];      // gleich blass, aber blau

        var a = new Adjustments(Vibrance: 80);
        var skinGain = Chroma(Adjust.Apply(skin, 1, 1, a)) / Chroma(skin);
        var skyGain = Chroma(Adjust.Apply(sky, 1, 1, a)) / Chroma(sky);

        Assert.True(skinGain < skyGain - 0.2, $"Haut ×{skinGain:F2}, Himmel ×{skyGain:F2}");
    }

    /// <summary>
    /// Tiefen lässt Mitteltöne in Ruhe: Gesichter in Innenräumen liegen um 0,4–0,6
    /// (gemessen an einem Familienfoto, das Wangen und Augenpartie mit aufhellte).
    /// </summary>
    [Fact]
    public void Shadows_LeaveMidtonesLikeFaces()
    {
        const int w = 200, h = 200;
        var bgra = new byte[w * h * 4];
        for (var i = 0; i < w * h; i++)
        {
            bgra[i * 4] = bgra[i * 4 + 1] = bgra[i * 4 + 2] = 102;   // ≈ 0,4
            bgra[i * 4 + 3] = 255;
        }

        var result = Adjust.Apply(bgra, w, h, new Adjustments(Shadows: 100));

        Assert.InRange(result[(100 * w + 100) * 4 + 1], 100, 104);
    }

    /// <summary>
    /// Tonwerte dürfen sich nie umdrehen: Was dunkler war, bleibt dunkler — für jede
    /// Reglerstellung. Die alte Tiefen-Kurve (Belichtung x·2^…) kippte bei starken Werten.
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

    /// <summary>Eine Pupille im hellen Gesicht bleibt dunkel — nur weite dunkle Flächen zählen.</summary>
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
    /// Pufferpool (Phase 2): Zwischenstände gehen zurück in den Pool — der Eingang nie. Würde er
    /// zurückgegeben und wiederverwendet, änderte sich das Bild im Zwischenspeicher der Vorschau.
    /// Zwei Rechnungen nacheinander müssen gleich ausfallen, der Eingang unverändert bleiben.
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
        Adjust.Apply(px, w, h, a);   // nutzt den Pool erneut
        Assert.Equal(source, prepared.Source);
    }

    /// <summary>Vorschau (Phase 2): Ein abgebrochener Plan beendet die Rechnung beim nächsten Schritt; der Eingang bleibt.</summary>
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
        // Wie die Vorschau: 32-Bit-Puffer, gerundet erst am Ende (hier ohne Dither, damit Pixel vergleichbar)
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
