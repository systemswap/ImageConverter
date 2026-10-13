using ImageConverter.Core.Ai;
using ImageConverter.Core.Imaging;
using ImageMagick;

namespace ImageConverter.Tests;

/// <summary>
/// Zwischenstände für wiederholte Läufe (Größenschätzung, <see cref="LookCache"/>) und die geteilte Vorbereitung
/// (<see cref="Adjust.Dehazed"/> → <see cref="Adjust.Finish"/>): gleiches Ergebnis wie ohne, aber nur ab der ersten
/// geänderten Stufe gerechnet — Tiefen/Lichter, Licht und Farbe, Schärfen, Körnung rechnen den Dunst nicht neu.
/// </summary>
public class LookCacheTests
{
    /// <summary>Foto-ähnliches Testbild: Verlauf mit Struktur und etwas Dunst.</summary>
    private static MagickImage Photo(int w = 320, int h = 220)
    {
        var bytes = new byte[w * h * 3];
        var rng = new Random(7);
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var k = (y * w + x) * 3;
            var haze = 90 + 80 * y / h;
            var detail = 60 * Math.Sin(x * 0.11) * Math.Cos(y * 0.07);
            for (var c = 0; c < 3; c++)
                bytes[k + c] = (byte)Math.Clamp(haze + detail + rng.Next(-12, 13) + c * 10, 0, 255);
        }
        var img = new MagickImage();
        img.ReadPixels(bytes, new PixelReadSettings((uint)w, (uint)h, StorageType.Char, "RGB"));
        return img;
    }

    private static ExportOptions Options(Adjustments? a, double sharpen = 0.5, GrainSettings? grain = null, int w = 240, int h = 165) =>
        new(OutputFormat.Png, 90, false, w, h, ResampleFilter.Lanczos3, false, 0.2, sharpen, 1.0, 72,
            MetadataMode.KeepAll, ColorMode.KeepProfile, a, Grain: grain);

    private static ushort[] Rgb(MagickImage img) => AiEnhancer.ReadPixels(img, "RGB");

    [Fact]
    public void Prepare_IsDehazedThenFinish()
    {
        using var photo = Photo();
        var px = Pixels.FromMagick(photo);
        var a = new Adjustments(Dehaze: 40, Shadows: 30, Highlights: -20, MaskAmount: 50);
        var whole = Adjust.Prepare(px, (int)photo.Width, (int)photo.Height, a);
        var split = Adjust.Finish(Adjust.Dehazed(px, (int)photo.Width, (int)photo.Height, a), (int)photo.Width, (int)photo.Height, a);
        Assert.Equal(whole.Source, split.Source);
        Assert.Equal(whole.Toned, split.Toned);
        // Tiefen/Lichter und Bereichskarten gehören nicht zum Schlüssel des Dunstes, Licht und Farbe auch nicht
        Assert.Equal(a.DehazeKey, (a with { Shadows = 0, Highlights = 0, MaskAmount = 0, Brightness = 20 }).DehazeKey);
        Assert.NotEqual(a.DehazeKey, (a with { Dehaze = 41 }).DehazeKey);
    }

    [Fact]
    public void Cached_IsTheSameImage_AndOnlyRecomputesFromTheChangedStage()
    {
        using var photo = Photo();
        using var cache = new LookCache();
        var a = new Adjustments(Dehaze: 40, Shadows: 25, Brightness: 10, DehazePasses: 2);
        var grain = new GrainSettings(40, Size: 10, Roughness: 20);

        void Same(ExportOptions o, int expectedComputed)
        {
            using var plain = ImageExporter.Render(photo, o);
            using var cached = ImageExporter.Render(photo, o, null, cache);
            Assert.Equal(expectedComputed, cache.Computed);
            Assert.Equal(Rgb(plain), Rgb(cached));
            Assert.Equal(plain.Depth, cached.Depth);
        }

        Same(Options(a, grain: grain), 4);                                          // Eingang, Dunst, Tiefen/Lichter, Licht und Farbe
        Same(Options(a, grain: grain), 0);                                          // nichts geändert
        Same(Options(a, sharpen: 1.2, grain: grain with { Hardness = 50 }), 0);      // Schärfen und Körnung: immer neu, aber billig
        Same(Options(a with { Brightness = 30 }, grain: grain), 1);                 // nur Licht und Farbe
        Same(Options(a with { Shadows = 60 }, grain: grain), 2);                    // Tiefen/Lichter, Dunst bleibt
        Same(Options(a with { Dehaze = 60 }, grain: grain), 3);                     // Dunst neu
        Same(Options(a, grain: grain), 2);                                          // voriger Dunst noch im Speicher
        Same(Options(a, w: 120, h: 83), 4);                                         // andere Größe: andere Probe
        Same(Options(null, sharpen: 0), 0);                                         // ohne Look: das skalierte Bild selbst … (Eingang da)
        Same(Options(null, sharpen: 0, w: 100, h: 69), 1);
    }

    [Fact]
    public void Estimate_WithCache_EqualsWithout()
    {
        using var photo = Photo(640, 440);
        using var cache = new LookCache();
        var o = Options(new Adjustments(Dehaze: 50, Shadows: 20), w: 640, h: 440) with { Format = OutputFormat.Jpeg };
        var plain = SizeEstimator.EstimateSize(photo, o, sampleEdge: 300);
        var first = SizeEstimator.EstimateSize(photo, o, sampleEdge: 300, cache: cache);
        var again = SizeEstimator.EstimateSize(photo, o with { Quality = 70 }, sampleEdge: 300, cache: cache);
        Assert.Equal(plain, first);
        Assert.Equal(0, cache.Computed);   // zweite Probe des zweiten Laufs: alles aus dem Speicher
        Assert.Equal(SizeEstimator.EstimateSize(photo, o with { Quality = 70 }, sampleEdge: 300), again);
    }
}
