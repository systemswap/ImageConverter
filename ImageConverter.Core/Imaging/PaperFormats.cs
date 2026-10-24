namespace ImageConverter.Core.Imaging;

/// <summary>A standard paper or print size in millimetres, stored in portrait orientation (width ≤ height).</summary>
/// <param name="Key">Identifier used for lookup with <see cref="PaperFormats.Find"/> (case-insensitive).</param>
/// <param name="Name">Display name, for example "A4".</param>
/// <param name="WidthMm">Width in mm.</param>
/// <param name="HeightMm">Height in mm.</param>
public sealed record PaperFormat(string Key, string Name, double WidthMm, double HeightMm)
{
    /// <summary>Gets the menu text, for example "A4 · 210 × 297 mm".</summary>
    public string Label => $"{Name}  ·  {WidthMm:0.#} × {HeightMm:0.#} mm";
}

/// <summary>
/// Provides the standard formats offered for selection (Size → Format) and fits an image into them.
/// </summary>
/// <remarks>
/// Portrait or landscape orientation follows the image. With the aspect-ratio lock closed, the image fits entirely
/// inside with its own aspect ratio and one edge fills the format.
/// </remarks>
public static class PaperFormats
{
    /// <summary>Gets the ISO 216 A series (A0–A7) and DIN long (DL).</summary>
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

    /// <summary>Gets common photo print sizes (nominal size in cm as the name, actual size in mm).</summary>
    public static IReadOnlyList<PaperFormat> Photo { get; } =
    [
        new("9x13", "9 × 13", 89, 127),
        new("10x15", "10 × 15", 102, 152),
        new("13x18", "13 × 18", 127, 178),
        new("15x20", "15 × 20", 152, 203),
        new("20x30", "20 × 30", 203, 305),
        new("30x45", "30 × 45", 305, 457),
    ];

    /// <summary>Gets the US paper sizes Letter and Legal.</summary>
    public static IReadOnlyList<PaperFormat> Us { get; } =
    [
        new("Letter", "Letter", 215.9, 279.4),
        new("Legal", "Legal", 215.9, 355.6),
    ];

    /// <summary>Gets all formats: <see cref="Din"/>, then <see cref="Photo"/>, then <see cref="Us"/>.</summary>
    public static IEnumerable<PaperFormat> All => Din.Concat(Photo).Concat(Us);

    /// <summary>Looks up a format by its <see cref="PaperFormat.Key"/>.</summary>
    /// <param name="key">Key to look for; compared case-insensitively.</param>
    /// <returns>The format, or <see langword="null"/> if no format has this key.</returns>
    public static PaperFormat? Find(string key) =>
        All.FirstOrDefault(f => string.Equals(f.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Computes the print size in mm for an image with the aspect ratio <paramref name="imageAspect"/>.
    /// </summary>
    /// <param name="imageAspect">Image width ÷ height. Values ≤ 0 return the full format, as with
    /// <paramref name="keepAspect"/> off.</param>
    /// <param name="format">Target format; it is turned to landscape if the image is wider than tall (a square image
    /// stays portrait).</param>
    /// <param name="keepAspect"><see langword="true"/>: the image fits entirely inside and one edge fills the format;
    /// <see langword="false"/>: exactly the format (the image is squeezed or stretched).</param>
    /// <returns>Width and height in mm; <c>WidthLeads</c> is <see langword="true"/> if the width fills the format
    /// (the height follows from it).</returns>
    public static (double WidthMm, double HeightMm, bool WidthLeads) Fit(double imageAspect, PaperFormat format, bool keepAspect)
    {
        var landscape = imageAspect > 1;
        var fw = landscape ? format.HeightMm : format.WidthMm;
        var fh = landscape ? format.WidthMm : format.HeightMm;
        if (!keepAspect || imageAspect <= 0) return (fw, fh, true);

        // Wider than the format: the width reaches the edge first
        return imageAspect >= fw / fh
            ? (fw, fw / imageAspect, true)
            : (fh * imageAspect, fh, false);
    }
}
