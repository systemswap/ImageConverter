using ImageConverter.Core.Imaging;
using ImageMagick;
using Microsoft.ML.OnnxRuntime;
using ImageConverter.Core.Platform;

namespace ImageConverter.Core.Ai;

/// <summary>A face restoration model: aligned face crop in, restored face crop of the same size out.</summary>
public interface IFaceModel
{
    /// <summary>Gets the edge length of the square crop in pixels.</summary>
    int Size { get; }

    /// <summary>Runs the model on one aligned crop.</summary>
    /// <param name="input">Planar RGB, 3 × <see cref="Size"/> × <see cref="Size"/>, values −1…1.</param>
    /// <param name="ct">Cancels the computation.</param>
    /// <returns>Planar RGB in the same layout, values nominally −1…1.</returns>
    float[] Run(float[] input, CancellationToken ct);
}

/// <summary>A detected face with its bounding box and five landmarks (eyes, nose, mouth corners).</summary>
/// <param name="X1">Left edge of the bounding box in source image pixels.</param>
/// <param name="Y1">Top edge of the bounding box in source image pixels.</param>
/// <param name="X2">Right edge of the bounding box in source image pixels.</param>
/// <param name="Y2">Bottom edge of the bounding box in source image pixels.</param>
/// <param name="Score">Detection confidence, 0…1 (class score × objectness).</param>
/// <param name="Landmarks">
/// Five points in source image pixels, in the order YuNet reports them: eye, eye, nose, mouth corner,
/// mouth corner (each pair from left to right in the image).
/// </param>
public sealed record DetectedFace(double X1, double Y1, double X2, double Y2, double Score, (double X, double Y)[] Landmarks)
{
    /// <summary>Gets the width of the bounding box in pixels.</summary>
    public double Width => X2 - X1;

    /// <summary>Gets the height of the bounding box in pixels.</summary>
    public double Height => Y2 - Y1;
}

/// <summary>
/// A 2-D similarity transform (rotation, uniform scaling, translation):
/// x' = a·x − b·y + tx,  y' = b·x + a·y + ty.
/// </summary>
/// <param name="A">Scale × cos(angle).</param>
/// <param name="B">Scale × sin(angle).</param>
/// <param name="Tx">Horizontal translation.</param>
/// <param name="Ty">Vertical translation.</param>
public readonly record struct Similarity(double A, double B, double Tx, double Ty)
{
    /// <summary>Maps a point.</summary>
    /// <param name="x">X coordinate of the point.</param>
    /// <param name="y">Y coordinate of the point.</param>
    /// <returns>The transformed point.</returns>
    public (double X, double Y) Apply(double x, double y) => (A * x - B * y + Tx, B * x + A * y + Ty);

    /// <summary>Gets the uniform scale factor of the transform.</summary>
    public double ScaleFactor => Math.Sqrt(A * A + B * B);

    /// <summary>Returns the inverse transform.</summary>
    /// <returns>The transform that maps the results of <see cref="Apply"/> back to the original points.</returns>
    /// <remarks>Undefined (division by zero) when the scale factor is 0.</remarks>
    public Similarity Inverse()
    {
        var d = A * A + B * B;
        double ia = A / d, ib = -B / d;
        return new Similarity(ia, ib, -(ia * Tx - ib * Ty), -(ib * Tx + ia * Ty));
    }

    /// <summary>Estimates the similarity transform that best maps <paramref name="from"/> onto <paramref name="to"/> (least squares).</summary>
    /// <param name="from">Source points.</param>
    /// <param name="to">Target points; same count and order as <paramref name="from"/>.</param>
    /// <returns>The least-squares transform.</returns>
    public static Similarity Estimate(IReadOnlyList<(double X, double Y)> from, IReadOnlyList<(double X, double Y)> to)
    {
        var n = from.Count;
        double fx = from.Average(p => p.X), fy = from.Average(p => p.Y);
        double tx = to.Average(p => p.X), ty = to.Average(p => p.Y);

        double num1 = 0, num2 = 0, den = 0;
        for (var i = 0; i < n; i++)
        {
            double sx = from[i].X - fx, sy = from[i].Y - fy;
            double dx = to[i].X - tx, dy = to[i].Y - ty;
            num1 += sx * dx + sy * dy;
            num2 += sx * dy - sy * dx;
            den += sx * sx + sy * sy;
        }

        double a = num1 / den, b = num2 / den;
        return new Similarity(a, b, tx - (a * fx - b * fy), ty - (b * fx + a * fy));
    }
}

