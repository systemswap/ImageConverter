using ImageConverter.Core.Imaging;
using Microsoft.ML.OnnxRuntime;

namespace ImageConverter.Core.Ai;

/// <summary>
/// „KI begradigen“ mit SyntheticDoc: das Modell sagt für ein fotografiertes Blatt die Rückabbildung voraus — ein Gitter von
/// 121 × 177 Stellen des flachen Blatts, je mit der Stelle im Foto. Zwei Läufe: erst das ganze Bild, dann der Ausschnitt um
/// das gefundene Blatt (+ 4 % je Seite), so sieht das Modell das Blatt groß. Das Seitenverhältnis kennt das Modell nicht:
/// es kommt aus unserem Blattmodell, dem Gitterzeilen und -spalten als Linien dienen (mit DIN-Einrasten), ersatzweise aus den
/// Bogenlängen des Gitters.
/// </summary>
public static class AiDewarp
{
    public const int InputWidth = 1024, InputHeight = 1440, GridWidth = 177, GridHeight = 121;

    /// <summary>
    /// Gitter für das Bild <paramref name="bgra"/> (B, G, R, A, 0 … 1; am besten höchstens etwa 1600 px) — relativ zur
    /// Bildgröße, also für jede Größe desselben Bildes gültig.
    /// </summary>
    public static DewarpGrid Run(InferenceSession session, float[] bgra, int w, int h, double focal35, CancellationToken ct)
    {
        var first = Predict(session, bgra, w, h, 0, 0, w, h, ct);
        // Blatt eingrenzen: Umriss des Gitters, 4 % Rand — nur, wenn das Blatt nicht ohnehin das Bild füllt
        double bx0 = double.MaxValue, by0 = double.MaxValue, bx1 = double.MinValue, by1 = double.MinValue;
        for (var j = 0; j < GridHeight; j++)
        for (var i = 0; i < GridWidth; i++)
        {
            if (j != 0 && j != GridHeight - 1 && i != 0 && i != GridWidth - 1) continue;
            var (x, y) = first[j, i];
            (bx0, bx1, by0, by1) = (Math.Min(bx0, x), Math.Max(bx1, x), Math.Min(by0, y), Math.Max(by1, y));
        }
        double mx = 0.04 * (bx1 - bx0), my = 0.04 * (by1 - by0);
        (bx0, by0) = (Math.Max(0, Math.Floor(bx0 - mx)), Math.Max(0, Math.Floor(by0 - my)));
        (bx1, by1) = (Math.Min(w, Math.Ceiling(bx1 + mx)), Math.Min(h, Math.Ceiling(by1 + my)));
        var grid = bx1 - bx0 >= 0.3 * w && by1 - by0 >= 0.3 * h && (bx1 - bx0 < 0.98 * w || by1 - by0 < 0.98 * h)
            ? Predict(session, bgra, w, h, bx0, by0, bx1, by1, ct)
            : first;
        return ToGrid(grid, w, h, focal35);
    }

    /// <summary>Ausschnitt (x0, y0 – x1, y1) des Bildes → Modell → Gitter als Stellen im Bild (Pixelkanten: 0 und w sind die Ränder).</summary>
    private static (double X, double Y)[,] Predict(InferenceSession session, float[] px, int w, int h,
        double x0, double y0, double x1, double y1, CancellationToken ct)
    {
        var t = new float[3 * InputHeight * InputWidth];
        Parallel.For(0, InputHeight, j =>
        {
            int ya = Math.Clamp((int)Math.Floor(y0 + j * (y1 - y0) / InputHeight), 0, h - 1);
            int yb = Math.Clamp((int)Math.Ceiling(y0 + (j + 1) * (y1 - y0) / InputHeight), ya + 1, h);
            for (var i = 0; i < InputWidth; i++)
            {
                int xa = Math.Clamp((int)Math.Floor(x0 + i * (x1 - x0) / InputWidth), 0, w - 1);
                int xb = Math.Clamp((int)Math.Ceiling(x0 + (i + 1) * (x1 - x0) / InputWidth), xa + 1, w);
                double r = 0, g = 0, b = 0;
                for (var y = ya; y < yb; y++)
                for (var x = xa; x < xb; x++)
                {
                    var k = (y * w + x) * 4;
                    b += px[k]; g += px[k + 1]; r += px[k + 2];
                }
                // Mittel über die Fläche des Zielpixels (wie das Verkleinern mit Vorfilter beim Training), RGB, auf −1 … 1
                var n = (double)(yb - ya) * (xb - xa);
                t[j * InputWidth + i] = (float)(r / n * 2 - 1);
                t[(InputHeight + j) * InputWidth + i] = (float)(g / n * 2 - 1);
                t[(2 * InputHeight + j) * InputWidth + i] = (float)(b / n * 2 - 1);
            }
        });
        ct.ThrowIfCancellationRequested();
        using var value = OrtValue.CreateTensorValueFromMemory(t, [1, 3, InputHeight, InputWidth]);
        var bm = OnnxSessions.Run(session, new Dictionary<string, OrtValue> { [session.InputMetadata.Keys.First()] = value }, ct);
        var grid = new (double X, double Y)[GridHeight, GridWidth];
        for (var j = 0; j < GridHeight; j++)
        for (var i = 0; i < GridWidth; i++)
        {
            double vx = bm[j * GridWidth + i], vy = bm[GridHeight * GridWidth + j * GridWidth + i];
            // wie beim Modell angewendet (align_corners = True): ±1 sind die Mitten der äußersten Pixel des Ausschnitts
            grid[j, i] = (x0 + 0.5 + (vx + 1) / 2 * (x1 - x0 - 1), y0 + 0.5 + (vy + 1) / 2 * (y1 - y0 - 1));
        }
        return grid;
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
