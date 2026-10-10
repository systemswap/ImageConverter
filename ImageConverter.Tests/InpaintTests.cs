using ImageConverter.Core.Ai;
using ImageConverter.Core.Imaging;
using ImageMagick;

namespace ImageConverter.Tests;

/// <summary>Entfernen: Maske, Stücke, Einsetzen — mit einem Ersatzmodell statt LaMa/MI-GAN.</summary>
public class InpaintTests
{
    /// <summary>Malt alles Markierte in einer festen Farbe und zählt die Aufrufe.</summary>
    private sealed class PaintModel((float R, float G, float B) colour) : IInpaintModel
    {
        public int Size => 64;
        public int Calls;
        public float[] Run(float[] rgb, float[] mask, CancellationToken ct)
        {
            Calls++;
            var s = Size * Size;
            var output = (float[])rgb.Clone();
            for (var i = 0; i < s; i++)
                if (mask[i] > 0) (output[i], output[s + i], output[2 * s + i]) = colour;
            return output;
        }
    }

    private static MagickImage Gradient()
    {
        // Verlauf mit 16-Bit-Werten: jeder Pixel anders, damit „unverändert“ wirklich geprüft wird
        var img = new MagickImage(MagickColors.Black, 400, 300) { Depth = 16 };
        using var pixels = img.GetPixelsUnsafe();
        var data = new ushort[400 * 300 * 3];
        for (var y = 0; y < 300; y++)
        for (var x = 0; x < 400; x++)
        {
            var i = (y * 400 + x) * 3;
            data[i] = (ushort)(x * 163);
            data[i + 1] = (ushort)(y * 217);
            data[i + 2] = (ushort)((x + y) * 93);
        }
        pixels.SetArea(0, 0, 400, 300, data);
        return img;
    }

    private static ushort[] Rgb(MagickImage img)
    {
        using var p = img.GetPixelsUnsafe();
        return p.ToShortArray(0, 0, img.Width, img.Height, "RGB")!;
    }

    [Fact]
    public void FillsMarkedArea_AndLeavesEverythingElseUntouched()
    {
        using var img = Gradient();
        var stroke = new BrushStroke([(100, 100), (160, 110)], 8);
        var model = new PaintModel((1, 0, 0));
        using var result = Inpainter.Run(img, [stroke], model, null, CancellationToken.None);

        var before = Rgb(img);
        var after = Rgb(result);
        var hole = Inpainter.Rasterize([stroke], 400, 300, Inpainter.Grow(400, 300) + 7);   // mit weichem Rand (zweimal ±2 px, diagonal bis knapp 6 px)
        for (var p = 0; p < 400 * 300; p++)
        {
            if (hole[p] == 1) continue;
            Assert.True(before[p * 3] == after[p * 3] && before[p * 3 + 1] == after[p * 3 + 1] && before[p * 3 + 2] == after[p * 3 + 2],
                $"Pixel {p % 400},{p / 400} außerhalb verändert: {before[p * 3]},{before[p * 3 + 1]},{before[p * 3 + 2]} → {after[p * 3]},{after[p * 3 + 1]},{after[p * 3 + 2]}");
        }
        // Mitten im Strich: ganz vom Modell
        var c = (105 * 400 + 130) * 3;
        Assert.True(after[c] > 60000 && after[c + 1] < 2000 && after[c + 2] < 2000, $"innen: {after[c]} {after[c + 1]} {after[c + 2]}");
        Assert.Equal(1, model.Calls);
        Assert.Equal(img.Width, result.Width);
    }

    /// <summary>
    /// Langer Strich in mehreren Stücken: Jeder markierte Pixel wird gefüllt — auch dort, wo ein Ausschnitt endet
    /// (vorher blieben an den Stückgrenzen schmale Streifen offen, die MI-GAN als Rest weitermalte).
    /// </summary>
    [Fact]
    public void LongStroke_InPieces_FillsEveryMarkedPixel()
    {
        using var img = new MagickImage(new MagickColor(20000, 40000, 30000), 640, 800);
        var stroke = new BrushStroke([(70, 665), (570, 665)], 36);
        var model = new PaintModel((1, 0, 0));
        using var result = Inpainter.Run(img, [stroke], model, null, CancellationToken.None);
        Assert.True(model.Calls >= 3, $"{model.Calls} Stücke");

        var after = Rgb(result);
        var core = Inpainter.Rasterize([stroke], 640, 800, 0);
        for (var p = 0; p < 640 * 800; p++)
            if (core[p] == 1)
                Assert.True(after[p * 3] > 60000 && after[p * 3 + 1] < 4000, $"Pixel {p % 640},{p / 640} nicht gefüllt: {after[p * 3]} {after[p * 3 + 1]}");
    }