/// <summary>
/// Face restoration: detects faces (YuNet), aligns each to the reference position of the model,
/// restores it and pastes it back with a soft edge. The rest of the image is left untouched.
/// </summary>
public static class AiFaces
{
    /// <summary>
    /// Reference positions of the five landmarks as fractions of the crop edge (FFHQ alignment of the
    /// 512-px crop, as used for training).
    /// </summary>
    internal static readonly (double X, double Y)[] Template =
    [
        (0.37691676, 0.46864664), (0.62285697, 0.46912813), (0.50123859, 0.61331904),
        (0.39308822, 0.72541100), (0.61150205, 0.72490465),
    ];

    /// <summary>
    /// Minimum face width in source pixels; narrower faces are skipped because they contain too little
    /// detail to restore.
    /// </summary>
    public const int MinFaceSize = 16;

    // ------------------------------------------------------------ Detection

    /// <summary>Finds faces in an image with the YuNet detector.</summary>
    /// <param name="source">Image to search; it is not modified.</param>
    /// <param name="detector">ONNX session of the YuNet model (640 × 640 input).</param>
    /// <param name="threshold">Minimum confidence (class score × objectness), 0…1.</param>
    /// <returns>
    /// The detected faces in source image coordinates, overlapping duplicates removed (IoU above 0.4),
    /// faces narrower than <see cref="MinFaceSize"/> omitted, ordered by descending confidence.
    /// </returns>
    /// <remarks>
    /// The image is reduced to fit into 640 × 640 (aspect ratio kept, never enlarged), so very small faces
    /// in large images can be missed.
    /// </remarks>
    public static List<DetectedFace> Detect(MagickImage source, InferenceSession detector, double threshold = 0.5)
    {
        const int size = 640;
        byte[] bgr;
        int rw, rh;
        using (var small = ImageOps.CloneOf(source))
        {
            if (small.Width > size || small.Height > size)
                small.Resize(new MagickGeometry(size, size));   // aspect ratio is kept
            rw = (int)small.Width;
            rh = (int)small.Height;
            using var pixels = small.GetPixelsUnsafe();
            bgr = pixels.ToByteArray("BGR") ?? throw new InvalidOperationException(Tr.T("Pixel konnten nicht gelesen werden."));
        }

        // Place at the top left, rest black; values 0…255 in BGR order (as YuNet was trained)
        var input = new float[3 * size * size];
        for (var y = 0; y < rh; y++)
        for (var x = 0; x < rw; x++)
        {
            var si = (y * rw + x) * 3;
            var di = y * size + x;
            input[di] = bgr[si];
            input[size * size + di] = bgr[si + 1];
            input[2 * size * size + di] = bgr[si + 2];
        }

        using var value = OrtValue.CreateTensorValueFromMemory(input, [1, 3, size, size]);
        var outputs = OnnxSessions.RunAll(detector, detector.InputNames[0], value);
        var ratio = source.Width / (double)rw;
        return Nms(Decode(outputs, size, threshold, ratio), 0.4)
            .Where(f => f.Width >= MinFaceSize)
            .ToList();
    }

