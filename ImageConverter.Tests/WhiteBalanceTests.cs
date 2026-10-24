using ImageConverter.Core.Imaging;
using ImageMagick;

namespace ImageConverter.Tests;

/// <summary>White balance with eyedroppers: neutral, black, white — individually and together, several clicks averaged.</summary>
public class WhiteBalanceTests
{
    // Photo under warm artificial light: the same light colour on grey, black and white — the same factor per channel in
    // linear light (this is how a real colour cast arises; casts of different strength would not occur in any photo)
    private static readonly double[] Light = [1.0, 0.82, 0.6];

    private static MagickColor UnderLight(double neutralLinear)
    {
        ushort Enc(double lin) => (ushort)Math.Round(WhiteBalance.ToSrgb(Math.Clamp(lin, 0, 1)) * 65535);
        return new MagickColor(Enc(neutralLinear * Light[0]), Enc(neutralLinear * Light[1]), Enc(neutralLinear * Light[2]));
    }

    private static MagickImage CastImage()
    {
        var img = new MagickImage(UnderLight(0.25), 300, 100);                                     // grey
        using (var black = new MagickImage(UnderLight(0.012), 100, 100)) img.Composite(black, 100, 0, CompositeOperator.Over);
        using (var white = new MagickImage(UnderLight(0.85), 100, 100)) img.Composite(white, 200, 0, CompositeOperator.Over);
        return img;   // left grey, centre black, right white
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
        Assert.True(Spread(before) > 0.08, $"Testbild ohne Stich: {before}");   // clearly coloured before balancing
        var lumBefore = 0.2126 * WhiteBalance.ToLinear(before.R) + 0.7152 * WhiteBalance.ToLinear(before.G) + 0.0722 * WhiteBalance.ToLinear(before.B);
        Assert.Equal(lumBefore, WhiteBalance.ToLinear(gray.G), 2);  // brightness is kept

        // The cast also disappears in white and black — it is the same light colour
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
        Assert.Null(WhiteBalance.Measure(img, 25, 25, WbPoint.Neutral));   // everything clipped
        Assert.Null(WhiteBalance.Measure(img, 25, 25, WbPoint.White));
        Assert.NotNull(WhiteBalance.Measure(img, 25, 25, WbPoint.Black));  // black measures everything
        Assert.Null(WhiteBalance.Measure(img, 80, 25, WbPoint.Neutral));   // outside the image
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
        Assert.Equal((0.2 + 0.4 + 0.6) / 3, three.R, 9);           // every click has the same weight
        Assert.Equal((0.4 + 0.4 + 0.1) / 3, three.G, 9);
    }

    /// <summary>Through the whole chain (preview and export compute via <see cref="Adjust"/>): white balance is applied first.</summary>
    [Fact]
    public void ThroughAdjust_GrayBecomesNeutral()
    {
        using var img = CastImage();
        var adj = Adjustments.None with { WbNeutral = WhiteBalance.Measure(img, 50, 50, WbPoint.Neutral) };
        Assert.False(adj.IsNone);
        var px = Adjust.Apply(Pixels.FromMagick(img), 300, 100, adj);
        Assert.True(Spread(Encoded(px, 300, 50, 50)) < 0.012);
        Assert.NotEqual(Adjustments.None.PrepareKey, adj.PrepareKey);   // the preview recomputes the prepared part
    }
}
