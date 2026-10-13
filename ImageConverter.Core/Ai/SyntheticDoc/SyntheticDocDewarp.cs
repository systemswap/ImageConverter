using ImageConverter.Core.Imaging;
using ImageMagick;
using Microsoft.ML.OnnxRuntime;

namespace ImageConverter.Core.Ai.SyntheticDoc;

/// <summary>
/// „KI begradigen“ mit SyntheticDoc: das Modell sagt für ein fotografiertes Blatt die Rückabbildung voraus — ein Gitter von
/// 121 × 177 Stellen des flachen Blatts, je mit der Stelle im Foto. Zwei Läufe: ein grober auf einem Viertel der Pixel
/// (720 × 512), nur um das Blatt zu finden, dann der genaue (1440 × 1024, so wurde trainiert) auf dem Ausschnitt um das Blatt
/// (+ 4 % je Seite) — trainiert wurde nur auf solchen Ausschnitten. Das Seitenverhältnis kennt das Modell nicht: es kommt aus
/// unserem Blattmodell, dem Gitterzeilen und -spalten als Linien dienen (mit DIN-Einrasten), ersatzweise aus den Bogenlängen.
/// Der genaue Lauf liefert auch die Schattenkarte (wie viel Licht jede Stelle des Blatts bekam); sie kommt gerade gezogen
/// und verkleinert ins Gitter (<see cref="DewarpShading"/>) und wird beim Rendern herausgerechnet, wenn eingeschaltet.
/// </summary>
public static class SyntheticDocDewarp
{
    public const int InputWidth = 1024, InputHeight = 1440, GridWidth = 177, GridHeight = 121;

    /// <summary>Raster der Schattenkarte im Ergebnis: ein Viertel der Eingabe des Modells (Licht ändert sich langsam).</summary>
    public const int ShadingWidth = InputWidth / 4, ShadingHeight = InputHeight / 4;

    /// <summary>
    /// Gitter für das Bild (in der Ansicht, volle Auflösung) — relativ, also für jede Größe des Bildes gültig.
    /// <paramref name="shadowSession"/>: Modell mit Schattenkarte für den genauen Lauf (null: ohne Schatten, nur das Gitter).
    /// </summary>
    public static DewarpGrid Run(InferenceSession session, InferenceSession? shadowSession, MagickImage doc, double focal35,
        bool removeShadows, CancellationToken ct)
    {
        int w, h;
        lock (doc) (w, h) = ((int)doc.Width, (int)doc.Height);
        var (first, _) = Predict(session, doc, 0, 0, w, h, InputWidth / 2, InputHeight / 2, false, ct);

        // Blatt eingrenzen: Umriss des Gitters, 4 % Rand
        double bx0 = double.MaxValue, by0 = double.MaxValue, bx1 = double.MinValue, by1 = double.MinValue;
        for (var j = 0; j < GridHeight; j++)
        for (var i = 0; i < GridWidth; i++)
        {
            if (j != 0 && j != GridHeight - 1 && i != 0 && i != GridWidth - 1) continue;
            var (x, y) = first[j, i];
            (bx0, bx1, by0, by1) = (Math.Min(bx0, x), Math.Max(bx1, x), Math.Min(by0, y), Math.Max(by1, y));
        }
        double mx = 0.04 * (bx1 - bx0), my = 0.04 * (by1 - by0);
        int x0 = (int)Math.Max(0, Math.Floor(bx0 - mx)), y0 = (int)Math.Max(0, Math.Floor(by0 - my));
        int x1 = (int)Math.Min(w, Math.Ceiling(bx1 + mx)), y1 = (int)Math.Min(h, Math.Ceiling(by1 + my));
        if (x1 - x0 < 0.2 * w || y1 - y0 < 0.2 * h) (x0, y0, x1, y1) = (0, 0, w, h);   // nichts Brauchbares gefunden: ganzes Bild
        var (grid, light) = Predict(shadowSession ?? session, doc, x0, y0, x1, y1, InputWidth, InputHeight, shadowSession is not null, ct);
        var result = ToGrid(grid, w, h, focal35);
        if (light is null) return result;
        return result with { Shading = Straighten(light, result, w, h, x0, y0, x1, y1, removeShadows) };
    }

    /// <summary>Schattenkarte des genauen Laufs (im Ausschnitt, verkleinert) und die Helligkeit der Eingabe.</summary>
    private sealed record Light(float[] Rgb, int Width, int Height, double Brightness);

