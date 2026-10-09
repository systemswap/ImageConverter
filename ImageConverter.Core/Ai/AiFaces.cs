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
    internal static readonly (double X, double Y)[] Template =
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
            bgr = pixels.ToByteArray("BGR") ?? throw new InvalidOperationException(Tr.T("Pixel konnten nicht gelesen werden."));
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

    /// <summary>Gesichter verbessern und mit <paramref name="strength"/> ins Bild mischen (ohne Detailregler).</summary>
    /// <param name="strength">0…1: Deckkraft über dem Original.</param>
    /// <param name="progress">Anteil 0…1.</param>
    /// <param name="preview">Zwischenstand nach jedem Gesicht.</param>
    public static MagickImage Restore(MagickImage source, IReadOnlyList<DetectedFace> faces, IFaceModel model,
        double strength, IProgress<double>? progress, CancellationToken ct,
        Action<AiPreviewFrame>? preview = null, int previewMaxEdge = 2048) =>
        Run(source, faces, model, null, progress, ct, preview, previewMaxEdge).Compose(new AiBlend(strength));

    /// <summary>
    /// Gesichter mit voller Wirkung verbessern, aber noch nicht ins Bild mischen: Das Ergebnis
    /// (<see cref="FaceRun"/>) mischt danach Stärke, Hautstruktur, Falten und Korn beliebig oft neu, ohne das Modell.
    /// Vor dem Modell wird das Ausgangsbild eingelesen (Gesichtsbereiche, Kornprofil), danach mit dem
    /// Ergebnis verglichen (was an Falten und Zügen verloren ging).
    /// </summary>
    /// <param name="parser">Gesichtsbereiche; ohne ersatzweise Kreise um Augen und Mund.</param>
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

        // Vor der KI: wie sieht das Korn des Fotos aus?
        var grain = FaceDetails.MeasureGrain(image, w, h);

        var sink = new AiPreviewSink(preview, previewMaxEdge);
        var size = model.Size;
        var target = Template.Select(p => (p.X * size, p.Y * size)).ToArray();
        var done = new List<FaceRun.Face>();
        float[]? shown = null;   // Zwischenstand während der Rechnung
        var pasted = 0;
        for (var f = 0; f < faces.Count; f++)
        {
            ct.ThrowIfCancellationRequested();
            var toCrop = Similarity.Estimate(faces[f].Landmarks, target);
            var crop = WarpToCrop(image, w, h, toCrop, size);

            // vor der KI: Bereiche des Gesichts (Haut, Augen, Mund …) im Original
            var labels = parser?.Parse(crop, size, ct);
            var foldMask = FaceDetails.FoldMask(labels, size, Template, FaceDetails.FoldsKeepAway(toCrop.ScaleFactor));

            var enhanced = FromModelRange(model.Run(ToModelRange(crop), ct));

            // nach der KI: was hat das Modell an Falten und Zügen weggeglättet?
            var folds = FaceDetails.Folds(crop, enhanced, size, toCrop.ScaleFactor, foldMask);
            var face = new FaceRun.Face(crop, enhanced, Smoothed(enhanced, size, toCrop), toCrop, size, folds, foldMask,
                FaceDetails.KeptMask(labels, size, Template));
            face = face with { GrainGain = MeasureGrainGain(image, w, h, face, grain) };   // Korn, das schon da ist
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
    /// Grobe Fassung des verbesserten Gesichts: Unterschied dazu = seine feine Struktur. Geglättet über etwa
    /// ein Pixel des Bildes (im Ausschnitt so viele Pixel, wie ein Bildpixel dort groß ist).
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

    /// <summary>Was als „feine Struktur“ gilt: Unterschiede über so viele Bildpixel (Gauß-Sigma).</summary>
    internal const double FineDetailPixels = 1.0;

    /// <summary>Feine Struktur des Bildes: Bild minus über <see cref="FineDetailPixels"/> geglättet (RGB verschachtelt).</summary>
    internal static float[] HighPass(float[] image, int w, int h)
    {
        var blurred = (float[])image.Clone();
        Blur.Gauss(blurred, w, h, FineDetailPixels, channels: 3);
        for (long i = 0; i < blurred.Length; i++) blurred[i] = image[i] - blurred[i];
        return blurred;
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

    /// <summary>Bildbereich, den ein Gesicht beim Zurücksetzen berührt (Bildkoordinaten, einschließlich).</summary>
    internal static (int X0, int Y0, int X1, int Y1) Bounds(Similarity toCrop, int size, int w, int h)
    {
        var back = toCrop.Inverse();
        var corners = new[] { back.Apply(0, 0), back.Apply(size - 1, 0), back.Apply(0, size - 1), back.Apply(size - 1, size - 1) };
        return (Math.Max(0, (int)Math.Floor(corners.Min(c => c.X))), Math.Max(0, (int)Math.Floor(corners.Min(c => c.Y))),
            Math.Min(w - 1, (int)Math.Ceiling(corners.Max(c => c.X))), Math.Min(h - 1, (int)Math.Ceiling(corners.Max(c => c.Y))));
    }

    /// <summary>Weicher Rand beim Zurücksetzen: 0 bis 10 px vom Rand des Ausschnitts, voll ab 66 px (bei 512).</summary>
    internal static float EdgeWeight(double u, double v, int size)
    {
        var d = Math.Min(Math.Min(u, v), Math.Min(size - 1 - u, size - 1 - v)) / (size / 512.0);
        var t = Math.Clamp((d - 10) / 56, 0, 1);
        return (float)(t * t * (3 - 2 * t));
    }

    /// <summary>Korn für ein Gesicht in den Koordinaten des Bildes, auf das zurückgesetzt wird.</summary>
    /// <param name="Field">Korn mit Streuung 1 (bzw. verkleinert in der Vorschau), Zeile für Zeile ab <paramref name="X0"/>, <paramref name="Y0"/>.</param>
    internal sealed record GrainPatch(float[] Field, int X0, int Y0, int Width, int Height, GrainProfile Profile);

    /// <summary>
    /// Verbessertes Gesicht zurück ins Bild, mit weichem Rand zum Rand des Ausschnitts.
    /// Bei Gesichtern, die größer als der Ausschnitt sind, bleiben die feinen Details des
    /// Originals erhalten, die im Ausschnitt gar nicht vorkommen können.
    /// </summary>
    /// <param name="highPass">Feine Struktur des Bildes davor (<see cref="HighPass"/>, gleiche Größe wie
    /// <paramref name="image"/>); nur für Hautstruktur &gt; 0 nötig.</param>
    /// <param name="toCrop">Bild → Ausschnitt; für die Vorschau auf deren Maßstab umgerechnet.</param>
    /// <param name="blend">Stärke (Deckkraft); Hautstruktur: feine Struktur aus dem Bild davor statt der des Modells —
    /// gegen das „Wachsgesicht“; Falten: was das Modell an Zügen weggeglättet hat, auf der Haut zurück; Korn.</param>
    /// <param name="grain">Korn in diesen Koordinaten; nur für Korn &gt; 0 nötig.</param>
    internal static void PasteBack(float[] image, int w, int h, FaceRun.Face face, float[]? highPass,
        Similarity toCrop, AiBlend blend, GrainPatch? grain)
    {
        var size = face.Size;
        var (x0, y0, x1, y1) = Bounds(toCrop, size, w, h);
        var strength = (float)Math.Clamp(blend.Strength, 0, 1);
        if (x1 < x0 || y1 < y0 || strength <= 0) return;

        var back = toCrop.Inverse();
        // Anteil der Originaldetails jenseits der Ausschnitt-Auflösung (0 bei kleinen Gesichtern)
        var keepDetail = (float)Math.Clamp(back.ScaleFactor - 1, 0, 1);
        // Hautstruktur: dort, wo das Original schon über keepDetail zurückkommt, nicht doppelt
        var tex = highPass is null ? 0 : (float)Math.Clamp(blend.Texture, 0, 1) * (1 - keepDetail);
        var folds = (float)Math.Clamp(blend.Folds, 0, 1);
        var grainAmount = grain is null ? 0 : (float)Math.Max(0, blend.Grain);
        var plane = size * size;

        Parallel.For(y0, y1 + 1, y =>
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
                    // feine Struktur des Modells (enh − grob) gegen die des Originals tauschen
                    if (tex > 0) val += tex * (highPass![i + c] - (enh[c] - coarse[c]));
                    value[c] = val + fold;   // Falten: Helligkeit, auf alle Kanäle gleich
                }

                if (grainAmount > 0)
                {
                    // Korn in der Helligkeit, so stark wie im Foto bei dieser Helligkeit
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
    /// Wie viel Korn dem eingesetzten Gesicht bis zum Foto noch fehlt: das Gesicht einmal mit voller Stärke in
    /// seinen Bildausschnitt setzen und auf der Haut messen (<see cref="FaceDetails.GrainGain"/>).
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
    /// <summary>Bilinear aus einer einzelnen Ebene (size × size).</summary>
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
