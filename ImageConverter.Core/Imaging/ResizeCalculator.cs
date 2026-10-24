namespace ImageConverter.Core.Imaging;

/// <summary>
/// Computes target dimensions in pixels.
/// </summary>
/// <remarks>
/// The aspect ratio is a fixed value taken from the source size given to the constructor (when the image is loaded or
/// after a crop), not recomputed from the rounded input fields after every keystroke, because the ratio would drift
/// otherwise. All results are at least 1 px.
/// </remarks>
public sealed class ResizeCalculator
{
    /// <summary>Gets the aspect ratio width ÷ height of the source.</summary>
    public double AspectRatio { get; }

    /// <summary>Gets the source width in px.</summary>
    public int SourceWidth { get; }

    /// <summary>Gets the source height in px.</summary>
    public int SourceHeight { get; }

    /// <summary>Creates a calculator for a source image of the given size.</summary>
    /// <param name="sourceWidth">Source width in px.</param>
    /// <param name="sourceHeight">Source height in px.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="sourceWidth"/> or
    /// <paramref name="sourceHeight"/> is not positive.</exception>
    public ResizeCalculator(int sourceWidth, int sourceHeight)
    {
        if (sourceWidth <= 0)
            throw new ArgumentOutOfRangeException(nameof(sourceWidth));
        if (sourceHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(sourceHeight));

        SourceWidth = sourceWidth;
        SourceHeight = sourceHeight;
        AspectRatio = (double)sourceWidth / sourceHeight;
    }

    /// <summary>
    /// Returns the height that matches an entered width. The entered width stays untouched; the rounding error goes
    /// into the height.
    /// </summary>
    /// <param name="width">Entered width in px.</param>
    /// <returns>Height in px, rounded, at least 1.</returns>
    public int HeightFor(int width) => Clamp((int)Math.Round(width / AspectRatio));

    /// <summary>Returns the width that matches an entered height.</summary>
    /// <param name="height">Entered height in px.</param>
    /// <returns>Width in px, rounded, at least 1.</returns>
    public int WidthFor(int height) => Clamp((int)Math.Round(height * AspectRatio));

    /// <summary>Computes both edges from a percentage of the source size.</summary>
    /// <param name="percent">Scale in percent; 100 is the original size.</param>
    /// <returns>Width and height in px, each rounded separately, at least 1.</returns>
    public (int Width, int Height) FromPercent(double percent) =>
        (Clamp((int)Math.Round(SourceWidth * percent / 100.0)),
         Clamp((int)Math.Round(SourceHeight * percent / 100.0)));

    /// <summary>Computes both edges so that the longest edge equals <paramref name="longestEdge"/>.</summary>
    /// <param name="longestEdge">Target length of the longest edge in px (for a square source, the width).</param>
    /// <returns>Width and height in px, at least 1.</returns>
    public (int Width, int Height) FromLongestEdge(int longestEdge) =>
        SourceWidth >= SourceHeight
            ? (Clamp(longestEdge), HeightFor(longestEdge))
            : (WidthFor(longestEdge), Clamp(longestEdge));

    /// <summary>Computes the largest size that fits entirely inside the box (fit, leaves a margin).</summary>
    /// <param name="boxWidth">Box width in px.</param>
    /// <param name="boxHeight">Box height in px.</param>
    /// <returns>Width and height in px, at least 1.</returns>
    public (int Width, int Height) Fit(int boxWidth, int boxHeight)
    {
        var scale = Math.Min((double)boxWidth / SourceWidth, (double)boxHeight / SourceHeight);
        return (Clamp((int)Math.Round(SourceWidth * scale)),
                Clamp((int)Math.Round(SourceHeight * scale)));
    }

    /// <summary>Computes the smallest size that fills the box completely (cover, the excess is cropped).</summary>
    /// <param name="boxWidth">Box width in px.</param>
    /// <param name="boxHeight">Box height in px.</param>
    /// <returns>Width and height in px, at least 1.</returns>
    public (int Width, int Height) Fill(int boxWidth, int boxHeight)
    {
        var scale = Math.Max((double)boxWidth / SourceWidth, (double)boxHeight / SourceHeight);
        return (Clamp((int)Math.Round(SourceWidth * scale)),
                Clamp((int)Math.Round(SourceHeight * scale)));
    }

    /// <summary>
    /// Computes pixel dimensions from a physical target size and a resolution (DPI mode "resample").
    /// </summary>
    /// <param name="widthInches">Target width in inches.</param>
    /// <param name="heightInches">Target height in inches.</param>
    /// <param name="dpi">Resolution in dots per inch.</param>
    /// <returns>Width and height in px, rounded, at least 1.</returns>
    public static (int Width, int Height) FromPhysicalSize(
        double widthInches, double heightInches, double dpi) =>
        (Clamp((int)Math.Round(widthInches * dpi)),
         Clamp((int)Math.Round(heightInches * dpi)));

    private static int Clamp(int value) => value < 1 ? 1 : value;
}