    /// <summary>
    /// Schattenkarte gerade ziehen: für jede Stelle des Rasters über dem Ergebnis die Stelle im Foto (aus dem Gitter), dort
    /// in der Karte lesen. Stärke wie bei den Autoren: min(1, 0,4 / mittlere Helligkeit) — helle Fotos weniger, damit nichts ausbrennt.
    /// </summary>
    private static DewarpShading Straighten(Light light, DewarpGrid grid, int w, int h, int x0, int y0, int x1, int y1, bool on)
    {
        var map = new DewarpMap(grid.Nx, grid.Ny, grid.Sx, grid.Sy, grid.RelWidth, grid.RelHeight);
        double cw = x1 - x0, ch = y1 - y0;
        var rgb = new float[ShadingWidth * ShadingHeight * 3];
        Parallel.For(0, ShadingHeight, b =>
        {
            for (var a = 0; a < ShadingWidth; a++)
            {
                var (rx, ry) = map.Source(a / (double)(ShadingWidth - 1), b / (double)(ShadingHeight - 1));
                // Foto (Pixelkanten) → Karte (Pixelmitten)
                var mx = (rx * w - x0) / cw * light.Width - 0.5;
                var my = (ry * h - y0) / ch * light.Height - 0.5;
                var k = (b * ShadingWidth + a) * 3;
                for (var c = 0; c < 3; c++) rgb[k + c] = Bilinear(light.Rgb, light.Width, light.Height, c, mx, my);
            }
        });
        var strength = Math.Min(1, 0.4 / Math.Max(light.Brightness, 1e-6));
        return new DewarpShading(ShadingWidth, ShadingHeight, rgb, strength, on);
    }

    private static float Bilinear(float[] planes, int w, int h, int c, double x, double y)
    {
        x = Math.Clamp(x, 0, w - 1);
        y = Math.Clamp(y, 0, h - 1);
        int ix = Math.Min((int)x, w - 2), iy = Math.Min((int)y, h - 2);
        float ax = (float)(x - ix), ay = (float)(y - iy);
        var o = c * w * h;
        float At(int i, int j) => planes[o + j * w + i];
        return (At(ix, iy) * (1 - ax) + At(ix + 1, iy) * ax) * (1 - ay) + (At(ix, iy + 1) * (1 - ax) + At(ix + 1, iy + 1) * ax) * ay;
    }

    /// <summary>
    /// Ausschnitt des Bildes → Modell (Eingabe inW × inH) → Gitter als Stellen im Bild (Pixelkanten: 0 und w sind die Ränder),
    /// mit <paramref name="shadow"/> auch die Schattenkarte (0 … 1, je 4 × 4 gemittelt).
    /// </summary>
    private static ((double X, double Y)[,] Grid, Light? Light) Predict(InferenceSession session, MagickImage doc,
        int x0, int y0, int x1, int y1, int inW, int inH, bool shadow, CancellationToken ct)
    {
        // Ausschnitt, verkleinert mit Dreiecksfilter (wie das Verkleinern mit Vorfilter beim Training)
        using var crop = ImageOps.CloneOf(doc);
        crop.Crop(new MagickGeometry(x0, y0, (uint)(x1 - x0), (uint)(y1 - y0)));
        crop.ResetPage();
        using var small = ImageOps.Resize(crop, inW, inH, ResampleFilter.Bilinear, gammaCorrect: false);
        var px = ImageOps.ToDisplayPixels(small);   // B, G, R, A, 0 … 1
        var t = new float[3 * inH * inW];
        try
        {
            Parallel.For(0, inH, j =>
            {
                for (var i = 0; i < inW; i++)
                {
                    var k = (j * inW + i) * 4;
                    t[j * inW + i] = px[k + 2] * 2 - 1;                     // R, auf −1 … 1
                    t[(inH + j) * inW + i] = px[k + 1] * 2 - 1;             // G
                    t[(2 * inH + j) * inW + i] = px[k] * 2 - 1;             // B
                }
            });
        }
        finally
        {
            PixelPool.Return(px);
        }
        ct.ThrowIfCancellationRequested();
        using var value = OrtValue.CreateTensorValueFromMemory(t, [1, 3, inH, inW]);
        var outputs = OnnxSessions.Run(session, new Dictionary<string, OrtValue> { [session.InputMetadata.Keys.First()] = value },
            shadow ? ["bm_map", "shadow_map"] : ["bm_map"], ct);
        var bm = outputs[0];
        Light? light = null;
        if (shadow)
        {
            // −1 … 1 → 0 … 1, je 4 × 4 Pixel gemittelt (das Raster im Ergebnis ist ohnehin ein Viertel so fein)
            int lw = inW / 4, lh = inH / 4;
            var raw = outputs[1];
            var light4 = new float[3 * lw * lh];
            Parallel.For(0, 3 * lh, row =>
            {
                int c = row / lh, j = row % lh;
                for (var i = 0; i < lw; i++)
                {
                    double s = 0;
                    for (var dy = 0; dy < 4; dy++)
                    for (var dx = 0; dx < 4; dx++) s += raw[(c * inH + j * 4 + dy) * inW + i * 4 + dx];
                    light4[(c * lh + j) * lw + i] = (float)(s / 16 * 0.5 + 0.5);
                }
            });
            double sum = 0;
            foreach (var v in t) sum += v;
            light = new Light(light4, lw, lh, (sum / t.Length + 1) / 2);
        }
        var grid = new (double X, double Y)[GridHeight, GridWidth];
        double cw = x1 - x0, ch = y1 - y0;
        for (var j = 0; j < GridHeight; j++)
        for (var i = 0; i < GridWidth; i++)
        {
            double vx = bm[j * GridWidth + i], vy = bm[GridHeight * GridWidth + j * GridWidth + i];
            // wie beim Modell angewendet (align_corners = True): ±1 sind die Mitten der äußersten Pixel des Ausschnitts
            grid[j, i] = (x0 + 0.5 + (vx + 1) / 2 * (cw - 1), y0 + 0.5 + (vy + 1) / 2 * (ch - 1));
        }
        return (grid, light);
    }

