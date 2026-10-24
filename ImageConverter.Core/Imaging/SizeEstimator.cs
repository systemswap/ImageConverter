using ImageMagick;

namespace ImageConverter.Core.Imaging;

/// <summary>
/// Estimates the file size of an export without encoding the image at full size.
/// </summary>
/// <remarks>
/// Scaling up linearly by pixel count overestimates: large images compress better per pixel than small ones (measured:
/// 49 KB estimated instead of 26 KB). Therefore two samples of different sizes are encoded; together they show how the
/// size grows with the pixel count (power law), and the result is extrapolated with that exponent.
/// If the target is small enough, it is encoded directly at the target size, and the result is exact.
/// </remarks>
public static class SizeEstimator
{
    /// <summary>Default long edge of the larger sample in pixels.</summary>
    public const int DefaultSampleEdge = 1024;

    /// <summary>Result of <see cref="EstimateSize"/>.</summary>
    /// <param name="Bytes">Estimated file size in bytes.</param>
    /// <param name="IsExact">True if the image was encoded at the target size, so the size is exact.</param>
    public readonly record struct Estimate(long Bytes, bool IsExact);

    /// <summary>Estimates the encoded file size of <paramref name="source"/> exported with <paramref name="target"/>.</summary>
    /// <param name="source">Image from which the samples are rendered. May be an already downscaled copy of the original
    /// (faster), as long as it is larger than the sample.</param>
    /// <param name="target">Export job with the real target dimensions.</param>
    /// <param name="sampleEdge">Long edge of the larger sample in pixels; the second sample has half that edge. If the
    /// target's long edge does not exceed it, the target size is encoded directly.</param>
    /// <param name="ct">Cancels the estimate; checked before and after each sample and between the processing steps.</param>
    /// <param name="cache">Intermediate results from the previous run on the same <paramref name="source"/>, so that a
    /// slider further down the pipeline recomputes only from its own stage onwards.</param>
    /// <returns>The estimated size and whether it is exact.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    public static Estimate EstimateSize(MagickImage source, ExportOptions target,
        int sampleEdge = DefaultSampleEdge, CancellationToken ct = default, LookCache? cache = null)
    {
        var tw = target.Width;
        var th = target.Height;

        var s1 = Math.Min(1.0, (double)sampleEdge / Math.Max(tw, th));
        if (s1 >= 1)
            return new Estimate(Encoded(source, target, tw, th, 1, ct, cache), IsExact: true);

        var (w1, h1) = Scaled(tw, th, s1);
        var (w2, h2) = Scaled(tw, th, s1 / 2);
        var b1 = Encoded(source, target, w1, h1, s1, ct, cache);
        var b2 = Encoded(source, target, w2, h2, s1 / 2, ct, cache);

        // Size ≈ c · pixels^k. k comes from both samples and is clamped so that outliers with tiny files do not
        // blow up the extrapolation.
        var p1 = (double)w1 * h1;
        var p2 = (double)w2 * h2;
        var k = b1 > 0 && b2 > 0 && p1 > p2 ? Math.Log((double)b1 / b2) / Math.Log(p1 / p2) : 1.0;
        k = Math.Clamp(k, 0.4, 1.1);

        var bytes = b1 * Math.Pow((double)tw * th / p1, k);
        return new Estimate((long)Math.Round(bytes), IsExact: false);
    }

    private static (int W, int H) Scaled(int w, int h, double s) =>
        (Math.Max(1, (int)Math.Round(w * s)), Math.Max(1, (int)Math.Round(h * s)));

    private static long Encoded(MagickImage source, ExportOptions target, int w, int h, double scale, CancellationToken ct,
        LookCache? cache)
    {
        ct.ThrowIfCancellationRequested();
        // Scale the sharpening radius with the sample so that it has the same effect relative to the image.
        var o = target with { Width = w, Height = h, SharpenSigma = target.SharpenSigma * scale };
        // The plan carries only the cancellation token, so that cancellation takes effect between two steps (including
        // between the dehaze passes) rather than only after the sample.
        using var rendered = ImageExporter.Render(source, o, new WorkPlan(null, ct), cache);
        ct.ThrowIfCancellationRequested();
        using var ms = new MemoryStream();
        ImageExporter.Encode(rendered, o, ms);
        return ms.Length;
    }
}
