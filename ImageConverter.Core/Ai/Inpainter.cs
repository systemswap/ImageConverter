using ImageConverter.Core.Imaging;
using ImageMagick;

namespace ImageConverter.Core.Ai;

/// <summary>Ein Pinselstrich über das, was verschwinden soll — Punkte und Radius in Bildpixeln.</summary>
public sealed record BrushStroke(IReadOnlyList<(double X, double Y)> Points, double Radius);

/// <summary>
/// Ein Füllmodell (LaMa, MI-GAN): bekommt einen quadratischen Ausschnitt und eine Maske und malt die markierte
/// Fläche passend zur Umgebung neu.
/// </summary>
public interface IInpaintModel
{
    /// <summary>Kantenlänge, mit der das Modell rechnet (512).</summary>
    int Size { get; }

    /// <param name="rgb">Ausschnitt, R-, G-, B-Ebene hintereinander, sRGB 0 … 1, <see cref="Size"/>².</param>
    /// <param name="mask">1 = neu malen, 0 = bleibt; <see cref="Size"/>².</param>
    /// <returns>Ergebnis im selben Aufbau wie <paramref name="rgb"/>.</returns>
    float[] Run(float[] rgb, float[] mask, CancellationToken ct);
}

/// <summary>
/// Wasserzeichen, Schrift oder Störendes entfernen: Die markierten Stellen werden in Ausschnitten um die Markierung
/// vom Modell neu gemalt und weich ins Bild gesetzt. Alles außerhalb der Markierung bleibt Pixel für Pixel, wie es war
/// (volle Tiefe, Profile, Alpha).
/// </summary>
/// <remarks>
/// Die Modelle rechnen mit 512 × 512. Ein Ausschnitt ist etwa doppelt so groß wie die Markierung darin — so sieht das
/// Modell genug Umgebung. Lange, schmale Markierungen (Schriftzug am Rand) werden in Stücke geteilt, sonst würde der
/// Ausschnitt stark verkleinert und die Füllung unscharf; jedes Stück sieht die schon gefüllten davor als Umgebung.
/// </remarks>
public static class Inpainter
{
    /// <summary>
    /// Um so viele Pixel wird die Markierung vergrößert: Ein knapp gemalter Strich ließe sonst Ränder des Zeichens
    /// stehen, die das Modell als Umriss weitermalt.
    /// </summary>
    public static double Grow(int width, int height) => Math.Max(3, Math.Min(width, height) / 400.0);

    /// <summary>Kleinste Kante eines Ausschnitts — auch ein winziger Fleck braucht etwas Umgebung.</summary>
    private const int MinCrop = 192;

    /// <summary>Maske der Striche (1 = neu malen), um <paramref name="grow"/> vergrößert.</summary>
    public static byte[] Rasterize(IReadOnlyList<BrushStroke> strokes, int width, int height, double grow)
    {
        var mask = new byte[width * height];
        foreach (var s in strokes)
        {
            var r = s.Radius + grow;
            var pts = s.Points.Count == 1 ? [s.Points[0], s.Points[0]] : s.Points;
            for (var i = 0; i + 1 < pts.Count; i++)
                Capsule(mask, width, height, pts[i], pts[i + 1], r);
        }
        return mask;
    }

    /// <summary>Alle Pixel, deren Mitte höchstens <paramref name="r"/> von der Strecke a–b entfernt ist.</summary>
    private static void Capsule(byte[] mask, int width, int height, (double X, double Y) a, (double X, double Y) b, double r)
    {
        var x0 = Math.Max(0, (int)Math.Floor(Math.Min(a.X, b.X) - r));
        var x1 = Math.Min(width - 1, (int)Math.Ceiling(Math.Max(a.X, b.X) + r));
        var y0 = Math.Max(0, (int)Math.Floor(Math.Min(a.Y, b.Y) - r));
        var y1 = Math.Min(height - 1, (int)Math.Ceiling(Math.Max(a.Y, b.Y) + r));
        double dx = b.X - a.X, dy = b.Y - a.Y, len2 = dx * dx + dy * dy, r2 = r * r;
        for (var y = y0; y <= y1; y++)
        for (var x = x0; x <= x1; x++)
        {
            double px = x + 0.5 - a.X, py = y + 0.5 - a.Y;
            var t = len2 > 0 ? Math.Clamp((px * dx + py * dy) / len2, 0, 1) : 0;
            double ex = px - t * dx, ey = py - t * dy;
            if (ex * ex + ey * ey <= r2) mask[y * width + x] = 1;
        }
    }