    private static DewarpGrid ToGrid((double X, double Y)[,] grid, int w, int h, double focal35)
    {
        static double Dist((double X, double Y) a, (double X, double Y) b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
        // Größe: die längste Gitterzeile in Bildpixeln — am besten abgetasteten Rand ein Ergebnispixel je Bildpixel
        double rows = 0, cols = 0, longest = 0;
        for (var j = 0; j < GridHeight; j++)
        {
            double s = 0;
            for (var i = 0; i + 1 < GridWidth; i++) s += Dist(grid[j, i], grid[j, i + 1]);
            rows += s;
            longest = Math.Max(longest, s);
        }
        for (var i = 0; i < GridWidth; i++)
        for (var j = 0; j + 1 < GridHeight; j++) cols += Dist(grid[j, i], grid[j + 1, i]);
        var arcAspect = (cols / GridWidth) / Math.Max(1e-9, rows / GridHeight);   // Höhe / Breite, wie das Foto es zeigt

        // Seitenverhältnis aus dem Blattmodell: 9 Gitterzeilen und 4 -spalten als Linien, die äußeren gelten als Blattkanten
        var lines = new List<WarpLine>();
        foreach (var j in Enumerable.Range(0, 9).Select(k => (int)Math.Round(k * (GridHeight - 1) / 8.0)))
            lines.Add(new WarpLine(WarpLineKind.Horizontal,
                Enumerable.Range(0, GridWidth).Where(i => i % 11 == 0 || i == GridWidth - 1).Select(i => (grid[j, i].X / w, grid[j, i].Y / h)).ToArray()));
        foreach (var i in Enumerable.Range(0, 4).Select(k => (int)Math.Round(k * (GridWidth - 1) / 3.0)))
            lines.Add(new WarpLine(WarpLineKind.Vertical,
                Enumerable.Range(0, GridHeight).Where(j => j % 8 == 0 || j == GridHeight - 1).Select(j => (grid[j, i].X / w, grid[j, i].Y / h)).ToArray()));
        var model = Dewarp.MapFor(new DewarpSpec(lines, focal35), w, h);
        var (aspect, din) = model is { PageAspect: { } a } ? (a, model.Din) : (arcAspect, false);

        var sx = new float[GridHeight * GridWidth];
        var sy = new float[GridHeight * GridWidth];
        for (var j = 0; j < GridHeight; j++)
        for (var i = 0; i < GridWidth; i++)
            (sx[j * GridWidth + i], sy[j * GridWidth + i]) = ((float)(grid[j, i].X / w), (float)(grid[j, i].Y / h));
        var relWidth = longest / w;
        return new DewarpGrid(GridWidth, GridHeight, sx, sy, relWidth, relWidth * aspect, aspect, din);
    }
}
