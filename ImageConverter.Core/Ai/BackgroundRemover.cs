using ImageConverter.Core.Ai.Upscale;
using ImageConverter.Core.Imaging;
using ImageConverter.Core.Localization;
using ImageMagick;
using Microsoft.ML.OnnxRuntime;

namespace ImageConverter.Core.Ai;

/// <summary>What happens to the background once the subject has been found.</summary>
public enum BackgroundMode
{
    /// <summary>The background becomes transparent (alpha channel).</summary>
    Transparent,

    /// <summary>The background stays, blurred (portrait effect); the image stays opaque.</summary>
    Blur,
}

/// <summary>A segmentation model: one square crop in, the probability of "subject" per pixel out.</summary>
public interface ISegmentModel
{
    /// <summary>Gets the edge length the model works with in pixels (1024 for the supported models).</summary>
    int Size { get; }

    /// <summary>Computes the mask of one crop.</summary>
    /// <param name="rgb">Crop as R, G and B planes one after another, sRGB 0 … 1, <see cref="Size"/>² values per plane.</param>
    /// <param name="ct">Cancels the computation.</param>
    /// <returns>Subject probability 0 … 1, <see cref="Size"/>² values.</returns>
    float[] Run(float[] rgb, CancellationToken ct);
}

/// <summary>Background model over ONNX (BiRefNet, BEN2, RMBG-2.0): NCHW, RGB, ImageNet-normalised except BEN2.</summary>
/// <param name="session">Open session of the model; stays owned by the caller.</param>
/// <param name="model">Catalogue entry (input size in <see cref="AiModel.TileSize"/>).</param>
public sealed class OnnxSegmentModel(InferenceSession session, AiModel model) : ISegmentModel
{
    private static readonly float[] Mean = [0.485f, 0.456f, 0.406f];
    private static readonly float[] Std = [0.229f, 0.224f, 0.225f];

    /// <inheritdoc />
    public int Size => model.TileSize;

    /// <inheritdoc />
    /// <remarks>
    /// BEN2 expects plain 0 … 1 values; the BiRefNet family (also RMBG-2.0) expects ImageNet normalisation. BiRefNet returns
    /// logits, the others probabilities — the value range tells them apart (docs/KI-HINTERGRUND.md).
    /// </remarks>
    public float[] Run(float[] rgb, CancellationToken ct)
    {
        var plane = Size * Size;
        var input = new float[3 * plane];
        var normalise = model.Id != AiModelCatalog.Ben2.Id;
        for (var c = 0; c < 3; c++)
        for (var i = 0; i < plane; i++)
            input[c * plane + i] = normalise ? (rgb[c * plane + i] - Mean[c]) / Std[c] : rgb[c * plane + i];

        ct.ThrowIfCancellationRequested();
        using var value = OrtValue.CreateTensorValueFromMemory(input, [1, 3, Size, Size]);
        var output = OnnxSessions.Run(session, new Dictionary<string, OrtValue> { [session.InputNames[0]] = value }, ct);
        float min = float.MaxValue, max = float.MinValue;
        foreach (var v in output) { if (v < min) min = v; if (v > max) max = v; }
        if (min < -0.01f || max > 1.01f)
            for (var i = 0; i < output.Length; i++) output[i] = 1 / (1 + MathF.Exp(-output[i]));   // logits → probability
        return output;
    }
}

/// <summary>
/// The subject mask of one region of the image at working resolution, with the reduced image as guide for refining it.
/// </summary>
/// <param name="Region">Region of the pixel image the mask covers (the displayed crop); outside it everything is background.</param>
/// <param name="Width">Width of the mask (at most <see cref="BackgroundRemover.WorkEdge"/>).</param>
/// <param name="Height">Height of the mask.</param>
/// <param name="Alpha">Subject probability 0 … 1, row by row.</param>
/// <param name="Guide">The region at the same size, interleaved R, G, B (sRGB 0 … 1).</param>
public sealed record BackgroundMask(SourceRect Region, int Width, int Height, float[] Alpha, float[] Guide);