    /// <summary>
    /// Decodes the YuNet outputs: for each stride (8, 16, 32 px) class score, objectness, box and five points.
    /// Each grid cell is one anchor; values are relative to the cell.
    /// </summary>
    internal static List<DetectedFace> Decode(float[][] outputs, int size, double threshold, double ratio)
    {
        var faces = new List<DetectedFace>();
        int[] strides = [8, 16, 32];
        for (var level = 0; level < strides.Length; level++)
        {
            var stride = strides[level];
            var cols = size / stride;
            float[] cls = outputs[level], obj = outputs[level + 3], box = outputs[level + 6], kps = outputs[level + 9];

            for (var i = 0; i < cls.Length; i++)
            {
                var score = cls[i] * obj[i];
                if (score < threshold) continue;

                double ax = i % cols * stride, ay = i / cols * stride;
                double cx = box[i * 4] * stride + ax, cy = box[i * 4 + 1] * stride + ay;
                double bw = Math.Exp(box[i * 4 + 2]) * stride, bh = Math.Exp(box[i * 4 + 3]) * stride;

                var landmarks = new (double X, double Y)[5];
                for (var k = 0; k < 5; k++)
                    landmarks[k] = ((kps[i * 10 + 2 * k] * stride + ax) * ratio, (kps[i * 10 + 2 * k + 1] * stride + ay) * ratio);

                faces.Add(new DetectedFace((cx - bw / 2) * ratio, (cy - bh / 2) * ratio,
                    (cx + bw / 2) * ratio, (cy + bh / 2) * ratio, score, landmarks));
            }
        }

        return faces;
    }

    /// <summary>Non-maximum suppression: of overlapping detections of the same face, the most confident one is kept.</summary>
    internal static List<DetectedFace> Nms(List<DetectedFace> faces, double maxOverlap)
    {
        var kept = new List<DetectedFace>();
        foreach (var f in faces.OrderByDescending(f => f.Score))
            if (kept.All(k => IoU(k, f) <= maxOverlap))
                kept.Add(f);
        return kept;
    }

    private static double IoU(DetectedFace a, DetectedFace b)
    {
        var w = Math.Min(a.X2, b.X2) - Math.Max(a.X1, b.X1);
        var h = Math.Min(a.Y2, b.Y2) - Math.Max(a.Y1, b.Y1);
        if (w <= 0 || h <= 0) return 0;
        var inter = w * h;
        return inter / (a.Width * a.Height + b.Width * b.Height - inter);
    }

    // ------------------------------------------------------------ Restoration

    /// <summary>Restores faces and blends them into the image with <paramref name="strength"/> (without the detail controls).</summary>
    /// <param name="source">Image to restore; it is not modified.</param>
    /// <param name="faces">Faces to restore, typically from <see cref="Detect"/>.</param>
    /// <param name="model">Face restoration model.</param>
    /// <param name="strength">0…1: opacity over the original.</param>
    /// <param name="progress">Receives the completed fraction, 0…1, after each face.</param>
    /// <param name="ct">Cancels the computation.</param>
    /// <param name="preview">Receives intermediate frames at most every 400 ms; none after the last face.</param>
    /// <param name="previewMaxEdge">Longest edge of the intermediate frames in pixels.</param>
    /// <returns>A new image owned by the caller, with the profiles and density of <paramref name="source"/>.</returns>
    public static MagickImage Restore(MagickImage source, IReadOnlyList<DetectedFace> faces, IFaceModel model,
        double strength, IProgress<double>? progress, CancellationToken ct,
        Action<AiPreviewFrame>? preview = null, int previewMaxEdge = 2048) =>
        Run(source, faces, model, null, progress, ct, preview, previewMaxEdge).Compose(new AiBlend(strength));