    /// <summary>Ganzzahliges Rechteck in Bildpixeln.</summary>
    internal readonly record struct Box(int X, int Y, int Width, int Height)
    {
        public int Right => X + Width;
        public int Bottom => Y + Height;
        public bool Touches(Box o, int gap) =>
            X - gap < o.Right && o.X - gap < Right && Y - gap < o.Bottom && o.Y - gap < Bottom;
        public Box Union(Box o)
        {
            int x = Math.Min(X, o.X), y = Math.Min(Y, o.Y);
            return new Box(x, y, Math.Max(Right, o.Right) - x, Math.Max(Bottom, o.Bottom) - y);
        }
    }

    /// <summary>
    /// Stücke, die nacheinander gefüllt werden: je Strich sein Rahmen, nahe beieinander liegende zusammengefasst,
    /// lange schmale geteilt (Stück höchstens doppelt so lang wie breit).
    /// </summary>
    internal static List<Box> Pieces(IReadOnlyList<BrushStroke> strokes, int width, int height, double grow)
    {
        var boxes = new List<Box>();
        foreach (var s in strokes)
        {
            if (s.Points.Count == 0) continue;
            var r = s.Radius + grow;
            var x0 = Math.Max(0, (int)Math.Floor(s.Points.Min(p => p.X) - r));
            var y0 = Math.Max(0, (int)Math.Floor(s.Points.Min(p => p.Y) - r));
            var x1 = Math.Min(width, (int)Math.Ceiling(s.Points.Max(p => p.X) + r));
            var y1 = Math.Min(height, (int)Math.Ceiling(s.Points.Max(p => p.Y) + r));
            if (x1 > x0 && y1 > y0) boxes.Add(new Box(x0, y0, x1 - x0, y1 - y0));
        }

        // Zusammenfassen, was sich berührt oder dicht beieinander liegt (ein Schriftzug aus mehreren Strichen)
        for (var merged = true; merged;)
        {
            merged = false;
            for (var i = 0; i < boxes.Count && !merged; i++)
            for (var j = i + 1; j < boxes.Count && !merged; j++)
            {
                var gap = Math.Max(8, Math.Min(Math.Max(boxes[i].Width, boxes[i].Height), Math.Max(boxes[j].Width, boxes[j].Height)) / 4);
                if (!boxes[i].Touches(boxes[j], gap)) continue;
                boxes[i] = boxes[i].Union(boxes[j]);
                boxes.RemoveAt(j);
                merged = true;
            }
        }

        var pieces = new List<Box>();
        foreach (var b in boxes)
        {
            var horizontal = b.Width >= b.Height;
            var (longSide, shortSide) = horizontal ? (b.Width, b.Height) : (b.Height, b.Width);
            var count = Math.Max(1, (int)Math.Ceiling(longSide / (2.0 * Math.Max(shortSide, MinCrop / 2))));
            for (var k = 0; k < count; k++)
            {
                var from = longSide * k / count;
                var to = longSide * (k + 1) / count;
                pieces.Add(horizontal
                    ? new Box(b.X + from, b.Y, to - from, b.Height)
                    : new Box(b.X, b.Y + from, b.Width, to - from));
            }
        }
        return pieces;
    }