/// <summary>
/// Removes or blurs the background: the model finds the subject automatically; red and green brush strokes, painted
/// roughly, say what goes and what stays; the edges always come from the model and the photo. See docs/KI-HINTERGRUND.md.
/// </summary>
/// <remarks>
/// <para>Steps: the model computes the region at its size (<see cref="Detect"/>); strokes act on whole objects
/// (<see cref="Edit"/>); the mask is refined edge-aware at full resolution (fast guided filter: coefficients at working
/// resolution, applied with the full-resolution photo) and the foreground colours are estimated, so that no fringe of the
/// old background remains in hair (<see cref="Compose"/>).</para>
/// <para>Red removes every object it touches (and leftovers under the stroke); green protects objects from red and adds
/// what a second, zoomed run of the model finds around the green stroke — for parts of the subject the first run missed.</para>
/// </remarks>
public static class BackgroundRemover
{
    /// <summary>Longest edge of the working resolution of mask and guide.</summary>
    public const int WorkEdge = 2048;

    // Guided filter: window as Gaussian sigma in working pixels and regularisation (alpha 0 … 1, colour 0 … 1)
    private const double GuideSigma = 2.0;
    private const float GuideEps = 1e-3f;

    /// <summary>Computes the subject mask of a region with the model.</summary>
    /// <param name="image">Pixel image; not modified.</param>
    /// <param name="region">Region of <paramref name="image"/> (the displayed crop, or a zoomed part of it).</param>
    /// <param name="model">Segmentation model.</param>
    /// <param name="ct">Cancels the computation.</param>
    /// <returns>The mask at working resolution.</returns>
    public static BackgroundMask Detect(MagickImage image, SourceRect region, ISegmentModel model, CancellationToken ct)
    {
        var s = Math.Min(1.0, WorkEdge / (double)Math.Max(region.Width, region.Height));
        int w = Math.Max(1, (int)Math.Round(region.Width * s)), h = Math.Max(1, (int)Math.Round(region.Height * s));
        var guide = RegionRgb(image, region, w, h, interleaved: true);
        var input = RegionRgb(image, region, model.Size, model.Size, interleaved: false);
        ct.ThrowIfCancellationRequested();
        var raw = model.Run(input, ct);
        return new BackgroundMask(region, w, h, Resample(raw, model.Size, model.Size, w, h), guide);
    }

    /// <summary>
    /// Applies the brush strokes to the detected mask: red removes the objects it touches, green keeps them and adds what a
    /// zoomed run finds around the green strokes.
    /// </summary>
    /// <param name="mask">Detected mask.</param>
    /// <param name="strokes">Strokes in pixel-image coordinates; <see cref="BrushStroke.Keep"/> = green.</param>
    /// <param name="zoomDetect">Runs the model on a part of the image (pixel-image rectangle); null = no zoomed run.</param>
    /// <returns>The edited mask (a new array).</returns>
    public static float[] Edit(BackgroundMask mask, IReadOnlyList<BrushStroke> strokes, Func<SourceRect, BackgroundMask>? zoomDetect)
    {
        var alpha = (float[])mask.Alpha.Clone();
        if (strokes.Count == 0) return alpha;
        int w = mask.Width, h = mask.Height;
        var scale = w / (double)mask.Region.Width;
        BrushStroke ToWork(BrushStroke st) => st with
        {
            Points = st.Points.Select(p => ((p.X - mask.Region.X) * scale, (p.Y - mask.Region.Y) * scale)).ToArray(),
            Radius = Math.Max(1, st.Radius * scale),
        };
        var green = Inpainter.Rasterize(strokes.Where(st => st.Keep).Select(ToWork).ToArray(), w, h, 0);
        var red = Inpainter.Rasterize(strokes.Where(st => !st.Keep).Select(ToWork).ToArray(), w, h, 0);

        // Green first: objects found by a zoomed run around the green strokes (the first run missed them)
        if (zoomDetect is not null && Bounds(green, w, h) is { } box)
        {
            var zoom = zoomDetect(ToPixels(Grow(box, w, h), mask, scale));
            var zoomAlpha = SampleInto(zoom, mask);
            var zoomLabels = Components(zoomAlpha, w, h);
            var picked = Touched(zoomLabels, green);
            for (var i = 0; i < alpha.Length; i++)
                if (zoomLabels[i] > 0 && picked.Contains(zoomLabels[i])) alpha[i] = Math.Max(alpha[i], zoomAlpha[i]);
        }

        // Then red: whole objects it touches, unless green touches them too, and leftovers under the stroke
        var labels = Components(alpha, w, h);
        var keep = Touched(labels, green);
        var drop = Touched(labels, red);
        drop.ExceptWith(keep);
        for (var i = 0; i < alpha.Length; i++)
        {
            var label = labels[i];
            if (label > 0 && drop.Contains(label)) alpha[i] = 0;
            else if (red[i] != 0 && (label == 0 || !keep.Contains(label))) alpha[i] = 0;
        }
        return alpha;
    }

