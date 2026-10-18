using ImageMagick;
using ImageConverter.Core.Platform;

namespace ImageConverter.Core.Imaging;

/// <summary>
/// Der Pixelpuffer der Bildkette: 32-Bit-Fließkomma je Kanal (B, G, R, A), Werte 0–1, sRGB-kodiert.
/// Zwischen den Schritten wird nicht gerundet; auf 8 Bit erst für die Anzeige und beim Speichern in
/// 8-Bit-Formate — einmal, mit Dither, damit dabei keine neuen Stufen entstehen.
/// </summary>
/// <remarks>
/// Bewusst sRGB-kodiert und nicht linear: Kurven, Schärfen, Tiefen/Lichter und Dynamik sind auf
/// kodierte Werte abgestimmt (so abgenommen); die physikalischen Schritte (Dunst, Skalieren) rechnen
/// intern linear. In 32 Bit ist beides gleich genau — linear gespeichert hieße nur doppelt umrechnen.
/// Aus Magick (Q16) kommen die Werte direkt mit 16 Bit, ohne Umweg über 8 Bit.
/// </remarks>
public static class Pixels
{
    private const float Q16 = 65535f;

    public static float[] FromBytes(byte[] bgra)
    {
        var px = new float[bgra.Length];
        Parallel.For(0, bgra.Length / 4, ComputeLimits.Options, p =>
        {
            var i = p * 4;
            px[i] = bgra[i] / 255f;
            px[i + 1] = bgra[i + 1] / 255f;
            px[i + 2] = bgra[i + 2] / 255f;
            px[i + 3] = bgra[i + 3] / 255f;
        });
        return px;
    }

    /// <summary>Die Pixel eines Magick-Bildes mit voller Tiefe (Q16). Das Bild bleibt unverändert.</summary>
    public static float[] FromMagick(MagickImage image)
    {
        lock (image)
        {
            int w = (int)image.Width, h = (int)image.Height;
            var px = PixelPool.Rent(w * h * 4);
            using var pixels = image.GetPixelsUnsafe();
            // In Streifen lesen: ein 16-Bit-Zwischenpuffer des ganzen Bildes wären bei 28 MP weitere 230 MB
            for (var y0 = 0; y0 < h; y0 += StripRows)
            {
                var rows = Math.Min(StripRows, h - y0);
                var data = pixels.ToShortArray(0, y0, (uint)w, (uint)rows, "BGRA")
                           ?? throw new InvalidOperationException("Pixel konnten nicht gelesen werden.");
                var offset = y0 * w * 4;
                Parallel.For(0, rows, ComputeLimits.Options, r =>
                {
                    for (int i = r * w * 4, end = i + w * 4; i < end; i++) px[offset + i] = data[i] / Q16;
                });
            }
            return px;
        }
    }

    /// <summary>Zeilen je Streifen beim Lesen aus Magick.</summary>
    private const int StripRows = 256;

    /// <summary>Ein neues Magick-Bild mit 16 Bit je Kanal aus dem Puffer — in Streifen geschrieben, ohne
    /// 16-Bit-Kopie des ganzen Bildes.</summary>
    /// <param name="alpha">Ob das Ergebnis einen Alphakanal haben soll (sonst wird er verworfen).</param>
    public static MagickImage ToMagick16(float[] px, int width, int height, bool alpha)
    {
        var image = new MagickImage(MagickColors.Black, (uint)width, (uint)height);
        if (alpha) image.Alpha(AlphaOption.Set);
        image.Depth = 16;
        using var pixels = image.GetPixelsUnsafe();
        var channels = (int)pixels.Channels;
        // Reihenfolge der Kanäle im Bild (meist R, G, B[, A]) → Quelle im Puffer (B, G, R, A)
        var from = new int[channels];
        for (var c = 0; c < channels; c++) from[c] = 3;
        foreach (var (channel, source) in new[] { (PixelChannel.Blue, 0), (PixelChannel.Green, 1), (PixelChannel.Red, 2), (PixelChannel.Alpha, 3) })
            if (pixels.GetChannelIndex(channel) is uint index && index < channels) from[index] = source;
        var strip = new ushort[StripRows * width * channels];
        for (var y0 = 0; y0 < height; y0 += StripRows)
        {
            var rows = Math.Min(StripRows, height - y0);
            Parallel.For(0, rows, ComputeLimits.Options, r =>
            {
                for (var x = 0; x < width; x++)
                {
                    var i = ((y0 + r) * width + x) * 4;
                    var o = (r * width + x) * channels;
                    for (var c = 0; c < channels; c++)
                    {
                        var v = px[i + from[c]] * Q16;
                        strip[o + c] = (ushort)(v <= 0 ? 0 : v >= Q16 ? 65535 : v + 0.5f);
                    }
                }
            });
            pixels.SetArea(0, y0, (uint)width, (uint)rows, rows == StripRows ? strip : strip[..(rows * width * channels)]);
        }
        return image;
    }

