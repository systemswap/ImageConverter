namespace ImageConverter.Core.Imaging;

/// <summary>
/// The rectangle around the visible pixels of a transparent image. The filters compute only inside it, as if the image had
/// been cropped to it: that saves the time for the invisible surroundings, and they do not enter any estimate.
/// </summary>
/// <param name="X">Left edge in pixels.</param>
/// <param name="Y">Top edge in pixels.</param>
/// <param name="Width">Width in pixels.</param>
/// <param name="Height">Height in pixels.</param>
public readonly record struct VisibleArea(int X, int Y, int Width, int Height)
{
    /// <summary>Alpha below this counts as invisible: at most one 8-bit level on any background.</summary>
    public const float Invisible = 1f / 255;

    // The filters need some neighbourhood; a few visible pixels should not reach them as a 1 × 1 image
    private const int MinEdge = 16;

    /// <summary>Finds the rectangle around the visible pixels.</summary>
    /// <param name="px">Pixel buffer (B, G, R, A).</param>
    /// <param name="width">Width in pixels.</param>
    /// <param name="height">Height in pixels.</param>
    /// <returns>The rectangle; null if it would be the whole image (the filters then run as usual) or nothing is visible.</returns>
    /// <remarks>Stops at the first visible pixel from each side, so an opaque image costs almost nothing.</remarks>
    public static VisibleArea? Of(float[] px, int width, int height)
    {
        bool Visible(int x, int y) => px[(y * width + x) * 4 + 3] >= Invisible;

        var top = 0;
        while (top < height && !RowHas(top)) top++;
        if (top == height) return null;
        var bottom = height - 1;
        while (!RowHas(bottom)) bottom--;
        var left = 0;
        while (!ColumnHas(left)) left++;
        var right = width - 1;
        while (!ColumnHas(right)) right--;

        bool RowHas(int y)
        {
            for (var x = 0; x < width; x++) if (Visible(x, y)) return true;
            return false;
        }

        bool ColumnHas(int x)
        {
            for (var y = top; y <= bottom; y++) if (Visible(x, y)) return true;
            return false;
        }

        (left, right) = Widen(left, right, width);
        (top, bottom) = Widen(top, bottom, height);
        return left == 0 && top == 0 && right == width - 1 && bottom == height - 1
            ? null
            : new VisibleArea(left, top, right - left + 1, bottom - top + 1);
    }

    /// <summary>Widens a range to at least <see cref="MinEdge"/> within the image.</summary>
    private static (int From, int To) Widen(int from, int to, int size)
    {
        var missing = Math.Min(MinEdge, size) - (to - from + 1);
        if (missing <= 0) return (from, to);
        from = Math.Max(0, from - (missing + 1) / 2);
        to = Math.Min(size - 1, from + Math.Min(MinEdge, size) - 1);
        return (Math.Max(0, to - Math.Min(MinEdge, size) + 1), to);
    }

    /// <summary>Copies the rectangle out of an image.</summary>
    /// <param name="px">Pixels of the whole image; not modified.</param>
    /// <param name="width">Width of the whole image.</param>
    /// <returns>A new buffer from the <see cref="PixelPool"/>.</returns>
    public float[] Cut(float[] px, int width)
    {
        var area = PixelPool.Rent(Width * Height * 4);
        for (var y = 0; y < Height; y++)
            Array.Copy(px, ((y + Y) * width + X) * 4, area, y * Width * 4, Width * 4);
        return area;
    }

    /// <summary>Writes the computed rectangle back into an image.</summary>
    /// <param name="area">The rectangle; not modified.</param>
    /// <param name="px">Pixels of the whole image, overwritten inside the rectangle.</param>
    /// <param name="width">Width of the whole image.</param>
    public void Paste(float[] area, float[] px, int width)
    {
        for (var y = 0; y < Height; y++)
            Array.Copy(area, y * Width * 4, px, ((y + Y) * width + X) * 4, Width * 4);
    }

    /// <summary>
    /// Runs a chain of filters on the visible rectangle only; the surroundings stay as they are.
    /// </summary>
    /// <param name="px">Pixels of the whole image; not modified.</param>
    /// <param name="width">Width in pixels.</param>
    /// <param name="height">Height in pixels.</param>
    /// <param name="chain">Computes the result of a buffer of the given size; must not modify its input, may return it.</param>
    /// <returns>The result of the whole image: what <paramref name="chain"/> returned if there is no smaller rectangle,
    /// otherwise a new buffer from the <see cref="PixelPool"/>.</returns>
    public static float[] Apply(float[] px, int width, int height, Func<float[], int, int, float[]> chain)
    {
        if (Of(px, width, height) is not { } area) return chain(px, width, height);
        var cut = area.Cut(px, width);
        var done = chain(cut, area.Width, area.Height);
        var whole = PixelPool.Rent(px.Length);
        Array.Copy(px, whole, px.Length);
        area.Paste(done, whole, width);
        if (!ReferenceEquals(done, cut)) PixelPool.Return(done);
        PixelPool.Return(cut);
        return whole;
    }
}
