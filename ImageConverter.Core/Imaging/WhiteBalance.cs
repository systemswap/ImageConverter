using ImageMagick;

namespace ImageConverter.Core.Imaging;

/// <summary>Welche Pipette: eine Stelle, die grau, schwarz oder weiß sein soll.</summary>
public enum WbPoint { Neutral, Black, White }

/// <summary>
/// Gemessene Farbe einer Pipette in linearem Licht (0–1) und wie viele Klicks darin gemittelt sind.
/// Jeder weitere Klick mit derselben Pipette wird eingerechnet — eine einzelne ungünstige Stelle zählt weniger.
/// </summary>
public readonly record struct WbSample(double R, double G, double B, int Count = 1)
{
    /// <summary>Mittel aus beiden, nach Anzahl der Klicks gewichtet.</summary>
    public WbSample Add(WbSample next)
    {
        var n = Count + next.Count;
        return new WbSample((R * Count + next.R * next.Count) / n, (G * Count + next.G * next.Count) / n,
            (B * Count + next.B * next.Count) / n, n);
    }

    /// <summary>Für die Anzeige: Farbe sRGB-kodiert 0–255.</summary>
    public (byte R, byte G, byte B) ToSrgb8() =>
        (WhiteBalance.Byte(R), WhiteBalance.Byte(G), WhiteBalance.Byte(B));
}

/// <summary>
/// Weißabgleich mit bis zu drei Pipetten, jede allein oder zusammen:
/// <list type="bullet">
/// <item>Schwarz und Weiß setzen je Farbkanal die Tonwert-Enden (wie Referenzwerkzeug „Tonwerte“): die schwarze Stelle wird
/// ganz schwarz, die weiße ganz weiß — das entfernt einen Stich auch getrennt in Schatten und Lichtern.</item>
/// <item>Neutral macht die graue Stelle farblos, ihre Helligkeit bleibt. Mit Weiß über die Mitten je Kanal (Weiß bleibt
/// weiß), ohne Weiß über eine Verstärkung je Kanal — physikalisch das Umrechnen auf anderes Licht.</item>
/// </list>
/// Gerechnet in linearem Licht. Angewendet als erster Schritt der Kette, also auf dasselbe Bild, an dem gemessen wird.
/// </summary>
public static class WhiteBalance
{
    /// <summary>Ab hier gilt ein Kanal als ausgefressen — dort fehlt die Farbinformation, beim Messen bleibt er außen vor.</summary>
    public const double ClippedLevel = 0.985;

    /// <summary>Kleinster Abstand Schwarz → Weiß je Kanal (linear), sonst wäre die Kurve unbrauchbar steil.</summary>
    private const double MinRange = 0.05;

    /// <summary>Halbe Kantenlänge des Messfelds in Pixeln: mindestens 4, bei großen Bildern ein Dreihundertstel der kurzen Seite.</summary>
    public static int SampleRadius(int width, int height) => Math.Max(4, Math.Min(width, height) / 300);

    /// <summary>
    /// Mittlere Farbe um (x, y) in Bildpixeln. Bei Neutral und Weiß zählen ausgefressene Pixel nicht mit; ist alles
    /// ausgefressen (oder liegt die Stelle außerhalb), null.
    /// </summary>
    public static WbSample? Measure(MagickImage image, double x, double y, WbPoint kind)
    {
        int w, h;
        ushort[] data;
        int x0, y0, cw, ch;
        lock (image)
        {
            (w, h) = ((int)image.Width, (int)image.Height);
            if (x < 0 || y < 0 || x >= w || y >= h) return null;
            var r = SampleRadius(w, h);
            x0 = Math.Max(0, (int)x - r);
            y0 = Math.Max(0, (int)y - r);
            cw = Math.Min(w, (int)x + r + 1) - x0;
            ch = Math.Min(h, (int)y + r + 1) - y0;
            using var pixels = image.GetPixelsUnsafe();
            data = pixels.ToShortArray(x0, y0, (uint)cw, (uint)ch, "RGB")
                   ?? throw new InvalidOperationException("Pixel konnten nicht gelesen werden.");
        }

        double sr = 0, sg = 0, sb = 0;
        var count = 0;
        var skipClipped = kind != WbPoint.Black;
        for (var i = 0; i + 2 < data.Length; i += 3)
        {
            double r = data[i] / 65535.0, g = data[i + 1] / 65535.0, b = data[i + 2] / 65535.0;
            if (skipClipped && (r >= ClippedLevel || g >= ClippedLevel || b >= ClippedLevel)) continue;
            sr += ToLinear(r);
            sg += ToLinear(g);
            sb += ToLinear(b);
            count++;
        }
        return count == 0 ? null : new WbSample(sr / count, sg / count, sb / count);
    }