    /// <summary>
    /// Restores faces at full strength without blending them into the image.
    /// </summary>
    /// <remarks>
    /// The returned <see cref="FaceRun"/> blends strength, skin texture, folds and grain afterwards as
    /// often as needed without running the model again. Before the model runs, the source image is
    /// analysed (face regions, grain profile); afterwards the result is compared with it to find the
    /// folds and lines the model smoothed away. The faces are processed one after another on the
    /// calling thread.
    /// </remarks>
    /// <param name="source">Image to restore; it is not modified.</param>
    /// <param name="faces">Faces to restore, typically from <see cref="Detect"/>.</param>
    /// <param name="model">Face restoration model.</param>
    /// <param name="parser">Face parsing model for the face regions; if null, soft circles around eyes and mouth are used instead.</param>
    /// <param name="progress">Receives the completed fraction, 0…1, after each face.</param>
    /// <param name="ct">Cancels the computation; checked before each face and passed to the models.</param>
    /// <param name="preview">Receives intermediate frames at most every 400 ms; none after the last face.</param>
    /// <param name="previewMaxEdge">Longest edge of the intermediate frames in pixels.</param>
    /// <returns>The unblended result at the size of <paramref name="source"/>.</returns>
    public static FaceRun Run(MagickImage source, IReadOnlyList<DetectedFace> faces, IFaceModel model, IFaceParser? parser,
        IProgress<double>? progress, CancellationToken ct,
        Action<AiPreviewFrame>? preview = null, int previewMaxEdge = 2048)
    {
        int w = (int)source.Width, h = (int)source.Height;
        var hasAlpha = source.HasAlpha;
        var channels = hasAlpha ? 4 : 3;
        var mapping = hasAlpha ? "RGBA" : "RGB";

        var pixels = AiEnhancer.ReadPixels(source, mapping);
        var image = new float[(long)w * h * 3];
        for (long i = 0, j = 0; i < image.Length; i += 3, j += channels)
        {
            image[i] = pixels[j] / 65535f;
            image[i + 1] = pixels[j + 1] / 65535f;
            image[i + 2] = pixels[j + 2] / 65535f;
        }

        // Before the model: measure the grain of the photo
        var grain = FaceDetails.MeasureGrain(image, w, h);

        var sink = new AiPreviewSink(preview, previewMaxEdge);
        var size = model.Size;
        var target = Template.Select(p => (p.X * size, p.Y * size)).ToArray();
        var done = new List<FaceRun.Face>();
        float[]? shown = null;   // intermediate image for the preview frames
        var pasted = 0;
        for (var f = 0; f < faces.Count; f++)
        {
            ct.ThrowIfCancellationRequested();
            var toCrop = Similarity.Estimate(faces[f].Landmarks, target);
            var crop = WarpToCrop(image, w, h, toCrop, size);

            // Before the model: face regions (skin, eyes, mouth …) in the original
            var labels = parser?.Parse(crop, size, ct);
            var foldMask = FaceDetails.FoldMask(labels, size, Template, FaceDetails.FoldsKeepAway(toCrop.ScaleFactor));

            var enhanced = FromModelRange(model.Run(ToModelRange(crop), ct));

            // After the model: which folds and lines did the model smooth away?
            var folds = FaceDetails.Folds(crop, enhanced, size, toCrop.ScaleFactor, foldMask);
            var face = new FaceRun.Face(crop, enhanced, Smoothed(enhanced, size, toCrop), toCrop, size, folds, foldMask,
                FaceDetails.KeptMask(labels, size, Template));
            face = face with { GrainGain = MeasureGrainGain(image, w, h, face, grain) };   // grain the restored face already has
            done.Add(face);
            progress?.Report((f + 1) / (double)faces.Count);

            if (sink.IsDue && f < faces.Count - 1)
            {
                shown ??= (float[])image.Clone();
                for (; pasted < done.Count; pasted++) PasteBack(shown, w, h, done[pasted], null, done[pasted].ToCrop, new AiBlend(), null);
                sink.Emit(w, h, (x, y, rgba) =>
                {
                    var i = ((long)y * w + x) * 3;
                    rgba[0] = shown[i];
                    rgba[1] = shown[i + 1];
                    rgba[2] = shown[i + 2];
                    rgba[3] = hasAlpha ? pixels[((long)y * w + x) * channels + 3] / 65535f : 1f;
                });
            }
        }

        return new FaceRun(source, image, pixels, w, h, mapping, channels, done, grain);
    }

    /// <summary>
    /// Coarse version of the restored face; the difference to it is the fine structure. Smoothed over about
    /// one image pixel, i.e. as many crop pixels as one image pixel covers in the crop.
    /// </summary>
    private static float[] Smoothed(float[] enhanced, int size, Similarity toCrop)
    {
        var sigma = Math.Clamp(toCrop.ScaleFactor * FineDetailPixels, 0.6, 6);
        var low = (float[])enhanced.Clone();
        var plane = size * size;
        var one = new float[plane];
        for (var c = 0; c < 3; c++)
        {
            Array.Copy(low, c * plane, one, 0, plane);
            Blur.Gauss(one, size, size, sigma);
            Array.Copy(one, 0, low, c * plane, plane);
        }
        return low;
    }