    /// <summary>
    /// Builds the result in full resolution: the mask refined at the edges of the photo, foreground colours without the old
    /// background, and the background transparent or blurred.
    /// </summary>
    /// <param name="image">Pixel image; not modified.</param>
    /// <param name="mask">Mask (from <see cref="Detect"/>) with <paramref name="alpha"/> as its edited values.</param>
    /// <param name="alpha">Edited mask (<see cref="Edit"/>), same size as <paramref name="mask"/>.</param>
    /// <param name="mode">Transparent or blurred background.</param>
    /// <param name="blur">Blur of the background, 0 … 1 (fraction of the long edge × 0.02 as Gaussian sigma).</param>
    /// <param name="ct">Cancels the computation.</param>
    /// <returns>A new image (with alpha channel when transparent); full bit depth, profiles kept.</returns>
    public static MagickImage Compose(MagickImage image, BackgroundMask mask, float[] alpha, BackgroundMode mode, double blur,
        CancellationToken ct)
    {
        int w = mask.Width, h = mask.Height, n = w * h;
        var (coeffA, coeffB) = GuidedColor.Coefficients(mask.Guide, alpha, w, h, GuideSigma, GuideEps);
        ct.ThrowIfCancellationRequested();

        // Refined mask at working resolution, for the colour estimate and the blurred background
        var refined = new float[n];
        Parallel.For(0, h, y =>
        {
            for (int x = 0, i = y * w; x < w; x++, i++)
                refined[i] = Math.Clamp(coeffA[i * 3] * mask.Guide[i * 3] + coeffA[i * 3 + 1] * mask.Guide[i * 3 + 1]
                                        + coeffA[i * 3 + 2] * mask.Guide[i * 3 + 2] + coeffB[i], 0, 1);
        });
        var (fore, back) = ForegroundEstimate(mask.Guide, refined, w, h);
        ct.ThrowIfCancellationRequested();
        var blurred = mode == BackgroundMode.Blur ? BlurredBackground(mask.Guide, refined, w, h, blur) : null;

        var result = ImageOps.CloneOf(image);
        if (result.ColorSpace == ColorSpace.CMYK) result.ColorSpace = ColorSpace.sRGB;
        result.ColorType = mode == BackgroundMode.Transparent || result.HasAlpha ? ColorType.TrueColorAlpha : ColorType.TrueColor;
        // A private pixel buffer: a fresh copy shares its pixels, writing into it blackened the rest (No. 35)
        result.Evaluate(Channels.All, EvaluateOperator.Add, 0);

        var region = mask.Region;
        int imageW = (int)result.Width, imageH = (int)result.Height;
        using (var pixels = result.GetPixelsUnsafe())
        {
            var channels = (int)pixels.Channels;
            int Index(PixelChannel c) => pixels.GetChannelIndex(c) is uint i ? (int)i : throw new InvalidOperationException(c.ToString());
            int ri = Index(PixelChannel.Red), gi = Index(PixelChannel.Green), bi = Index(PixelChannel.Blue);
            var ai = pixels.GetChannelIndex(PixelChannel.Alpha) is uint a && a < channels ? (int)a : -1;
            var scale = w / (double)region.Width;
            const int strip = 128;
            var strips = (imageH + strip - 1) / strip;
            var rows = new ushort[strips][];
            Parallel.For(0, strips, k =>
            {
                ct.ThrowIfCancellationRequested();
                int y0 = k * strip, y1 = Math.Min(imageH, y0 + strip);
                ushort[] area;
                lock (pixels) area = pixels.GetArea(0, y0, (uint)imageW, (uint)(y1 - y0))!;
                Span<float> f = stackalloc float[3];
                for (var y = y0; y < y1; y++)
                for (var x = 0; x < imageW; x++)
                {
                    var o = ((y - y0) * imageW + x) * channels;
                    var inside = x >= region.X && x < region.X + region.Width && y >= region.Y && y < region.Y + region.Height;
                    if (!inside)
                    {
                        if (mode == BackgroundMode.Transparent && ai >= 0) area[o + ai] = 0;   // outside the crop: background
                        continue;
                    }
                    float r = area[o + ri] / 65535f, g = area[o + gi] / 65535f, b = area[o + bi] / 65535f;
                    var cell = new Bilinear(w, h, (x - region.X + 0.5) * scale - 0.5, (y - region.Y + 0.5) * scale - 0.5);
                    var al = Math.Clamp(cell.Of(coeffA, 0, 3) * r + cell.Of(coeffA, 1, 3) * g + cell.Of(coeffA, 2, 3) * b
                                        + cell.Of(coeffB, 0, 1), 0, 1);
                    // Foreground colour (blur fusion, Forte & Pitié 2021): blurred estimates plus the full-resolution detail
                    for (var c = 0; c < 3; c++)
                    {
                        var i = c == 0 ? r : c == 1 ? g : b;
                        float bf = cell.Of(fore, c, 3), bb = cell.Of(back, c, 3);
                        f[c] = Math.Clamp(bf + al * (i - al * bf - (1 - al) * bb), 0, 1);
                    }
                    float outR, outG, outB;
                    if (mode == BackgroundMode.Transparent)
                    {
                        (outR, outG, outB) = al > 0 ? (f[0], f[1], f[2]) : (r, g, b);
                        if (ai >= 0) area[o + ai] = ToShort(al * (area[o + ai] / 65535f));
                    }
                    else
                    {
                        outR = f[0] * al + cell.Of(blurred!, 0, 3) * (1 - al);
                        outG = f[1] * al + cell.Of(blurred!, 1, 3) * (1 - al);
                        outB = f[2] * al + cell.Of(blurred!, 2, 3) * (1 - al);
                    }
                    area[o + ri] = ToShort(outR);
                    area[o + gi] = ToShort(outG);
                    area[o + bi] = ToShort(outB);
                }
                rows[k] = area;
            });
            for (var k = 0; k < strips; k++)
            {
                int y0 = k * strip, y1 = Math.Min(imageH, y0 + strip);
                pixels.SetArea(0, y0, (uint)imageW, (uint)(y1 - y0), rows[k]);
            }
        }
        return result;
    }