    /// <param name="dither">Dreieck-Rauschen von ±1 Stufe vor dem Runden (TPDF): Ein weicher Verlauf
    /// zwischen zwei 8-Bit-Stufen wird so zu einem feinen Korn statt zu einer sichtbaren Kante.
    /// Fest je Pixelort — die Vorschau flimmert nicht.</param>
    public static byte[] ToBytes(float[] px, bool dither)
    {
        var bgra = new byte[px.Length];
        Parallel.For(0, px.Length / 4, ComputeLimits.Options, p =>
        {
            var i = p * 4;
            for (var c = 0; c < 3; c++)
            {
                var v = px[i + c] * 255;
                if (dither) v += Dither(v, (uint)(i + c));
                bgra[i + c] = ToByte(v);
            }
            bgra[i + 3] = ToByte(px[i + 3] * 255);
        });
        return bgra;
    }

    /// <summary>
    /// Ein 16-Bit-Bild für ein 8-Bit-Format vorbereiten: jeden Farbkanal mit Dither auf eine 8-Bit-Stufe
    /// setzen (an Ort und Stelle, Profile und Metadaten bleiben). Magick rundet danach verlustfrei.
    /// </summary>
    public static void DitherTo8Bit(MagickImage image)
    {
        using var pixels = image.GetPixelsUnsafe();
        var data = pixels.GetValues() ?? throw new InvalidOperationException("Pixel konnten nicht gelesen werden.");
        var channels = (int)pixels.Channels;
        // Farbkanäle: bei Graustufen der erste, sonst die ersten drei; Alpha wird nur gerundet
        var colour = channels >= 3 ? 3 : 1;
        Parallel.For(0, data.Length / channels, ComputeLimits.Options, p =>
        {
            var i = p * channels;
            for (var c = 0; c < channels; c++)
            {
                var v = data[i + c] / 257f;   // 65535 / 255
                if (c < colour) v += Dither(v, (uint)(i + c));
                data[i + c] = (ushort)(ToByte(v) * 257);
            }
        });
        pixels.SetPixels(data);
        image.Depth = 8;
    }

    private static byte ToByte(float v) => (byte)(v <= 0 ? 0 : v >= 255 ? 255 : v + 0.5f);

    /// <summary>
    /// TPDF-Rauschen (−1 … +1 Stufe) für einen Wert in 8-Bit-Stufen. Werte, die schon genau auf einer
    /// Stufe liegen, bleiben unverändert — ein unbearbeitetes 8-Bit-Bild ändert sich so nicht; Schwarz
    /// und Weiß bleiben rein (das Rauschen läuft zu den Enden hin aus).
    /// </summary>
    private static float Dither(float v, uint key)
    {
        if (MathF.Abs(v - MathF.Round(v)) < 1f / 512) return 0;
        return (Noise(key) + Noise(key ^ 0x9E3779B9u) - 1) * Math.Clamp(Math.Min(v, 255 - v), 0, 1);
    }

    /// <summary>Gleichverteilte Zahl 0 … 1 aus einem Ganzzahl-Schlüssel (Hash, ohne Zustand).</summary>
    internal static float Noise(uint key)
    {
        key ^= key >> 16; key *= 0x7FEB352Du;
        key ^= key >> 15; key *= 0x846CA68Bu;
        key ^= key >> 16;
        return (key >> 8) * (1f / (1 << 24));
    }
}