    /// <summary>Scale of what counts as fine structure: differences over this many image pixels (Gaussian sigma).</summary>
    internal const double FineDetailPixels = 1.0;

    /// <summary>Fine structure of the image: the image minus its blur over <see cref="FineDetailPixels"/> (interleaved RGB).</summary>
    internal static float[] HighPass(float[] image, int w, int h)
    {
        var blurred = (float[])image.Clone();
        Blur.Gauss(blurred, w, h, FineDetailPixels, channels: 3);
        for (long i = 0; i < blurred.Length; i++) blurred[i] = image[i] - blurred[i];
        return blurred;
    }

    /// <summary>
    /// Crop in the reference position, planar RGB 0…1. If the face in the image is larger than the crop,
    /// each crop pixel averages several samples (up to 4 × 4); otherwise jagged edges and aliasing appear.
    /// </summary>
    internal static float[] WarpToCrop(float[] image, int w, int h, Similarity toCrop, int size)
    {
        var back = toCrop.Inverse();
        var samples = Math.Clamp((int)Math.Ceiling(back.ScaleFactor), 1, 4);
        var crop = new float[3 * size * size];
        var plane = size * size;

        Parallel.For(0, size, ComputeLimits.Options, v =>
        {
            Span<float> rgb = stackalloc float[3];
            for (var u = 0; u < size; u++)
            {
                float r = 0, g = 0, b = 0;
                for (var sy = 0; sy < samples; sy++)
                for (var sx = 0; sx < samples; sx++)
                {
                    var (x, y) = back.Apply(u + (sx + 0.5) / samples - 0.5, v + (sy + 0.5) / samples - 0.5);
                    Sample(image, w, h, x, y, rgb);
                    r += rgb[0]; g += rgb[1]; b += rgb[2];
                }

                var n = samples * samples;
                var i = v * size + u;
                crop[i] = r / n;
                crop[plane + i] = g / n;
                crop[2 * plane + i] = b / n;
            }
        });

        return crop;
    }

    /// <summary>Image area a face touches when it is pasted back (image coordinates, inclusive).</summary>
    internal static (int X0, int Y0, int X1, int Y1) Bounds(Similarity toCrop, int size, int w, int h)
    {
        var back = toCrop.Inverse();
        var corners = new[] { back.Apply(0, 0), back.Apply(size - 1, 0), back.Apply(0, size - 1), back.Apply(size - 1, size - 1) };
        return (Math.Max(0, (int)Math.Floor(corners.Min(c => c.X))), Math.Max(0, (int)Math.Floor(corners.Min(c => c.Y))),
            Math.Min(w - 1, (int)Math.Ceiling(corners.Max(c => c.X))), Math.Min(h - 1, (int)Math.Ceiling(corners.Max(c => c.Y))));
    }

    /// <summary>
    /// Soft edge for pasting back: 0 up to 10 px from the crop edge, full weight from 66 px (for a 512-px crop,
    /// scaled for other sizes), smoothstep in between.
    /// </summary>
    internal static float EdgeWeight(double u, double v, int size)
    {
        var d = Math.Min(Math.Min(u, v), Math.Min(size - 1 - u, size - 1 - v)) / (size / 512.0);
        var t = Math.Clamp((d - 10) / 56, 0, 1);
        return (float)(t * t * (3 - 2 * t));
    }

    /// <summary>Grain for one face in the coordinates of the image it is pasted into.</summary>
    /// <param name="Field">Grain with standard deviation 1 (reduced in size for the preview), row by row from <paramref name="X0"/>, <paramref name="Y0"/>.</param>
    internal sealed record GrainPatch(float[] Field, int X0, int Y0, int Width, int Height, GrainProfile Profile);

