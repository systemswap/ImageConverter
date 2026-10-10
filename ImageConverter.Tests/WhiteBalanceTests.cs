using ImageConverter.Core.Imaging;
using ImageMagick;

namespace ImageConverter.Tests;

/// <summary>Weißabgleich mit Pipetten: Neutral, Schwarz, Weiß — einzeln und zusammen, mehrere Klicks gemittelt.</summary>
public class WhiteBalanceTests
{
    // Foto unter warmem Kunstlicht: dieselbe Lichtfarbe auf Grau, Schwarz und Weiß — je Kanal derselbe Faktor in
    // linearem Licht (so entsteht ein echter Farbstich; verschieden starke Stiche gäbe es in keinem Foto)
    private static readonly double[] Light = [1.0, 0.82, 0.6];

    private static MagickColor UnderLight(double neutralLinear)
    {
        ushort Enc(double lin) => (ushort)Math.Round(WhiteBalance.ToSrgb(Math.Clamp(lin, 0, 1)) * 65535);
        return new MagickColor(Enc(neutralLinear * Light[0]), Enc(neutralLinear * Light[1]), Enc(neutralLinear * Light[2]));
    }

    private static MagickImage CastImage()
    {
        var img = new MagickImage(UnderLight(0.25), 300, 100);                                     // Grau
        using (var black = new MagickImage(UnderLight(0.012), 100, 100)) img.Composite(black, 100, 0, CompositeOperator.Over);
        using (var white = new MagickImage(UnderLight(0.85), 100, 100)) img.Composite(white, 200, 0, CompositeOperator.Over);
        return img;   // links grau, Mitte schwarz, rechts weiß
    }

    private static (double R, double G, double B) Encoded(float[] px, int width, int x, int y)
    {
        var o = (y * width + x) * 4;
        return (px[o + 2], px[o + 1], px[o]);
    }

    private static float[] Balance(MagickImage img, WbSample? n, WbSample? b, WbSample? w)
    {
        var px = Pixels.FromMagick(img);
        return WhiteBalance.Apply(px, (int)img.Width, (int)img.Height, n, b, w);
    }

    private static double Spread((double R, double G, double B) c) =>
        Math.Max(c.R, Math.Max(c.G, c.B)) - Math.Min(c.R, Math.Min(c.G, c.B));

    [Fact]
    public void Neutral_Alone_MakesGrayColorlessAndKeepsBrightness()
    {
        using var img = CastImage();
        var n = WhiteBalance.Measure(img, 50, 50, WbPoint.Neutral)!.Value;
        var before = Encoded(Pixels.FromMagick(img), 300, 50, 50);
        var px = Balance(img, n, null, null);

        var gray = Encoded(px, 300, 50, 50);
        Assert.True(Spread(gray) < 0.01, $"Grau hat noch Farbe: {gray}");
        Assert.True(Spread(before) > 0.08, $"Testbild ohne Stich: {before}");   // vorher deutlich farbig
        var lumBefore = 0.2126 * WhiteBalance.ToLinear(before.R) + 0.7152 * WhiteBalance.ToLinear(before.G) + 0.0722 * WhiteBalance.ToLinear(before.B);
        Assert.Equal(lumBefore, WhiteBalance.ToLinear(gray.G), 2);  // Helligkeit bleibt

        // Der Stich verschwindet auch im Weiß und im Schwarz — es ist dieselbe Lichtfarbe
        Assert.True(Spread(Encoded(px, 300, 250, 50)) < 0.01, $"Weiß hat noch Farbe: {Encoded(px, 300, 250, 50)}");
        Assert.True(Spread(Encoded(px, 300, 150, 50)) < 0.01);
    }

    [Fact]
    public void BlackAndWhite_MakeEndsBlackAndWhite()
    {
        using var img = CastImage();
        var b = WhiteBalance.Measure(img, 150, 50, WbPoint.Black);
        var w = WhiteBalance.Measure(img, 250, 50, WbPoint.White);
        var px = Balance(img, null, b, w);

        var black = Encoded(px, 300, 150, 50);
        var white = Encoded(px, 300, 250, 50);
        Assert.True(black.R < 0.01 && black.G < 0.01 && black.B < 0.01, $"Schwarz: {black}");
        Assert.True(white.R > 0.99 && white.G > 0.99 && white.B > 0.99, $"Weiß: {white}");
    }

    [Fact]
    public void AllThree_GrayNeutral_EndsStay()
    {
        using var img = CastImage();
        var n = WhiteBalance.Measure(img, 50, 50, WbPoint.Neutral);
        var b = WhiteBalance.Measure(img, 150, 50, WbPoint.Black);
        var w = WhiteBalance.Measure(img, 250, 50, WbPoint.White);
        var px = Balance(img, n, b, w);

        Assert.True(Spread(Encoded(px, 300, 50, 50)) < 0.01);
        Assert.True(Encoded(px, 300, 150, 50).G < 0.01);
        Assert.True(Encoded(px, 300, 250, 50).B > 0.99);
    }

    [Fact]
    public void WhiteAlone_WhiteBecomesWhite()
    {
        using var img = CastImage();
        var px = Balance(img, null, null, WhiteBalance.Measure(img, 250, 50, WbPoint.White));
        var white = Encoded(px, 300, 250, 50);
        Assert.True(Spread(white) < 0.01 && white.G > 0.99, $"Weiß: {white}");
    }

    [Fact]
    public void Measure_IgnoresClippedPixels_AndReportsAllClipped()
    {
        using var img = new MagickImage(MagickColors.White, 50, 50);
        Assert.Null(WhiteBalance.Measure(img, 25, 25, WbPoint.Neutral));   // alles ausgefressen
        Assert.Null(WhiteBalance.Measure(img, 25, 25, WbPoint.White));
        Assert.NotNull(WhiteBalance.Measure(img, 25, 25, WbPoint.Black));  // Schwarz misst alles
        Assert.Null(WhiteBalance.Measure(img, 80, 25, WbPoint.Neutral));   // außerhalb
    }

    [Fact]
    public void MoreClicks_AreAveragedByCount()
    {
        var a = new WbSample(0.2, 0.4, 0.6);
        var twice = a.Add(new WbSample(0.4, 0.4, 0.4));
        Assert.Equal(2, twice.Count);
        Assert.Equal(0.3, twice.R, 9);
        var three = twice.Add(new WbSample(0.6, 0.1, 0.2));
        Assert.Equal(3, three.Count);
        Assert.Equal((0.2 + 0.4 + 0.6) / 3, three.R, 9);           // jeder Klick zählt gleich
        Assert.Equal((0.4 + 0.4 + 0.1) / 3, three.G, 9);
    }

    /// <summary>Durch die ganze Kette (Vorschau und Export rechnen über <see cref="Adjust"/>): Weißabgleich wirkt zuerst.</summary>
    [Fact]
    public void ThroughAdjust_GrayBecomesNeutral()
    {
        using var img = CastImage();
        var adj = Adjustments.None with { WbNeutral = WhiteBalance.Measure(img, 50, 50, WbPoint.Neutral) };
        Assert.False(adj.IsNone);
        var px = Adjust.Apply(Pixels.FromMagick(img), 300, 100, adj);
        Assert.True(Spread(Encoded(px, 300, 50, 50)) < 0.012);
        Assert.NotEqual(Adjustments.None.PrepareKey, adj.PrepareKey);   // Vorschau rechnet den vorbereiteten Teil neu
    }
}