    [Fact]
    public void InnerPart_KeepsDistanceToCropEdges_ButNotToImageEdges()
    {
        var inner = Inpainter.InnerPart(new Inpainter.Box(100, 0, 200, 200), 1000, 800);
        Assert.Equal((50, 0, 100, 150), (inner.X, inner.Y, inner.Width, inner.Height));   // oben Bildrand: kein Abstand
    }

    [Fact]
    public void Pieces_JoinNearbyStrokes_AndSplitLongOnes()
    {
        // Zwei Striche dicht nebeneinander: ein Stück
        var near = Inpainter.Pieces([new BrushStroke([(100, 100)], 10), new BrushStroke([(125, 100)], 10)], 1000, 1000, 3);
        Assert.Single(near);
        // Weit auseinander: zwei Stücke
        var far = Inpainter.Pieces([new BrushStroke([(100, 100)], 10), new BrushStroke([(800, 800)], 10)], 1000, 1000, 3);
        Assert.Equal(2, far.Count);
        // Langer, schmaler Schriftzug am unteren Rand: in Stücke geteilt, keins viel länger als doppelt so breit
        var line = Inpainter.Pieces([new BrushStroke([(50, 950), (1950, 950)], 20)], 2000, 1000, 3);
        Assert.True(line.Count >= 4, $"{line.Count} Stücke");
        Assert.All(line, b => Assert.True(b.Width <= 2.2 * Math.Max(b.Height, 96)));
        Assert.Equal(line.Min(b => b.X), line[0].X);
        Assert.Equal(line.Sum(b => b.Width), line.Max(b => b.Right) - line.Min(b => b.X));   // lückenlos
    }

    [Fact]
    public void CropAround_StaysInImage_AndIsAboutTwiceThePiece()
    {
        var crop = Inpainter.CropAround(new Inpainter.Box(980, 10, 100, 60), 1100, 900);
        Assert.Equal(200, crop.Width);
        Assert.Equal(200, crop.Height);
        Assert.True(crop.X >= 0 && crop.Right <= 1100 && crop.Y >= 0 && crop.Bottom <= 900);
        // Kleines Bild: höchstens das ganze Bild
        var small = Inpainter.CropAround(new Inpainter.Box(10, 10, 150, 150), 160, 120);
        Assert.Equal((0, 0, 160, 120), (small.X, small.Y, small.Width, small.Height));
    }

    [Fact]
    public void ShrinkMask_KeepsThinLines()
    {
        // Eine 1 Pixel dünne Linie in 640 px Breite: auf 64 verkleinert muss sie erhalten bleiben
        var mask = new byte[640 * 640];
        for (var x = 0; x < 640; x++) mask[333 * 640 + x] = 1;
        var small = Inpainter.ShrinkMask(mask, 640, 640, 64);
        Assert.Equal(64, Enumerable.Range(0, 64).Count(x => small[33 * 64 + x] == 1));
        Assert.Equal(64, small.Count(v => v == 1));
    }

    [Fact]
    public void KeepsAlphaChannel()
    {
        using var img = new MagickImage(new MagickColor(10000, 20000, 30000, 40000), 200, 200);
        using var result = Inpainter.Run(img, [new BrushStroke([(100, 100)], 20)], new PaintModel((0, 1, 0)), null, CancellationToken.None);
        Assert.True(result.HasAlpha);
        using var p = result.GetPixelsUnsafe();
        var px = p.GetPixel(100, 100).ToColor()!;
        Assert.Equal(40000, px.A);
        Assert.True(px.G > 60000);
    }
}
