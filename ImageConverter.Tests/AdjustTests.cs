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

    /// <summary>Luminanz mit Schwellwert: Dunkles wird heller, Helleres als die Grenze bleibt, die Tonwerte bleiben in Reihenfolge.</summary>
    [Fact]
    public void MatchLimit_BrightensOnlyBelowThreshold_AndStaysMonotonic()
    {
        var limited = Adjust.Apply(Ramp(), 256, 1, new Adjustments(MatchLuminance: 80, MatchLimit: 50));
        var free = Adjust.Apply(Ramp(), 256, 1, new Adjustments(MatchLuminance: 80));

        Assert.True(Gray(limited, 40) > 40 + 15);                   // Dunkles deutlich heller
        for (var v = 102; v <= 255; v++) Assert.InRange(Gray(limited, v), v - 1, v + 1);   // über der Grenze (Oklab L 0,5 ≈ Grau 99) unverändert
        Assert.True(Gray(free, 190) > 190 + 15);                    // ohne Grenze würde es mit aufgehellt
        // Reihenfolge der Töne auf den ungerundeten Werten prüfen (die 8-Bit-Ausgabe hat Dither-Rauschen)
        var exact = Adjust.Apply(Pixels.FromBytes(Ramp()), 256, 1, new Adjustments(MatchLuminance: 80, MatchLimit: 50));
        for (var v = 1; v < 256; v++) Assert.True(exact[v * 4 + 1] >= exact[(v - 1) * 4 + 1] - 1e-5f, $"Umkehr bei {v}");
        Assert.Equal(0, Gray(limited, 0));

        // Wirkt nur beim Aufhellen; 100 % = keine Grenze
        Assert.Equal(Adjust.Apply(Ramp(), 256, 1, new Adjustments(MatchLuminance: -30)),
            Adjust.Apply(Ramp(), 256, 1, new Adjustments(MatchLuminance: -30, MatchLimit: 50)));
        Assert.True(new Adjustments(MatchLimit: 40).IsNone);
        Assert.Equal(free, Adjust.Apply(Ramp(), 256, 1, new Adjustments(MatchLuminance: 80, MatchLimit: 100)));
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
