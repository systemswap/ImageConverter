namespace ImageConverter.Core.Imaging;

/// <summary>
/// Luminanzmaske wie in Referenzwerkzeug („Darks I/II/III“, Tony Kuyper): das Bild selbst als Maske, umgekehrt — Dunkles bekommt
/// viel, Mitteltöne anteilig, Helles nichts; pixelgenau (folgt jeder Nadel, kein Hof), weich auslaufend.
/// </summary>
/// <remarks>
/// Gewicht = (1 − Helligkeit / Auslaufen)^p, darüber 0. „Darks II“ ist p = 2 (Maske mit sich selbst malgenommen), je
/// höher p, desto steiler — nur das Dunkelste bekommt viel. Die Helligkeit für die Maske ist zu 70 % die des Pixels und zu
/// 30 % die eingeebnete des Bereichs (<see cref="LuminanceRegions"/>): Eine reine Pixelmaske hebt im Baum die dunklen
/// Nadeln stärker als die sonnigen — die Struktur würde flach. Mischpixel (halb Nadel, halb Himmel) liegen im Mittelton
/// und bekommen nur anteilig — der Schleier zwischen den Zweigen wird schwach.
/// Umkehr der Tonwerte ausgeschlossen: Mit L + (k·L − L)·(1 − L/E)^p bleibt die Kurve steigend, solange
/// 1 + (k − 1)·min[(1 − u)^p − p·u·(1 − u)^(p−1)] ≥ 0 — für p ≥ 1,5 bis k = 3 erfüllt (p = 1 nur bis k = 2,
/// p &lt; 1 nie: am Auslaufpunkt fällt die Kurve). Deshalb Verlauf p = 1,5 … 4 und Stärke bis +200 %.
/// </remarks>
public static class LumMask
{
    /// <summary>Anteil des Pixels an der Helligkeit der Maske (der Rest: Bereich).</summary>
    public const float PixelShare = 0.7f;

    /// <summary>Verlauf 0 … 100 → Exponent 1,5 (weich) … 4 (steil); 30 ≈ 2 (Darks II).</summary>
    public static float Power(double curve) => (float)(1.5 * Math.Pow(4 / 1.5, Math.Clamp(curve, 0, 100) / 100));

    /// <summary>Gewicht der Maske 0 … 1.</summary>
    /// <param name="pixel">Oklab-L des Pixels.</param>
    /// <param name="area">Oklab-L des Bereichs (eingeebnet); ohne Karte = <paramref name="pixel"/>.</param>
    /// <param name="end">Auslaufen ab (Oklab-L): ab hier 0.</param>
    public static float Weight(float pixel, float area, float end, float power)
    {
        var l = PixelShare * pixel + (1 - PixelShare) * area;
        if (l >= end) return 0;
        return MathF.Pow(1 - Math.Max(0, l) / end, power);
    }
}