    // ------------------------------------------------------------ Steps

    /// <summary>
    /// Foreground and background colours behind the soft edge (blur fusion, Forte &amp; Pitié 2021, two passes): returns
    /// the blurred estimates of the second pass, from which the colour of every full-resolution pixel follows.
    /// </summary>
    private static (float[] Fore, float[] Back) ForegroundEstimate(float[] image, float[] alpha, int w, int h)
    {
        var big = Math.Max(4, (int)Math.Round(90 * Math.Max(w, h) / 2048.0));
        var (fore1, back1) = BlurFusion(image, image, image, alpha, w, h, big);
        var f = new float[image.Length];
        for (var i = 0; i < alpha.Length; i++)
        for (var c = 0; c < 3; c++)
        {
            var j = i * 3 + c;
            var a = alpha[i];
            f[j] = Math.Clamp(fore1[j] + a * (image[j] - a * fore1[j] - (1 - a) * back1[j]), 0, 1);
        }
        return BlurFusion(image, f, back1, alpha, w, h, 6);
    }

    /// <summary>One pass of blur fusion: alpha-weighted box means of foreground and background colours.</summary>
    private static (float[] Fore, float[] Back) BlurFusion(float[] image, float[] fore, float[] back, float[] alpha, int w, int h, int r)
    {
        var n = w * h;
        var a = (float[])alpha.Clone();
        var oneMinus = new float[n];
        var fa = new float[n * 3];
        var ba = new float[n * 3];
        for (var i = 0; i < n; i++)
        {
            oneMinus[i] = 1 - alpha[i];
            for (var c = 0; c < 3; c++)
            {
                fa[i * 3 + c] = fore[i * 3 + c] * alpha[i];
                ba[i * 3 + c] = back[i * 3 + c] * (1 - alpha[i]);
            }
        }
        var sigma = r / 1.7;   // a box of radius r ≈ Gaussian with this sigma (Blur.Gauss uses box passes for larger ones)
        Blur.Gauss(a, w, h, sigma);
        Blur.Gauss(oneMinus, w, h, sigma);
        Blur.Gauss(fa, w, h, sigma, 3);
        Blur.Gauss(ba, w, h, sigma, 3);
        var outF = new float[n * 3];
        var outB = new float[n * 3];
        for (var i = 0; i < n; i++)
        for (var c = 0; c < 3; c++)
        {
            outF[i * 3 + c] = fa[i * 3 + c] / (a[i] + 1e-5f);
            outB[i * 3 + c] = ba[i * 3 + c] / (oneMinus[i] + 1e-5f);
        }
        return (outF, outB);
    }

