namespace ImageConverter.Core.Imaging;

/// <summary>Ein Standardformat in Millimetern (Hochformat: Breite ≤ Höhe).</summary>
public sealed record PaperFormat(string Key, string Name, double WidthMm, double HeightMm)
{
    /// <summary>Für das Menü, z. B. „A4 · 210 × 297 mm“.</summary>
    public string Label => $"{Name}  ·  {WidthMm:0.#} × {HeightMm:0.#} mm";
}

/// <summary>
/// Standardformate zum Auswählen (Größe → Format) und das Einpassen des Bildes. Hoch- oder Querformat folgt
/// dem Bild; bei geschlossener Kette passt das Bild mit gleichem Seitenverhältnis ganz hinein, die eine
/// Kante füllt das Format aus.
/// </summary>
public static class PaperFormats
{
    public static IReadOnlyList<PaperFormat> Din { get; } =
    [
        new("A0", "A0", 841, 1189),
        new("A1", "A1", 594, 841),
        new("A2", "A2", 420, 594),
        new("A3", "A3", 297, 420),
        new("A4", "A4", 210, 297),
        new("A5", "A5", 148, 210),
        new("A6", "A6", 105, 148),
        new("A7", "A7", 74, 105),
        new("DL", "DIN lang", 99, 210),
    ];

    /// <summary>Gängige Fotoabzüge (Nennmaß in cm, tatsächliche Größe in mm).</summary>
    public static IReadOnlyList<PaperFormat> Photo { get; } =
    [
        new("9x13", "9 × 13", 89, 127),
        new("10x15", "10 × 15", 102, 152),
        new("13x18", "13 × 18", 127, 178),
        new("15x20", "15 × 20", 152, 203),
        new("20x30", "20 × 30", 203, 305),
        new("30x45", "30 × 45", 305, 457),
    ];

    public static IReadOnlyList<PaperFormat> Us { get; } =
    [
        new("Letter", "Letter", 215.9, 279.4),
        new("Legal", "Legal", 215.9, 355.6),
    ];

    public static IEnumerable<PaperFormat> All => Din.Concat(Photo).Concat(Us);

    public static PaperFormat? Find(string key) =>
        All.FirstOrDefault(f => string.Equals(f.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Druckgröße in mm für ein Bild mit dem Seitenverhältnis <paramref name="imageAspect"/> (Breite/Höhe).
    /// Hoch- oder Querformat wie das Bild (quadratisch: Hochformat). <paramref name="keepAspect"/>: ganz
    /// hinein, eine Kante füllt das Format; sonst genau das Format (das Bild wird gestaucht/gestreckt).
    /// <c>WidthLeads</c>: die Breite füllt das Format aus (die Höhe ergibt sich).
    /// </summary>
    public static (double WidthMm, double HeightMm, bool WidthLeads) Fit(double imageAspect, PaperFormat format, bool keepAspect)
    {
        var landscape = imageAspect > 1;
        var fw = landscape ? format.HeightMm : format.WidthMm;
        var fh = landscape ? format.WidthMm : format.HeightMm;
        if (!keepAspect || imageAspect <= 0) return (fw, fh, true);

        // Breiter als das Format → die Breite stößt zuerst an
        return imageAspect >= fw / fh
            ? (fw, fw / imageAspect, true)
            : (fh * imageAspect, fh, false);
    }
}
