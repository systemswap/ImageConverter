using ImageMagick;

namespace ImageConverter.Core.Imaging;

/// <summary>
/// Schätzt die Dateigröße eines Exports, ohne das Bild in voller Größe zu kodieren.
///
/// Einfaches Hochrechnen nach Pixelzahl schätzt zu hoch: große Bilder komprimieren
/// pro Pixel besser als kleine (gemessen: 49 statt 26 KB). Deshalb werden zwei
/// Proben unterschiedlicher Größe kodiert; aus beiden ergibt sich, wie die Größe
/// mit der Pixelzahl wächst (Potenzgesetz), und damit wird hochgerechnet.
/// Ist das Ziel klein genug, wird direkt in Zielgröße kodiert — dann exakt.
/// </summary>
public static class SizeEstimator
{
    public const int DefaultSampleEdge = 1024;

    public readonly record struct Estimate(long Bytes, bool IsExact);

    /// <param name="source">Bild, aus dem die Proben gerechnet werden. Darf eine bereits
    /// verkleinerte Fassung des Originals sein (schneller), solange sie größer als die Probe ist.</param>
    /// <param name="target">Export-Auftrag mit den echten Zielmaßen.</param>
    public static Estimate EstimateSize(MagickImage source, ExportOptions target,
        int sampleEdge = DefaultSampleEdge, CancellationToken ct = default)
    {
        var tw = target.Width;
        var th = target.Height;

        var s1 = Math.Min(1.0, (double)sampleEdge / Math.Max(tw, th));
        if (s1 >= 1)
            return new Estimate(Encoded(source, target, tw, th, 1, ct), IsExact: true);

        var (w1, h1) = Scaled(tw, th, s1);
        var (w2, h2) = Scaled(tw, th, s1 / 2);
        var b1 = Encoded(source, target, w1, h1, s1, ct);
        var b2 = Encoded(source, target, w2, h2, s1 / 2, ct);

        // Größe ≈ c · Pixel^k. k aus beiden Proben; sinnvoll begrenzt, damit
        // Ausreißer bei winzigen Dateien nicht explodieren.
        var p1 = (double)w1 * h1;
        var p2 = (double)w2 * h2;
        var k = b1 > 0 && b2 > 0 && p1 > p2 ? Math.Log((double)b1 / b2) / Math.Log(p1 / p2) : 1.0;
        k = Math.Clamp(k, 0.4, 1.1);

        var bytes = b1 * Math.Pow((double)tw * th / p1, k);
        return new Estimate((long)Math.Round(bytes), IsExact: false);
    }

    private static (int W, int H) Scaled(int w, int h, double s) =>
        (Math.Max(1, (int)Math.Round(w * s)), Math.Max(1, (int)Math.Round(h * s)));

    private static long Encoded(MagickImage source, ExportOptions target, int w, int h, double scale, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // Schärferadius mitskalieren, damit er relativ zum Bild gleich wirkt
        var o = target with { Width = w, Height = h, SharpenSigma = target.SharpenSigma * scale };
        using var rendered = ImageExporter.Render(source, o);
        ct.ThrowIfCancellationRequested();
        using var ms = new MemoryStream();
        ImageExporter.Encode(rendered, o, ms);
        return ms.Length;
    }
}
