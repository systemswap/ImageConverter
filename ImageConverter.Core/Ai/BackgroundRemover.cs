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

    // Below this the result counts as invisible (one 8-bit level at most): alpha 0, colour from the visible pixels
    private const float Invisible = 1f / 255;

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
        CheckMask(raw, model.Size);
        return new BackgroundMask(region, w, h, Resample(raw, model.Size, model.Size, w, h), guide);
    }

    /// <summary>
    /// The model must return one finite value per pixel. A mask of another size failed deep in the refinement with dozens
    /// of index errors, and NaN made everything silently transparent (No. 87).
    /// </summary>
    /// <exception cref="InvalidDataException">The mask has the wrong size or contains invalid values.</exception>
    private static void CheckMask(float[] raw, int size)
    {
        if (raw.Length != size * size)
            throw new InvalidDataException(Tr.T("Das Modell lieferte {0} statt {1} Werte – passt die Modelldatei?", raw.Length, size * size));
        foreach (var v in raw)
            if (!float.IsFinite(v))
                throw new InvalidDataException(Tr.T("Das Modell lieferte ungültige Werte – bitte noch einmal oder mit einem anderen Modell versuchen."));
    }

    /// <summary>
    /// Brings greyscale and CMYK images to RGB: the steps read and write red, green and blue. Grey values stay exactly as
    /// they are (R = G = B); black-and-white photos and scans load as greyscale (No. 87).
    /// </summary>
    private static void ToRgb(MagickImage image)
    {
        if (image.ColorSpace is ColorSpace.CMYK or ColorSpace.Gray or ColorSpace.LinearGray) image.ColorSpace = ColorSpace.sRGB;
    }

    /// <summary>
    /// The same mask for another image of the same size (the image without an earlier step that was switched off): the
    /// subject probabilities stay, the guide for the edges and colours comes from the new image (No. 102).
    /// </summary>
    /// <param name="mask">Mask computed on another image; its guide is not used.</param>
    /// <param name="image">Image of the same size as the one the mask was computed on.</param>
    /// <returns>The mask with the guide of <paramref name="image"/>.</returns>
    public static BackgroundMask Rebase(BackgroundMask mask, MagickImage image) =>
        mask with { Guide = RegionRgb(image, mask.Region, mask.Width, mask.Height, interleaved: true) };

    /// <summary>
    /// Applies the brush strokes to the detected mask: red removes the objects it touches, green keeps them and adds what a
    /// zoomed run finds around the green strokes.
    /// </summary>
    /// <param name="mask">Detected mask.</param>
    /// <param name="strokes">Strokes in pixel-image coordinates; <see cref="BrushStroke.Keep"/> = green.</param>
    /// <param name="zoomDetect">Runs the model on a part of the image (pixel-image rectangle); null = no zoomed run.</param>
    /// <returns>The edited mask (a new array).</returns>
    public static float[] Edit(BackgroundMask mask, IReadOnlyList<BrushStroke> strokes, Func<SourceRect, BackgroundMask>? zoomDetect) =>
        Edit(mask, strokes, zoomDetect, out _);

    /// <summary>Like <see cref="Edit(BackgroundMask, IReadOnlyList{BrushStroke}, Func{SourceRect, BackgroundMask}?)"/>, and reports
    /// how much green keeps of each pixel.</summary>
    /// <param name="mask">Detected mask.</param>
    /// <param name="strokes">Strokes in pixel-image coordinates.</param>
    /// <param name="zoomDetect">Runs the model on a part of the image; null = no zoomed run.</param>
    /// <param name="kept">Strength of the green strokes per pixel of the mask (0 … 1); null without green. <see cref="Compose"/>
    /// takes the original colours there, so that a stroke at 40 % keeps 40 % of the photo itself.</param>
    /// <returns>The edited mask (a new array).</returns>
    public static float[] Edit(BackgroundMask mask, IReadOnlyList<BrushStroke> strokes, Func<SourceRect, BackgroundMask>? zoomDetect,
        out float[]? kept)
    {
        kept = null;
        var alpha = (float[])mask.Alpha.Clone();
        if (strokes.Count == 0) return alpha;
        int w = mask.Width, h = mask.Height;
        var scale = w / (double)mask.Region.Width;
        BrushStroke ToWork(BrushStroke st) => st with
        {
            Points = st.Points.Select(p => ((p.X - mask.Region.X) * scale, (p.Y - mask.Region.Y) * scale)).ToArray(),
            Radius = Math.Max(1, st.Radius * scale),
        };
        var keepStrokes = strokes.Where(st => st.Keep).Select(ToWork).ToArray();
        var green = Inpainter.Rasterize(keepStrokes, w, h, 0);
        // Only strokes at full strength add whole objects: a weak stroke over a railing should not keep the whole object hard
        var whole = keepStrokes.All(st => st.Strength >= 1) ? green : Inpainter.Rasterize(keepStrokes.Where(st => st.Strength >= 1).ToArray(), w, h, 0);
        var redStrokes = strokes.Where(st => !st.Keep).Select(ToWork).ToArray();
        var red = Inpainter.Rasterize(redStrokes, w, h, 0);
        // Soft red strokes: what lies under them fades out towards their edge instead of ending hard (whole objects still go)
        var redSoft = redStrokes.Any(st => st.Hardness < 1) ? Coverage(redStrokes, w, h, strength: false) : null;

        // Green first: objects found by a zoomed run around the green strokes (the first run missed them)
        if (zoomDetect is not null && Bounds(whole, w, h) is { } box)
        {
            var zoom = zoomDetect(ToPixels(Grow(box, w, h), mask, scale));
            var zoomAlpha = SampleInto(zoom, mask);
            var zoomLabels = Components(zoomAlpha, w, h);
            var picked = Touched(zoomLabels, whole);
            for (var i = 0; i < alpha.Length; i++)
                if (zoomLabels[i] > 0 && picked.Contains(zoomLabels[i])) alpha[i] = Math.Max(alpha[i], zoomAlpha[i]);
        }
        // What green covers stays in any case, even where the model sees no subject, not even zoomed in: otherwise the marked
        // part was blurred or removed anyway (No. 104). A weaker stroke keeps it at its strength (a railing 40 % sharp). The
        // edges still come from the photo (guided filter in Compose).
        var level = keepStrokes.Length > 0 ? new float[alpha.Length] : null;
        var hard = keepStrokes.Where(st => st.Hardness >= 1).ToArray();
        foreach (var group in hard.GroupBy(st => Math.Clamp(st.Strength, 0, 1)))
        {
            var strength = (float)group.Key;
            var covered = group.Count() == keepStrokes.Length ? green : Inpainter.Rasterize(group.ToArray(), w, h, 0);
            for (var i = 0; i < alpha.Length; i++)
                if (covered[i] != 0 && level![i] < strength) level[i] = strength;
        }
        // Soft strokes: the strength falls off towards their edge (docs/KI-HINTERGRUND.md, „Bedienung“)
        if (hard.Length < keepStrokes.Length)
        {
            var soft = Coverage(keepStrokes.Where(st => st.Hardness < 1).ToArray(), w, h, strength: true);
            for (var i = 0; i < alpha.Length; i++)
                if (level![i] < soft[i]) level[i] = soft[i];
        }
        if (level is not null)
            for (var i = 0; i < alpha.Length; i++)
                if (alpha[i] < level[i]) alpha[i] = level[i];
        kept = level;

        // Then red: whole objects it touches, unless green touches them too, and leftovers under the stroke
        var labels = Components(alpha, w, h);
        var keep = Touched(labels, green);
        var drop = Touched(labels, red);
        drop.ExceptWith(keep);
        for (var i = 0; i < alpha.Length; i++)
        {
            var label = labels[i];
            if (label > 0 && drop.Contains(label)) alpha[i] = 0;
            else if (red[i] != 0 && (label == 0 || !keep.Contains(label))) alpha[i] = redSoft is null ? 0 : alpha[i] * (1 - redSoft[i]);
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
    /// <param name="kept">Strength of green strokes per pixel of the mask (from <see cref="Edit(BackgroundMask, IReadOnlyList{BrushStroke}, Func{SourceRect, BackgroundMask}?, out float[])"/>);
    /// there the original colours are taken instead of the estimated foreground colours: the mix is the user's, not a
    /// soft edge such as hair, and a stroke at 40 % should keep 40 % of the photo. null = none.</param>
    /// <returns>A new image (with alpha channel when transparent); full bit depth, profiles kept.</returns>
    public static MagickImage Compose(MagickImage image, BackgroundMask mask, float[] alpha, BackgroundMode mode, double blur,
        CancellationToken ct, float[]? kept = null)
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
        var fill = mode == BackgroundMode.Transparent ? FillFromVisible(fore, refined, w, h) : null;

        var result = ImageOps.CloneOf(image);
        ToRgb(result);
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
                        if (mode == BackgroundMode.Transparent && ai >= 0)
                        {
                            // outside the crop: background
                            var nearest = new Bilinear(w, h, (x - region.X + 0.5) * scale - 0.5, (y - region.Y + 0.5) * scale - 0.5);
                            area[o + ai] = 0;
                            area[o + ri] = ToShort(nearest.Of(fill!, 0, 3));
                            area[o + gi] = ToShort(nearest.Of(fill!, 1, 3));
                            area[o + bi] = ToShort(nearest.Of(fill!, 2, 3));
                        }
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
                    if (kept is not null && cell.Of(kept, 0, 1) is var keptHere and > 0)
                    {
                        var own = Math.Clamp(keptHere / Math.Max(al, 1e-3f), 0, 1);   // share of the mask that green set
                        f[0] += own * (r - f[0]);
                        f[1] += own * (g - f[1]);
                        f[2] += own * (b - f[2]);
                    }
                    float outR, outG, outB;
                    if (mode == BackgroundMode.Transparent)
                    {
                        var visible = ai >= 0 ? al * (area[o + ai] / 65535f) : al;
                        // Invisible pixels must not keep the old background: it would stay in the saved file and every
                        // filter after this step would compute from it
                        (outR, outG, outB) = visible >= Invisible ? (f[0], f[1], f[2]) : (cell.Of(fill!, 0, 3), cell.Of(fill!, 1, 3), cell.Of(fill!, 2, 3));
                        if (ai >= 0) area[o + ai] = visible >= Invisible ? ToShort(visible) : (ushort)0;
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
    /// Colours for the invisible pixels, continued from the visible ones only (pull-push: averaged down to the coarsest
    /// level weighted by alpha, then filled in from coarse to fine). Smooth, so that filters working on neighbours find no
    /// artificial edge around the subject.
    /// </summary>
    /// <param name="colour">Foreground colours, interleaved R, G, B.</param>
    /// <param name="alpha">Mask 0 … 1.</param>
    /// <param name="w">Width.</param>
    /// <param name="h">Height.</param>
    /// <returns>Colours for every pixel, interleaved R, G, B; black if nothing is visible.</returns>
    internal static float[] FillFromVisible(float[] colour, float[] alpha, int w, int h)
    {
        var colours = new List<float[]> { (float[])colour.Clone() };
        var weights = new List<float[]> { alpha.Select(a => a >= Invisible ? a : 0).ToArray() };
        var sizes = new List<(int W, int H)> { (w, h) };
        while (sizes[^1] is not (1, 1))
        {
            var (fw, fh) = sizes[^1];
            int cw = (fw + 1) / 2, ch = (fh + 1) / 2;
            var (fineC, fineW) = (colours[^1], weights[^1]);
            var coarseC = new float[cw * ch * 3];
            var coarseW = new float[cw * ch];
            Parallel.For(0, ch, y =>
            {
                for (var x = 0; x < cw; x++)
                {
                    float sum = 0, r = 0, g = 0, b = 0;
                    for (var fy = y * 2; fy < Math.Min(fh, y * 2 + 2); fy++)
                    for (var fx = x * 2; fx < Math.Min(fw, x * 2 + 2); fx++)
                    {
                        var i = fy * fw + fx;
                        var a = fineW[i];
                        sum += a;
                        r += fineC[i * 3] * a;
                        g += fineC[i * 3 + 1] * a;
                        b += fineC[i * 3 + 2] * a;
                    }
                    if (sum <= 0) continue;
                    var j = y * cw + x;
                    (coarseC[j * 3], coarseC[j * 3 + 1], coarseC[j * 3 + 2]) = (r / sum, g / sum, b / sum);
                    coarseW[j] = Math.Min(1, sum);
                }
            });
            colours.Add(coarseC);
            weights.Add(coarseW);
            sizes.Add((cw, ch));
        }
        for (var level = colours.Count - 2; level >= 0; level--)
        {
            var (fw, fh) = sizes[level];
            var (cw, ch) = sizes[level + 1];
            var (fineC, fineW, coarseC) = (colours[level], weights[level], colours[level + 1]);
            Parallel.For(0, fh, y =>
            {
                for (var x = 0; x < fw; x++)
                {
                    var i = y * fw + x;
                    var a = fineW[i];
                    if (a >= 1) continue;
                    var cell = new Bilinear(cw, ch, (x + 0.5) / 2 - 0.5, (y + 0.5) / 2 - 0.5);
                    for (var c = 0; c < 3; c++)
                        fineC[i * 3 + c] = fineC[i * 3 + c] * a + cell.Of(coarseC, c, 3) * (1 - a);
                }
            });
        }
        return colours[0];
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

    /// <summary>
    /// How much strokes cover each pixel, with soft edges: full up to <see cref="BrushStroke.Hardness"/> × radius, then falling
    /// off smoothly (smoothstep) to 0 at the radius; overlapping strokes take the maximum.
    /// </summary>
    /// <param name="strokes">Strokes at working resolution.</param>
    /// <param name="w">Width of the grid.</param>
    /// <param name="h">Height of the grid.</param>
    /// <param name="strength">Multiply by <see cref="BrushStroke.Strength"/> (green); false = 1 (red).</param>
    /// <returns>Coverage 0…1 per pixel.</returns>
    internal static float[] Coverage(IReadOnlyList<BrushStroke> strokes, int w, int h, bool strength)
    {
        var cover = new float[w * h];
        foreach (var st in strokes)
        {
            if (st.Points.Count == 0) continue;
            var r = Math.Max(0.5, st.Radius);
            var inner = r * Math.Clamp(st.Hardness, 0, 1);
            var top = strength ? (float)Math.Clamp(st.Strength, 0, 1) : 1f;
            var pts = st.Points;
            int x0 = Math.Max(0, (int)Math.Floor(pts.Min(p => p.X) - r)), x1 = Math.Min(w - 1, (int)Math.Ceiling(pts.Max(p => p.X) + r));
            int y0 = Math.Max(0, (int)Math.Floor(pts.Min(p => p.Y) - r)), y1 = Math.Min(h - 1, (int)Math.Ceiling(pts.Max(p => p.Y) + r));
            Parallel.For(y0, y1 + 1, y =>
            {
                for (var x = x0; x <= x1; x++)
                {
                    double px = x + 0.5, py = y + 0.5, best = double.MaxValue;
                    for (var k = 0; k < pts.Count; k++)
                    {
                        var (ax, ay) = pts[k];
                        var (bx, by) = k + 1 < pts.Count ? pts[k + 1] : pts[k];
                        double dx = bx - ax, dy = by - ay, len = dx * dx + dy * dy;
                        var t = len > 0 ? Math.Clamp(((px - ax) * dx + (py - ay) * dy) / len, 0, 1) : 0;
                        double ex = px - (ax + t * dx), ey = py - (ay + t * dy);
                        best = Math.Min(best, ex * ex + ey * ey);
                    }
                    var d = Math.Sqrt(best);
                    if (d >= r) continue;
                    var v = d <= inner ? 1.0 : 1 - (d - inner) / (r - inner);
                    var value = top * (float)(v * v * (3 - 2 * v));   // smoothstep: no visible ring at the inner radius
                    var i = y * w + x;
                    if (cover[i] < value) cover[i] = value;
                }
            });
        }
        return cover;
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
        ToRgb(crop);
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