    /// <summary>Ausschnitt um ein Stück: etwa doppelt so groß, möglichst quadratisch, ganz im Bild.</summary>
    internal static Box CropAround(Box piece, int width, int height)
    {
        var side = Math.Max(MinCrop, 2 * Math.Max(piece.Width, piece.Height));
        int w = Math.Min(width, side), h = Math.Min(height, side);
        var x = Math.Clamp(piece.X + piece.Width / 2 - w / 2, 0, width - w);
        var y = Math.Clamp(piece.Y + piece.Height / 2 - h / 2, 0, height - h);
        return new Box(x, y, w, h);
    }

    /// <summary>
    /// Entfernt, was unter den Strichen liegt. Liefert ein neues Bild; <paramref name="source"/> bleibt unverändert.
    /// </summary>
    public static MagickImage Run(MagickImage source, IReadOnlyList<BrushStroke> strokes, IInpaintModel model,
        IProgress<double>? progress, CancellationToken ct)
    {
        var result = ImageOps.CloneOf(source);
        if (result.ColorSpace == ColorSpace.CMYK) result.ColorSpace = ColorSpace.sRGB;   // das Modell malt in RGB
        // Eigene Pixel erzwingen: Eine frische Kopie teilt sie noch mit dem Original — direkt hineingeschrieben wurde
        // danach alles außerhalb des Ausschnitts schwarz (Magick.NET 14.17.2). „+ 0“ rechnet jeden Pixel einmal durch.
        result.Evaluate(Channels.All, EvaluateOperator.Add, 0);
        int width = (int)result.Width, height = (int)result.Height;
        var grow = Grow(width, height);
        var hole = Rasterize(strokes, width, height, grow);
        var pieces = Pieces(strokes, width, height, grow);

        for (var n = 0; n < pieces.Count; n++)
        {
            ct.ThrowIfCancellationRequested();
            FillPiece(result, hole, CropAround(pieces[n], width, height), grow, model, ct);
            progress?.Report((n + 1) / (double)pieces.Count);
        }
        return result;
    }

    /// <summary>
    /// Einen Ausschnitt füllen. Das Modell bekommt alle noch offenen Stellen darin; übernommen (und danach als
    /// gefüllt gezählt) wird aber nur der innere Teil — am Rand des Ausschnitts fehlt dem Modell die Umgebung, dort
    /// entstanden Nähte und schmale Reste, die MI-GAN dann ins Innere weitermalte. Den Rand füllt das Nachbarstück,
    /// in dessen Innerem er liegt (jedes Stück liegt im inneren Teil seines eigenen Ausschnitts).
    /// </summary>
    private static void FillPiece(MagickImage image, byte[] hole, Box crop, double grow, IInpaintModel model, CancellationToken ct)
    {
        int width = (int)image.Width, height = (int)image.Height, cw = crop.Width, ch = crop.Height, size = model.Size;
        var inner = InnerPart(crop, width, height);

        var cropMask = new byte[cw * ch];
        var any = false;
        for (var y = 0; y < ch; y++)
        for (var x = 0; x < cw; x++)
            if (hole[(crop.Y + y) * width + crop.X + x] == 1) { cropMask[y * cw + x] = 1; any = true; }
        if (!any) return;

        var bgra = ReadBgra(image, crop);

        // Modell-Eingang: Ausschnitt auf size × size, Maske so, dass jeder berührte Pixel als offen gilt
        float[] rgb;
        using (var cropImage = Pixels.ToMagick16(bgra, cw, ch, alpha: false))
        using (var small = ImageOps.Resize(cropImage, size, size, ResampleFilter.Lanczos3, gammaCorrect: false))
        {
            var px = Pixels.FromMagick(small);
            rgb = new float[3 * size * size];
            for (var i = 0; i < size * size; i++)
            {
                rgb[i] = px[i * 4 + 2];
                rgb[size * size + i] = px[i * 4 + 1];
                rgb[2 * size * size + i] = px[i * 4];
            }
            PixelPool.Return(px);
        }
        // Lanczos verschmiert beim Verkleinern das Zeichen bis zu drei Modellpixel über den Maskenrand — dieser helle
        // Saum lag sonst ungelöscht neben der Lücke, und LaMa setzte ihn als Band in die Lücke fort
        var bleed = (int)Math.Ceiling(3 * Math.Max(1.0, size / (double)Math.Min(cw, ch)));
        var mask = Dilate(ShrinkMask(cropMask, cw, ch, size), size, bleed);

        var output = model.Run(rgb, mask, ct);

        // Zurück auf die Ausschnittgröße
        var outBgra = new float[size * size * 4];
        for (var i = 0; i < size * size; i++)
        {
            outBgra[i * 4] = output[2 * size * size + i];
            outBgra[i * 4 + 1] = output[size * size + i];
            outBgra[i * 4 + 2] = output[i];
            outBgra[i * 4 + 3] = 1;
        }
        float[] filled;
        using (var outImage = Pixels.ToMagick16(outBgra, size, size, alpha: false))
        using (var back = ImageOps.Resize(outImage, cw, ch, ResampleFilter.Lanczos3, gammaCorrect: false))
            filled = Pixels.FromMagick(back);

        // Weich einsetzen: innen ganz, zum Rand der (vergrößerten) Markierung hin auslaufend
        var alpha = Feather(cropMask, cw, ch, Math.Max(1, (int)Math.Round(grow / 2)));
        for (var y = 0; y < ch; y++)
        for (var x = 0; x < cw; x++)
        {
            var i = y * cw + x;
            var keep = x >= inner.X && x < inner.Right && y >= inner.Y && y < inner.Bottom;
            filled[i * 4 + 3] = keep ? alpha[i] : 0;
            if (keep && cropMask[i] == 1) hole[(crop.Y + y) * width + crop.X + x] = 0;
        }
        Insert(image, crop, filled);
        PixelPool.Return(filled);
    }