    /// <summary>
    /// The background blurred without the subject bleeding into it: blur of colour × (1 − alpha) divided by the blur of
    /// (1 − alpha) (normalised convolution). Where only subject is near, the nearest background colour spreads in.
    /// </summary>
    private static float[] BlurredBackground(float[] image, float[] alpha, int w, int h, double amount)
    {
        var sigma = Math.Max(0.5, Math.Clamp(amount, 0, 1) * 0.02 * Math.Max(w, h));
        var n = w * h;
        var weight = new float[n];
        var color = new float[n * 3];
        for (var i = 0; i < n; i++)
        {
            var bg = 1 - alpha[i];
            weight[i] = bg;
            for (var c = 0; c < 3; c++) color[i * 3 + c] = image[i * 3 + c] * bg;
        }
        Blur.Gauss(weight, w, h, sigma);
        Blur.Gauss(color, w, h, sigma, 3);
        for (var i = 0; i < n; i++)
        for (var c = 0; c < 3; c++)
            color[i * 3 + c] = weight[i] > 1e-4f ? color[i * 3 + c] / weight[i] : image[i * 3 + c];
        return color;
    }

    // ------------------------------------------------------------ Objects

    /// <summary>
    /// Objects of a mask: connected areas with alpha over ½, extended to their soft edges (alpha over 0.02) so that hair
    /// belongs to its object. 0 = background; labels from 1.
    /// </summary>
    internal static int[] Components(float[] alpha, int w, int h)
    {
        var labels = new int[w * h];
        var queue = new Queue<int>();
        var next = 0;
        for (var start = 0; start < labels.Length; start++)
        {
            if (labels[start] != 0 || alpha[start] <= 0.5f) continue;
            labels[start] = ++next;
            queue.Enqueue(start);
            while (queue.Count > 0)
            {
                var p = queue.Dequeue();
                foreach (var q in Neighbours(p, w, h))
                    if (labels[q] == 0 && alpha[q] > 0.5f) { labels[q] = next; queue.Enqueue(q); }
            }
        }
        // Soft edges: grow every object into neighbouring pixels with some alpha (breadth first, nearest object wins)
        for (var p = 0; p < labels.Length; p++)
            if (labels[p] != 0) queue.Enqueue(p);
        while (queue.Count > 0)
        {
            var p = queue.Dequeue();
            foreach (var q in Neighbours(p, w, h))
                if (labels[q] == 0 && alpha[q] > 0.02f) { labels[q] = labels[p]; queue.Enqueue(q); }
        }
        return labels;
    }

