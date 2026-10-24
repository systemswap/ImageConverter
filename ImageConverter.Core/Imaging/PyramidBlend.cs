namespace ImageConverter.Core.Imaging;

/// <summary>
/// Blends two versions of an image with a mask, level by level of a Laplacian pyramid (Burt and Adelson 1983; the
/// basis of exposure fusion after Mertens 2007).
/// </summary>
/// <remarks>
/// Coarse brightness is blended over large areas, fine edges only over a few pixels, so even a soft mask leaves no halo
/// at edges.
/// </remarks>
internal static class PyramidBlend
{
    /// <summary>Minimum shorter edge (px) of the coarsest level.</summary>
    private const int MinTop = 8;

    /// <summary>
    /// The coarsest level is at most as coarse as one 24th of the longest edge. Any higher, the mask would be averaged
    /// over half the image, and a large bright area would take on light from the dark area next to it. Relative to the
    /// image size, so that preview and export look the same.
    /// </summary>
    private const int TopPerEdge = 24;

    /// <summary>
    /// Computes <paramref name="a"/> · (1 − w) + <paramref name="b"/> · w per pyramid level, with w clamped to 0…1.
    /// Returns a new buffer from the <see cref="PixelPool"/>; the inputs remain unchanged.
    /// </summary>
    public static float[] Blend(float[] a, float[] b, float[] weight, int width, int height)
    {
        var levels = 1;
        var coarsest = Math.Max(MinTop, Math.Max(width, height) / TopPerEdge);
        for (int w = width, h = height; Math.Min(w, h) / 2 >= MinTop && Math.Max(w, h) / 2 >= coarsest && levels < 12;
             w = (w + 1) / 2, h = (h + 1) / 2) levels++;

        var pa = Pyramid.Gaussian(a, width, height, levels);
        var pb = Pyramid.Gaussian(b, width, height, levels);
        var pw = Pyramid.Gaussian(weight, width, height, levels);
        var output = pa.EmptyLike();

        for (var l = 0; l < levels; l++)
        {
            var top = l == levels - 1;
            var la = top ? pa[l] : pa.Laplacian(l);
            var lb = top ? pb[l] : pb.Laplacian(l);
            var wl = pw[l];
            var o = output[l];
            for (var i = 0; i < o.Length; i++) o[i] = la[i] + (lb[i] - la[i]) * Math.Clamp(wl[i], 0, 1);
            if (!top)
            {
                PixelPool.Return(la);
                PixelPool.Return(lb);
            }
        }

        // Level 0 of the Gaussian pyramids is the input itself, so only the coarser levels are returned to the pool
        pa.ReturnLevels(1);
        pb.ReturnLevels(1);
        pw.ReturnLevels(1);
        var result = output.Collapse();
        return result;
    }
}