    /// <summary>
    /// Pastes a restored face back into the image (in place), fading out towards the edge of the crop.
    /// For faces larger than the crop, the fine details of the original that the crop cannot contain
    /// are preserved.
    /// </summary>
    /// <param name="highPass">Fine structure of the image before the model (<see cref="HighPass"/>, same size as
    /// <paramref name="image"/>); needed only for texture &gt; 0.</param>
    /// <param name="toCrop">Image → crop; for the preview converted to its scale.</param>
    /// <param name="blend">Strength (opacity); texture: fine structure from the image before the model instead of the
    /// model's own, against the "wax face" look; folds: what the model smoothed away, restored on the skin; grain.</param>
    /// <param name="grain">Grain in these coordinates; needed only for grain &gt; 0.</param>
    internal static void PasteBack(float[] image, int w, int h, FaceRun.Face face, float[]? highPass,
        Similarity toCrop, AiBlend blend, GrainPatch? grain)
    {
        var size = face.Size;
        var (x0, y0, x1, y1) = Bounds(toCrop, size, w, h);
        var strength = (float)Math.Clamp(blend.Strength, 0, 1);
        if (x1 < x0 || y1 < y0 || strength <= 0) return;

        var back = toCrop.Inverse();
        // Share of original detail beyond the crop resolution (0 for small faces)
        var keepDetail = (float)Math.Clamp(back.ScaleFactor - 1, 0, 1);
        // Texture: not twice where the original already comes back through keepDetail
        var tex = highPass is null ? 0 : (float)Math.Clamp(blend.Texture, 0, 1) * (1 - keepDetail);
        var folds = (float)Math.Clamp(blend.Folds, 0, 1);
        var grainAmount = grain is null ? 0 : (float)Math.Max(0, blend.Grain);
        var plane = size * size;

        Parallel.For(y0, y1 + 1, ComputeLimits.Options, y =>
        {
            Span<float> enh = stackalloc float[3];
            Span<float> low = stackalloc float[3];
            Span<float> coarse = stackalloc float[3];
            Span<float> value = stackalloc float[3];
            for (var x = x0; x <= x1; x++)
            {
                var (u, v) = toCrop.Apply(x, y);
                if (u < 0 || v < 0 || u > size - 1 || v > size - 1) continue;

                var mask = EdgeWeight(u, v, size) * strength;
                if (mask <= 0) continue;

                SamplePlanar(face.Enhanced, size, u, v, enh);
                if (keepDetail > 0) SamplePlanar(face.Crop, size, u, v, low);
                if (tex > 0) SamplePlanar(face.Low, size, u, v, coarse);
                var fold = folds > 0 ? folds * SampleOne(face.Folds, size, u, v) : 0;

                var i = ((long)y * w + x) * 3;
                for (var c = 0; c < 3; c++)
                {
                    var original = image[i + c];
                    var val = enh[c] + (keepDetail > 0 ? (original - low[c]) * keepDetail : 0);
                    // Swap the fine structure of the model (enh − coarse) for that of the original
                    if (tex > 0) val += tex * (highPass![i + c] - (enh[c] - coarse[c]));
                    value[c] = val + fold;   // folds: brightness, equal on all channels
                }

                if (grainAmount > 0)
                {
                    // Grain in the brightness, as strong as in the photo at this brightness
                    var g = grain!;
                    int gx = x - g.X0, gy = y - g.Y0;
                    if (gx >= 0 && gy >= 0 && gx < g.Width && gy < g.Height)
                    {
                        var luma = 0.2126f * value[0] + 0.7152f * value[1] + 0.0722f * value[2];
                        var n = grainAmount * face.GrainGain * g.Profile.SigmaAt(luma) * g.Field[gy * g.Width + gx];
                        for (var c = 0; c < 3; c++) value[c] += n;
                    }
                }

                for (var c = 0; c < 3; c++)
                    image[i + c] = image[i + c] + (value[c] - image[i + c]) * mask;
            }
        });
    }


