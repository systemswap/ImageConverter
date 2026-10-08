namespace ImageConverter.Core.Imaging;

/// <summary>
/// Oklab (Björn Ottosson 2020) — ein wahrnehmungsgleicher Farbraum: L Helligkeit, a/b
/// Farbrichtung; Buntheit C = √(a² + b²), Farbton h = atan2(b, a). Mehr oder weniger Farbe
/// heißt hier nur C ändern — Farbton und empfundene Helligkeit bleiben stehen. In RGB
/// kippt Blau beim Verstärken ins Violette und Gelb wird heller.
/// </summary>
internal static class Oklab
{
    private const int TableSize = 4096;

    // sRGB-Kodierung ↔ linear, als Tabellen (Werte 0–1)
    private static readonly float[] ToLinearTable = BuildTable(v =>
        v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4));
    private static readonly float[] ToSrgbTable = BuildTable(v =>
        v <= 0.0031308 ? 12.92 * v : 1.055 * Math.Pow(v, 1 / 2.4) - 0.055);

    private static float[] BuildTable(Func<double, double> f)
    {
        var t = new float[TableSize + 1];
        for (var i = 0; i <= TableSize; i++) t[i] = (float)f((double)i / TableSize);
        return t;
    }

    private static float Lookup(float[] table, float v)
    {
        var x = Math.Clamp(v, 0, 1) * TableSize;
        var i = (int)x;
        if (i >= TableSize) return table[TableSize];
        return table[i] + (table[i + 1] - table[i]) * (x - i);
    }

    /// <summary>sRGB-kodiert (0–1) → Oklab.</summary>
    public static (float L, float A, float B) FromSrgb(float r, float g, float b)
    {
        r = Lookup(ToLinearTable, r);
        g = Lookup(ToLinearTable, g);
        b = Lookup(ToLinearTable, b);
        var l = MathF.Cbrt(0.4122214708f * r + 0.5363325363f * g + 0.0514459929f * b);
        var m = MathF.Cbrt(0.2119034982f * r + 0.6806995451f * g + 0.1073969566f * b);
        var s = MathF.Cbrt(0.0883024619f * r + 0.2817188376f * g + 0.6299787005f * b);
        return (0.2104542553f * l + 0.7936177850f * m - 0.0040720468f * s,
                1.9779984951f * l - 2.4285922050f * m + 0.4505937099f * s,
                0.0259040371f * l + 0.7827717662f * m - 0.8086757660f * s);
    }

    /// <summary>Oklab → linear (ungeklemmt — Werte außerhalb 0–1 heißen „nicht darstellbar“).</summary>
    private static (float R, float G, float B) ToLinear(float L, float a, float b)
    {
        var l = L + 0.3963377774f * a + 0.2158037573f * b;
        var m = L - 0.1055613458f * a - 0.0638541728f * b;
        var s = L - 0.0894841775f * a - 1.2914855480f * b;
        l = l * l * l; m = m * m * m; s = s * s * s;
        return (4.0767416621f * l - 3.3077115913f * m + 0.2309699292f * s,
               -1.2684380046f * l + 2.6097574011f * m - 0.3413193965f * s,
               -0.0041960863f * l - 0.7034186147f * m + 1.7076147010f * s);
    }

    private static bool InGamut((float R, float G, float B) c) =>
        c.R is >= -1e-4f and <= 1.0001f && c.G is >= -1e-4f and <= 1.0001f && c.B is >= -1e-4f and <= 1.0001f;

    /// <summary>
    /// Buntheit mit <paramref name="factor"/> skalieren, Farbton und Helligkeit fest. Was dabei
    /// nicht mehr darstellbar wäre, wird in der Buntheit zurückgenommen (statt Kanäle hart zu
    /// kappen, was den Farbton verschiebt). Ergebnis sRGB-kodiert, 0–1.
    /// </summary>
    public static (float R, float G, float B) ScaleChroma(float L, float a, float b, float factor)
    {
        var lin = ToLinear(L, a * factor, b * factor);
        if (!InGamut(lin) && factor > 1)
        {
            // Größten darstellbaren Faktor zwischen 1 und factor suchen
            float lo = 1, hi = factor;
            for (var k = 0; k < 8; k++)
            {
                var mid = (lo + hi) / 2;
                if (InGamut(ToLinear(L, a * mid, b * mid))) lo = mid; else hi = mid;
            }
            lin = ToLinear(L, a * lo, b * lo);
        }
        return (Lookup(ToSrgbTable, lin.R), Lookup(ToSrgbTable, lin.G), Lookup(ToSrgbTable, lin.B));
    }

    /// <summary>Farbwinkel in Grad (0–360).</summary>
    public static float Hue(float a, float b)
    {
        var h = MathF.Atan2(b, a) * (180 / MathF.PI);
        return h < 0 ? h + 360 : h;
    }
}