    private static IEnumerable<int> Neighbours(int p, int w, int h)
    {
        int x = p % w, y = p / w;
        if (x > 0) yield return p - 1;
        if (x < w - 1) yield return p + 1;
        if (y > 0) yield return p - w;
        if (y < h - 1) yield return p + w;
    }

    /// <summary>Labels of the objects that a stroke raster touches.</summary>
    private static HashSet<int> Touched(int[] labels, byte[] raster)
    {
        var set = new HashSet<int>();
        for (var i = 0; i < labels.Length; i++)
            if (raster[i] != 0 && labels[i] != 0) set.Add(labels[i]);
        return set;
    }

    // ------------------------------------------------------------ Geometry

    /// <summary>Bounding box of the set pixels of a raster (working resolution); null if there are none.</summary>
    private static SourceRect? Bounds(byte[] raster, int w, int h)
    {
        int x0 = w, y0 = h, x1 = -1, y1 = -1;
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            if (raster[y * w + x] == 0) continue;
            if (x < x0) x0 = x;
            if (x > x1) x1 = x;
            if (y < y0) y0 = y;
            if (y > y1) y1 = y;
        }
        return x1 < 0 ? null : new SourceRect(x0, y0, x1 - x0 + 1, y1 - y0 + 1);
    }

    /// <summary>A box enlarged by half its size on every side (at least 64 pixels), within the mask.</summary>
    private static SourceRect Grow(SourceRect box, int w, int h)
    {
        int mx = Math.Max(64, box.Width / 2), my = Math.Max(64, box.Height / 2);
        int x0 = Math.Max(0, box.X - mx), y0 = Math.Max(0, box.Y - my);
        int x1 = Math.Min(w, box.X + box.Width + mx), y1 = Math.Min(h, box.Y + box.Height + my);
        return new SourceRect(x0, y0, x1 - x0, y1 - y0);
    }

    /// <summary>A box at working resolution as a rectangle of the pixel image.</summary>
    private static SourceRect ToPixels(SourceRect box, BackgroundMask mask, double scale)
    {
        int x0 = mask.Region.X + (int)Math.Floor(box.X / scale), y0 = mask.Region.Y + (int)Math.Floor(box.Y / scale);
        int x1 = Math.Min(mask.Region.X + mask.Region.Width, mask.Region.X + (int)Math.Ceiling((box.X + box.Width) / scale));
        int y1 = Math.Min(mask.Region.Y + mask.Region.Height, mask.Region.Y + (int)Math.Ceiling((box.Y + box.Height) / scale));
        return new SourceRect(x0, y0, Math.Max(1, x1 - x0), Math.Max(1, y1 - y0));
    }

    /// <summary>The alpha of a (zoomed) mask on the grid of another mask; 0 outside its region.</summary>
    private static float[] SampleInto(BackgroundMask from, BackgroundMask onto)
    {
        var result = new float[onto.Width * onto.Height];
        double scaleOnto = onto.Width / (double)onto.Region.Width, scaleFrom = from.Width / (double)from.Region.Width;
        Parallel.For(0, onto.Height, y =>
        {
            for (var x = 0; x < onto.Width; x++)
            {
                // pixel-image position of this grid cell, then into the grid of the zoomed mask
                var px = onto.Region.X + (x + 0.5) / scaleOnto;
                var py = onto.Region.Y + (y + 0.5) / scaleOnto;
                if (px < from.Region.X || py < from.Region.Y || px >= from.Region.X + from.Region.Width || py >= from.Region.Y + from.Region.Height)
                    continue;
                var cell = new Bilinear(from.Width, from.Height, (px - from.Region.X) * scaleFrom - 0.5, (py - from.Region.Y) * scaleFrom - 0.5);
                result[y * onto.Width + x] = cell.Of(from.Alpha, 0, 1);
            }
        });
        return result;
    }

    /// <summary>Bilinear resampling of one plane.</summary>
    private static float[] Resample(float[] src, int sw, int sh, int dw, int dh)
    {
        var dst = new float[dw * dh];
        Parallel.For(0, dh, y =>
        {
            for (var x = 0; x < dw; x++)
                dst[y * dw + x] = new Bilinear(sw, sh, (x + 0.5) * sw / dw - 0.5, (y + 0.5) * sh / dh - 0.5).Of(src, 0, 1);
        });
        return dst;
    }

    /// <summary>A region of the pixel image reduced to w × h, as R, G, B (interleaved or as planes), sRGB 0 … 1.</summary>
    private static float[] RegionRgb(MagickImage image, SourceRect region, int w, int h, bool interleaved)
    {
        using var crop = ImageOps.CloneOf(image);
        if (crop.ColorSpace == ColorSpace.CMYK) crop.ColorSpace = ColorSpace.sRGB;
        crop.Crop(new MagickGeometry(region.X, region.Y, (uint)region.Width, (uint)region.Height));
        crop.ResetPage();
        using var small = ImageOps.Resize(crop, w, h, ResampleFilter.Lanczos3, gammaCorrect: false);
        using var pixels = small.GetPixelsUnsafe();
        var data = pixels.ToShortArray(0, 0, (uint)w, (uint)h, "RGB")
                   ?? throw new InvalidOperationException(Tr.T("Pixel konnten nicht gelesen werden."));
        var result = new float[data.Length];
        var plane = w * h;
        for (var i = 0; i < plane; i++)
        for (var c = 0; c < 3; c++)
            result[interleaved ? i * 3 + c : c * plane + i] = data[i * 3 + c] / 65535f;
        return result;
    }

    private static ushort ToShort(float v) => (ushort)(v <= 0 ? 0 : v >= 1 ? 65535 : v * 65535f + 0.5f);

    /// <summary>Position on a grid with its four neighbours and weights (bilinear, clamped to the edge).</summary>
    private readonly struct Bilinear
    {
        private readonly int _i00, _i01, _i10, _i11;
        private readonly float _fx, _fy;

        public Bilinear(int w, int h, double x, double y)
        {
            x = Math.Clamp(x, 0, w - 1);
            y = Math.Clamp(y, 0, h - 1);
            int x0 = (int)x, y0 = (int)y;
            int x1 = Math.Min(x0 + 1, w - 1), y1 = Math.Min(y0 + 1, h - 1);
            (_fx, _fy) = ((float)(x - x0), (float)(y - y0));
            (_i00, _i01, _i10, _i11) = (y0 * w + x0, y0 * w + x1, y1 * w + x0, y1 * w + x1);
        }

        /// <summary>Channel <paramref name="c"/> of a plane with <paramref name="ch"/> channels.</summary>
        public float Of(float[] data, int c, int ch)
        {
            var top = data[_i00 * ch + c] * (1 - _fx) + data[_i01 * ch + c] * _fx;
            var bottom = data[_i10 * ch + c] * (1 - _fx) + data[_i11 * ch + c] * _fx;
            return top * (1 - _fy) + bottom * _fy;
        }
    }
}