    /// <summary>
    /// Innerer Teil eines Ausschnitts (in Ausschnittkoordinaten): ein Viertel der kürzeren Seite Abstand zum Rand —
    /// außer dort, wo der Ausschnitt an den Bildrand stößt (dahinter gibt es keine Umgebung, die fehlen könnte).
    /// </summary>
    internal static Box InnerPart(Box crop, int width, int height)
    {
        var m = Math.Min(crop.Width, crop.Height) / 4;
        var left = crop.X == 0 ? 0 : m;
        var top = crop.Y == 0 ? 0 : m;
        var right = crop.Right == width ? crop.Width : crop.Width - m;
        var bottom = crop.Bottom == height ? crop.Height : crop.Height - m;
        return new Box(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top));
    }

    /// <summary>Maske auf size × size: offen, sobald ein Pixel im Einzugsbereich offen ist (nichts geht verloren).</summary>
    internal static float[] ShrinkMask(byte[] mask, int width, int height, int size)
    {
        var result = new float[size * size];
        for (var ty = 0; ty < size; ty++)
        {
            var y0 = ty * height / size;
            var y1 = Math.Max(y0 + 1, (ty + 1) * height / size);
            for (var tx = 0; tx < size; tx++)
            {
                var x0 = tx * width / size;
                var x1 = Math.Max(x0 + 1, (tx + 1) * width / size);
                for (var y = y0; y < y1 && result[ty * size + tx] == 0; y++)
                for (var x = x0; x < x1; x++)
                    if (mask[y * width + x] == 1) { result[ty * size + tx] = 1; break; }
            }
        }
        return result;
    }

    /// <summary>Maske um <paramref name="r"/> Pixel vergrößern (Quadrat, getrennt waagerecht und senkrecht).</summary>
    internal static float[] Dilate(float[] mask, int size, int r)
    {
        var tmp = new float[mask.Length];
        var result = new float[mask.Length];
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            if (mask[y * size + x] == 0) continue;
            for (var k = Math.Max(0, x - r); k <= Math.Min(size - 1, x + r); k++) tmp[y * size + k] = 1;
        }
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            if (tmp[y * size + x] == 0) continue;
            for (var k = Math.Max(0, y - r); k <= Math.Min(size - 1, y + r); k++) result[k * size + x] = 1;
        }
        return result;
    }

    /// <summary>Deckkraft beim Einsetzen: Maske zweimal mit einem Kasten von ±<paramref name="radius"/> weichgezeichnet.</summary>
    internal static float[] Feather(byte[] mask, int width, int height, int radius)
    {
        var a = new float[mask.Length];
        for (var i = 0; i < a.Length; i++) a[i] = mask[i];
        for (var pass = 0; pass < 2; pass++)
        {
            a = BoxBlur(a, width, height, radius, horizontal: true);
            a = BoxBlur(a, width, height, radius, horizontal: false);
        }
        // Was markiert war, kommt ganz vom Modell — weich wird nur der Rand nach außen
        for (var i = 0; i < a.Length; i++) a[i] = mask[i] == 1 ? 1 : Math.Min(1, a[i] * 2);
        return a;
    }

    private static float[] BoxBlur(float[] src, int width, int height, int r, bool horizontal)
    {
        var dst = new float[src.Length];
        var (lines, length) = horizontal ? (height, width) : (width, height);
        for (var line = 0; line < lines; line++)
        {
            int Index(int k) => horizontal ? line * width + k : k * width + line;
            double sum = 0;
            var count = 0;
            for (var k = 0; k <= Math.Min(r, length - 1); k++) { sum += src[Index(k)]; count++; }
            for (var k = 0; k < length; k++)
            {
                dst[Index(k)] = (float)(sum / count);
                var add = k + r + 1;
                var remove = k - r;
                if (add < length) { sum += src[Index(add)]; count++; }
                if (remove >= 0) { sum -= src[Index(remove)]; count--; }
            }
        }
        return dst;
    }

    private static float[] ReadBgra(MagickImage image, Box crop)
    {
        using var pixels = image.GetPixelsUnsafe();
        var data = pixels.ToShortArray(crop.X, crop.Y, (uint)crop.Width, (uint)crop.Height, "BGRA")
                   ?? throw new InvalidOperationException(Tr.T("Pixel konnten nicht gelesen werden."));
        var px = new float[data.Length];
        for (var i = 0; i < data.Length; i++) px[i] = data[i] / 65535f;
        return px;
    }

    /// <summary>Füllung mit ihrer Deckkraft (Alpha im Puffer) in den Ausschnitt mischen; Alpha und weitere Kanäle bleiben.</summary>
    /// <remarks>
    /// Über die Pixel-API, nicht über Composite: Composite veränderte auch Pixel mit Deckkraft 0 um ±2 Stufen
    /// (Magick.NET 14.17.2) — außerhalb der Markierung soll aber alles exakt bleiben.
    /// </remarks>
    private static void Insert(MagickImage image, Box crop, float[] bgra)
    {
        using var pixels = image.GetPixelsUnsafe();
        var channels = (int)pixels.Channels;
        var area = pixels.GetArea(crop.X, crop.Y, (uint)crop.Width, (uint)crop.Height)
                   ?? throw new InvalidOperationException(Tr.T("Pixel konnten nicht gelesen werden."));
        var targets = new List<(int Index, int Source)>();
        foreach (var (channel, source) in new[] { (PixelChannel.Red, 2), (PixelChannel.Green, 1), (PixelChannel.Blue, 0) })
            if (pixels.GetChannelIndex(channel) is uint index && index < channels && targets.All(t => t.Index != index))
                targets.Add(((int)index, source));
        for (var p = 0; p < crop.Width * crop.Height; p++)
        {
            var a = bgra[p * 4 + 3];
            if (a <= 0) continue;
            foreach (var (index, source) in targets)
            {
                var old = area[p * channels + index];
                var v = old + (Math.Clamp(bgra[p * 4 + source], 0, 1) * 65535f - old) * Math.Min(1, a);
                area[p * channels + index] = (ushort)(v <= 0 ? 0 : v >= 65535 ? 65535 : v + 0.5f);
            }
        }
        pixels.SetArea(crop.X, crop.Y, (uint)crop.Width, (uint)crop.Height, area);
    }
}