    /// <summary>
    /// How much grain the pasted face still lacks compared with the photo: pastes the face once at full strength
    /// into its image area and measures on the skin (<see cref="FaceDetails.GrainGain"/>).
    /// </summary>
    private static float MeasureGrainGain(float[] image, int w, int h, FaceRun.Face face, GrainProfile profile)
    {
        if (profile.Average <= 0) return 0;
        var (x0, y0, x1, y1) = Bounds(face.ToCrop, face.Size, w, h);
        int bw = x1 - x0 + 1, bh = y1 - y0 + 1;
        if (bw < 8 || bh < 8) return 1;

        var sub = new float[bw * bh * 3];
        for (var y = 0; y < bh; y++)
            Array.Copy(image, ((long)(y + y0) * w + x0) * 3, sub, (long)y * bw * 3, bw * 3);
        var t = face.ToCrop;
        var shifted = new Similarity(t.A, t.B, t.Tx + t.A * x0 - t.B * y0, t.Ty + t.B * x0 + t.A * y0);
        PasteBack(sub, bw, bh, face, null, shifted, new AiBlend(), null);

        var luma = new float[bw * bh];
        var weight = new float[bw * bh];
        for (var y = 0; y < bh; y++)
        for (var x = 0; x < bw; x++)
        {
            var i = y * bw + x;
            luma[i] = 0.2126f * sub[i * 3] + 0.7152f * sub[i * 3 + 1] + 0.0722f * sub[i * 3 + 2];
            var (u, v) = shifted.Apply(x, y);
            if (u >= 0 && v >= 0 && u <= face.Size - 1 && v <= face.Size - 1)
                weight[i] = EdgeWeight(u, v, face.Size) * SampleOne(face.FoldMask, face.Size, u, v);
        }
        return FaceDetails.GrainGain(luma, weight, bw, bh, profile);
    }
    /// <summary>Bilinear sample from a single plane (size × size).</summary>
    internal static float SampleOne(float[] data, int size, double u, double v)
    {
        int iu = Math.Min((int)u, size - 2), iv = Math.Min((int)v, size - 2);
        float fu = (float)(u - iu), fv = (float)(v - iv);
        var p00 = data[iv * size + iu];
        var p10 = data[iv * size + iu + 1];
        var p01 = data[(iv + 1) * size + iu];
        var p11 = data[(iv + 1) * size + iu + 1];
        return (p00 * (1 - fu) + p10 * fu) * (1 - fv) + (p01 * (1 - fu) + p11 * fu) * fv;
    }

    private static float[] ToModelRange(float[] crop)
    {
        var result = new float[crop.Length];
        for (var i = 0; i < crop.Length; i++) result[i] = crop[i] * 2 - 1;
        return result;
    }

    private static float[] FromModelRange(float[] output)
    {
        var result = new float[output.Length];
        for (var i = 0; i < output.Length; i++) result[i] = (Math.Clamp(output[i], -1f, 1f) + 1) / 2;
        return result;
    }

    /// <summary>Bilinear sample from the interleaved RGB image; outside the image the edge pixels are used.</summary>
    private static void Sample(float[] image, int w, int h, double x, double y, Span<float> rgb)
    {
        x = Math.Clamp(x, 0, w - 1);
        y = Math.Clamp(y, 0, h - 1);
        int ix = (int)x, iy = (int)y;
        float fx = (float)(x - ix), fy = (float)(y - iy);
        int ix1 = Math.Min(ix + 1, w - 1), iy1 = Math.Min(iy + 1, h - 1);

        for (var c = 0; c < 3; c++)
        {
            var p00 = image[((long)iy * w + ix) * 3 + c];
            var p10 = image[((long)iy * w + ix1) * 3 + c];
            var p01 = image[((long)iy1 * w + ix) * 3 + c];
            var p11 = image[((long)iy1 * w + ix1) * 3 + c];
            rgb[c] = (p00 * (1 - fx) + p10 * fx) * (1 - fy) + (p01 * (1 - fx) + p11 * fx) * fy;
        }
    }

    /// <summary>Bilinear sample from a planar crop (3 × size × size).</summary>
    private static void SamplePlanar(float[] crop, int size, double u, double v, Span<float> rgb)
    {
        int iu = Math.Min((int)u, size - 2), iv = Math.Min((int)v, size - 2);
        float fu = (float)(u - iu), fv = (float)(v - iv);
        var plane = size * size;
        for (var c = 0; c < 3; c++)
        {
            var o = c * plane;
            var p00 = crop[o + iv * size + iu];
            var p10 = crop[o + iv * size + iu + 1];
            var p01 = crop[o + (iv + 1) * size + iu];
            var p11 = crop[o + (iv + 1) * size + iu + 1];
            rgb[c] = (p00 * (1 - fu) + p10 * fu) * (1 - fv) + (p01 * (1 - fu) + p11 * fu) * fv;
        }
    }
}