    /// <summary>Ist etwas zu tun?</summary>
    public static bool IsActive(WbSample? neutral, WbSample? black, WbSample? white) =>
        neutral is not null || black is not null || white is not null;

    /// <summary>Kurve je Kanal: Tonwert-Enden, dann Mitten (Exponent) oder Verstärkung.</summary>
    internal readonly record struct Channel(double Black, double White, double Gamma, double Gain)
    {
        public double Map(double linear)
        {
            var v = (linear - Black) / (White - Black);
            if (v <= 0) return 0;
            v = Gamma != 1 ? Math.Pow(Math.Min(v, 1), Gamma) : v;
            return Math.Clamp(v * Gain, 0, 1);
        }
    }

    /// <summary>Die drei Kanalkurven (R, G, B) aus den Messungen.</summary>
    internal static Channel[] Plan(WbSample? neutral, WbSample? black, WbSample? white)
    {
        double[] Of(WbSample s) => [s.R, s.G, s.B];
        var b = black is { } bs ? Of(bs) : [0, 0, 0];
        var w = white is { } ws ? Of(ws) : [1, 1, 1];
        for (var c = 0; c < 3; c++) w[c] = Math.Max(w[c], b[c] + MinRange);

        var gamma = new double[] { 1, 1, 1 };
        var gain = new double[] { 1, 1, 1 };
        if (neutral is { } ns)
        {
            var n = Of(ns);
            var level = new double[3];
            for (var c = 0; c < 3; c++) level[c] = Math.Clamp((n[c] - b[c]) / (w[c] - b[c]), 1e-4, 1);
            // Zielgrau: Helligkeit der Stelle (Rec. 709, linear) — sie soll nur ihre Farbe verlieren
            var target = Math.Clamp(0.2126 * level[0] + 0.7152 * level[1] + 0.0722 * level[2], 1e-4, 0.9999);
            for (var c = 0; c < 3; c++)
            {
                if (white is not null && level[c] < 0.9999)
                    gamma[c] = Math.Clamp(Math.Log(target) / Math.Log(level[c]), 0.2, 5);   // Weiß bleibt weiß
                else
                    gain[c] = Math.Clamp(target / level[c], 0.2, 5);
            }
        }

        return [new(b[0], w[0], gamma[0], gain[0]), new(b[1], w[1], gamma[1], gain[1]), new(b[2], w[2], gamma[2], gain[2])];
    }

    /// <summary>Stützstellen der Kennlinie je Kanal (über den sRGB-kodierten Eingang, dazwischen linear gelesen).</summary>
    private const int TableSize = 4096;

    /// <summary>
    /// Weißabgleich auf einen Pixelpuffer (B, G, R, A; sRGB-kodiert 0–1). Liefert einen neuen Puffer aus dem
    /// <see cref="PixelPool"/>; <paramref name="px"/> bleibt unverändert. Alpha bleibt.
    /// </summary>
    public static float[] Apply(float[] px, int width, int height, WbSample? neutral, WbSample? black, WbSample? white)
    {
        var plan = Plan(neutral, black, white);
        // Kennlinie je Kanal: kodierter Eingang → kodierter Ausgang (Reihenfolge im Puffer: B, G, R)
        var tables = new float[3][];
        for (var c = 0; c < 3; c++)
        {
            var channel = plan[2 - c];   // Puffer B, G, R ↔ Plan R, G, B
            var t = new float[TableSize + 1];
            for (var i = 0; i <= TableSize; i++)
                t[i] = (float)ToSrgb(channel.Map(ToLinear(i / (double)TableSize)));
            tables[c] = t;
        }

        var result = PixelPool.Rent(px.Length);
        var pixels = width * height;
        Parallel.For(0, height, y =>
        {
            for (int i = y * width * 4, end = Math.Min(i + width * 4, pixels * 4); i < end; i += 4)
            {
                result[i] = Lookup(tables[0], px[i]);
                result[i + 1] = Lookup(tables[1], px[i + 1]);
                result[i + 2] = Lookup(tables[2], px[i + 2]);
                result[i + 3] = px[i + 3];
            }
        });
        return result;
    }

    private static float Lookup(float[] table, float v)
    {
        var f = Math.Clamp(v, 0f, 1f) * TableSize;
        var i = (int)f;
        if (i >= TableSize) return table[TableSize];
        return table[i] + (table[i + 1] - table[i]) * (f - i);
    }

    /// <summary>sRGB-kodiert → linear.</summary>
    public static double ToLinear(double v) =>
        v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);

    /// <summary>Linear → sRGB-kodiert.</summary>
    public static double ToSrgb(double v) =>
        v <= 0.0031308 ? v * 12.92 : 1.055 * Math.Pow(v, 1 / 2.4) - 0.055;

    internal static byte Byte(double linear) => (byte)Math.Clamp(Math.Round(ToSrgb(Math.Clamp(linear, 0, 1)) * 255), 0, 255);
}
