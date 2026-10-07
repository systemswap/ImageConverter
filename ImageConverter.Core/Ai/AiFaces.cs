using ImageConverter.Core.Imaging;
using ImageMagick;
using Microsoft.ML.OnnxRuntime;

namespace ImageConverter.Core.Ai;

/// <summary>Gesichtsmodell: ausgerichteter Gesichtsausschnitt (RGB planar, −1…1) hinein und heraus.</summary>
public interface IFaceModel
{
    int Size { get; }
    float[] Run(float[] input, CancellationToken ct);
}

/// <summary>Ein gefundenes Gesicht mit fünf Punkten: Augen, Nase, Mundwinkel.</summary>
public sealed record DetectedFace(double X1, double Y1, double X2, double Y2, double Score, (double X, double Y)[] Landmarks)
{
    public double Width => X2 - X1;
    public double Height => Y2 - Y1;
}

/// <summary>
/// Ähnlichkeitstransformation (drehen, gleichmäßig skalieren, verschieben):
/// x' = a·x − b·y + tx,  y' = b·x + a·y + ty.
/// </summary>
public readonly record struct Similarity(double A, double B, double Tx, double Ty)
{
    public (double X, double Y) Apply(double x, double y) => (A * x - B * y + Tx, B * x + A * y + Ty);

    public double ScaleFactor => Math.Sqrt(A * A + B * B);

    public Similarity Inverse()
    {
        var d = A * A + B * B;
        double ia = A / d, ib = -B / d;
        return new Similarity(ia, ib, -(ia * Tx - ib * Ty), -(ib * Tx + ia * Ty));
    }

    /// <summary>Beste Transformation (kleinste Quadrate), die <paramref name="from"/> auf <paramref name="to"/> abbildet.</summary>
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
/// Gesichter verbessern: finden (YuNet), auf die Normlage des Modells ausrichten,
/// verbessern und mit weichem Rand zurücksetzen. Der Rest des Bildes bleibt unberührt.
/// </summary>
public static class AiFaces
{
    /// <summary>Normlage der fünf Punkte im 512er-Ausschnitt (FFHQ, wie beim Training).</summary>
    private static readonly (double X, double Y)[] Template =
    [
        (0.37691676, 0.46864664), (0.62285697, 0.46912813), (0.50123859, 0.61331904),
        (0.39308822, 0.72541100), (0.61150205, 0.72490465),
    ];

    /// <summary>Kleinere Gesichter (Breite in Pixeln) werden übergangen — da gibt es nichts zu retten.</summary>
    public const int MinFaceSize = 16;

    // ------------------------------------------------------------ Erkennung

    public static List<DetectedFace> Detect(MagickImage source, InferenceSession detector, double threshold = 0.5)
    {
        const int size = 640;
        byte[] bgr;
        int rw, rh;
        using (var small = ImageOps.CloneOf(source))
        {
            if (small.Width > size || small.Height > size)
                small.Resize(new MagickGeometry(size, size));   // Seitenverhältnis bleibt
            rw = (int)small.Width;
            rh = (int)small.Height;
            using var pixels = small.GetPixelsUnsafe();
            bgr = pixels.ToByteArray("BGR") ?? throw new InvalidOperationException("Pixel konnten nicht gelesen werden.");
        }

        // links oben einsetzen, Rest schwarz; Werte 0…255, BGR (so wurde YuNet trainiert)
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
    /// YuNet-Ausgaben lesen: je Raster (8, 16, 32 px) Klasse, „Objekt“, Rahmen, 5 Punkte.
    /// Jede Rasterzelle ist ein Anker; Werte sind relativ zur Zelle.
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

    /// <summary>Überlappende Treffer desselben Gesichts zusammenfassen: der sicherste bleibt.</summary>
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

    // ------------------------------------------------------------ Verbessern

    /// <param name="strength">0…1: Mischung mit dem Original.</param>
    /// <param name="progress">Anteil 0…1.</param>
    /// <param name="preview">Zwischenstand nach jedem Gesicht.</param>
    public static MagickImage Restore(MagickImage source, IReadOnlyList<DetectedFace> faces, IFaceModel model,
        double strength, IProgress<double>? progress, CancellationToken ct,
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

        var sink = new AiPreviewSink(preview, previewMaxEdge);
        var size = model.Size;
        var target = Template.Select(p => (p.X * size, p.Y * size)).ToArray();
        for (var f = 0; f < faces.Count; f++)
        {
            ct.ThrowIfCancellationRequested();
            var toCrop = Similarity.Estimate(faces[f].Landmarks, target);
            var crop = WarpToCrop(image, w, h, toCrop, size);
            var enhanced = model.Run(ToModelRange(crop), ct);
            PasteBack(image, w, h, crop, FromModelRange(enhanced), toCrop, size, (float)Math.Clamp(strength, 0, 1));
            progress?.Report((f + 1) / (double)faces.Count);

            if (sink.IsDue && f < faces.Count - 1)
                sink.Emit(w, h, (x, y, rgba) =>
                {
                    var i = ((long)y * w + x) * 3;
                    rgba[0] = image[i];
                    rgba[1] = image[i + 1];
                    rgba[2] = image[i + 2];
                    rgba[3] = hasAlpha ? pixels[((long)y * w + x) * channels + 3] / 65535f : 1f;
                });
        }

        for (long i = 0, j = 0; i < image.Length; i += 3, j += channels)
        {
            pixels[j] = (ushort)(Math.Clamp(image[i], 0f, 1f) * 65535f + 0.5f);
            pixels[j + 1] = (ushort)(Math.Clamp(image[i + 1], 0f, 1f) * 65535f + 0.5f);
            pixels[j + 2] = (ushort)(Math.Clamp(image[i + 2], 0f, 1f) * 65535f + 0.5f);
        }

        var result = AiEnhancer.WritePixels(pixels, w, h, mapping);
        lock (source) result.Density = source.Density;
        ImageExporter.CopyProfiles(source, result);
        return result;
    }

    /// <summary>
    /// Ausschnitt in Normlage, RGB planar 0…1. Ist das Gesicht im Bild größer als der
    /// Ausschnitt, wird pro Pixel über mehrere Punkte gemittelt (sonst Treppchen/Flimmern).
    /// </summary>
    internal static float[] WarpToCrop(float[] image, int w, int h, Similarity toCrop, int size)
    {
        var back = toCrop.Inverse();
        var samples = Math.Clamp((int)Math.Ceiling(back.ScaleFactor), 1, 4);
        var crop = new float[3 * size * size];
        var plane = size * size;

        Parallel.For(0, size, v =>
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

    /// <summary>
    /// Verbessertes Gesicht zurück ins Bild, mit weichem Übergang zum Rand des Ausschnitts.
    /// Bei Gesichtern, die größer als der Ausschnitt sind, bleiben die feinen Details des
    /// Originals erhalten, die im Ausschnitt gar nicht vorkommen können.
    /// </summary>
    private static void PasteBack(float[] image, int w, int h, float[] crop, float[] enhanced, Similarity toCrop, int size, float strength)
    {
        var back = toCrop.Inverse();
        var corners = new[] { back.Apply(0, 0), back.Apply(size - 1, 0), back.Apply(0, size - 1), back.Apply(size - 1, size - 1) };
        var x0 = Math.Max(0, (int)Math.Floor(corners.Min(c => c.X)));
        var x1 = Math.Min(w - 1, (int)Math.Ceiling(corners.Max(c => c.X)));
        var y0 = Math.Max(0, (int)Math.Floor(corners.Min(c => c.Y)));
        var y1 = Math.Min(h - 1, (int)Math.Ceiling(corners.Max(c => c.Y)));
        if (x1 < x0 || y1 < y0) return;

        // Anteil der Originaldetails jenseits der Ausschnitt-Auflösung (0 bei kleinen Gesichtern)
        var keepDetail = (float)Math.Clamp(back.ScaleFactor - 1, 0, 1);
        var edge = size / 512.0;   // Maße unten für 512er-Ausschnitt

        Parallel.For(y0, y1 + 1, y =>
        {
            Span<float> enh = stackalloc float[3];
            Span<float> low = stackalloc float[3];
            for (var x = x0; x <= x1; x++)
            {
                var (u, v) = toCrop.Apply(x, y);
                if (u < 0 || v < 0 || u > size - 1 || v > size - 1) continue;

                // weicher Rand: 0 bis 10 px vom Rand, voll ab 66 px (bei 512)
                var d = Math.Min(Math.Min(u, v), Math.Min(size - 1 - u, size - 1 - v)) / edge;
                var t = Math.Clamp((d - 10) / 56, 0, 1);
                var mask = (float)(t * t * (3 - 2 * t)) * strength;
                if (mask <= 0) continue;

                SamplePlanar(enhanced, size, u, v, enh);
                if (keepDetail > 0) SamplePlanar(crop, size, u, v, low);

                var i = ((long)y * w + x) * 3;
                for (var c = 0; c < 3; c++)
                {
                    var original = image[i + c];
                    var value = enh[c] + (keepDetail > 0 ? (original - low[c]) * keepDetail : 0);
                    image[i + c] = original + (value - original) * mask;
                }
            }
        });
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

    /// <summary>Bilinear aus dem RGB-Bild (verschachtelt), außerhalb: Randpixel.</summary>
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

    /// <summary>Bilinear aus einem planaren Ausschnitt (3 × size × size).</summary>
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
