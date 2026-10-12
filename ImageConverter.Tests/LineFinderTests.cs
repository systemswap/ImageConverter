using ImageConverter.Core.Imaging;
using Xunit.Abstractions;

namespace ImageConverter.Tests;

/// <summary>
/// Linien vorschlagen: künstliche Seite (Blatt als Viereck auf braunem Grund, gebogene Zeilen aus „Buchstaben“ mit Ober- und
/// Unterlängen, ohne Schriftart), Grundlinien, Blattkanten und Falz bekannt.
/// </summary>
public class LineFinderTests(ITestOutputHelper output)
{
    private const int W = 1200, H = 900;
    private const double Letter = 14;   // Buchstabenhöhe in Pixeln

    private sealed class Page
    {
        public (double X, double Y)[] Corners = [(180, 90), (1030, 120), (1060, 820), (150, 800)];
        public double FoldX = double.NaN;      // Doppelseite: Falz bei x
        public bool Whole;                     // Blatt füllt das ganze Bild (kein Rand zu sehen)
        public double Bend = 18;               // Zeilen nach unten gebogen (Pixel in der Mitte)
        public bool Frame;                     // gezeichneter Rahmen (2 px) bei x 230 … 990, y 160 … 760, dazu eine Spalte bei x 610
        public List<double> Rows = [];         // Grundlinien (y am Zeilenanfang)

        public double Baseline(double row, double x) => row + Bend * Math.Sin(Math.PI * Math.Clamp((x - 260) / 700, 0, 1));
    }

    private static bool Inside((double X, double Y)[] q, double x, double y)
    {
        var inside = false;
        for (int i = 0, j = q.Length - 1; i < q.Length; j = i++)
            if ((q[i].Y > y) != (q[j].Y > y) && x < (q[j].X - q[i].X) * (y - q[i].Y) / (q[j].Y - q[i].Y) + q[i].X)
                inside = !inside;
        return inside;
    }

    private static float[] Render(Page page)
    {
        var px = new float[W * H * 4];
        var rnd = new Random(7);
        for (var y = 0; y < H; y++)
        for (var x = 0; x < W; x++)
        {
            var i = (y * W + x) * 4;
            var onPage = page.Whole || Inside(page.Corners, x + 0.5, y + 0.5);
            float r = 0.42f, g = 0.33f, b = 0.25f;
            if (onPage)
            {
                var v = 0.93f;
                if (double.IsFinite(page.FoldX)) v *= (float)(1 - 0.3 * Math.Exp(-Math.Pow((x - page.FoldX) / 7, 2)));
                (r, g, b) = (v, v, v * 0.98f);
            }
            var noise = (float)(rnd.NextDouble() - 0.5) * 0.02f;
            (px[i], px[i + 1], px[i + 2], px[i + 3]) = (b + noise, g + noise, r + noise, 1);
        }

        if (page.Frame)
        {
            void Box(int x0, int y0, int x1, int y1)
            {
                for (var y = y0; y < y1; y++)
                for (var x = x0; x < x1; x++)
                {
                    var i = (y * W + x) * 4;
                    px[i] = px[i + 1] = px[i + 2] = 0.15f;
                }
            }
            Box(230, 160, 990, 162); Box(230, 758, 990, 760); Box(230, 160, 232, 760); Box(988, 160, 990, 760); Box(609, 160, 611, 760);
        }

        // Zeilen: Wörter aus Kästchen, zwischen x 260 und 960 (bzw. nur bis vor den Falz und danach)
        for (var row = 0; row < 9; row++)
        {
            var y0 = 200 + row * 62.0;
            page.Rows.Add(y0);
            var x = 260.0;
            while (x < 960)
            {
                if (double.IsFinite(page.FoldX) && Math.Abs(x - page.FoldX) < 40) { x += 80; continue; }
                var letters = rnd.Next(3, 9);
                for (var k = 0; k < letters && x < 960; k++)
                {
                    var lw = Letter * (0.45 + 0.4 * rnd.NextDouble());
                    var kind = rnd.NextDouble();
                    double top = kind < 0.25 ? 1.45 * Letter : Letter, bottom = kind > 0.9 ? -0.45 * Letter : 0;
                    var baseY = page.Baseline(y0, x + lw / 2);
                    for (var yy = (int)(baseY - top); yy < (int)(baseY - bottom); yy++)
                    for (var xx = (int)x; xx < (int)(x + lw); xx++)
                    {
                        var i = (yy * W + xx) * 4;
                        px[i] = px[i + 1] = px[i + 2] = 0.12f;
                    }
                    x += lw + 0.28 * Letter;
                }
                x += 0.9 * Letter;
            }
        }
        return px;
    }

    [Fact]
    public void FindsTheTextLines_OnTheirBaseline()
    {
        var page = new Page();
        var found = LineFinder.Find(Render(page), W, H);
        var text = found.Where(f => f.Source == FoundLineSource.Text).ToList();
        output.WriteLine($"{text.Count} Zeilen, {found.Count(f => f.Source == FoundLineSource.Edge)} Kanten");
        Assert.InRange(text.Count, 6, 8);
        foreach (var line in text)
        {
            Assert.Equal(WarpLineKind.Horizontal, line.Kind);
            // alle Punkte auf derselben echten Grundlinie, höchstens 1 px daneben (gemessen 0,4 px; die Kästchen sind flach,
            // die Linie gebogen — ±1 px liegen schon im Testbild; Unterlängen dürfen sich häufen)
            var row = page.Rows.MinBy(r => Math.Abs(page.Baseline(r, line.Points[0].X) - line.Points[0].Y));
            var worst = line.Points.Max(p => Math.Abs(page.Baseline(row, p.X) - p.Y));
            output.WriteLine($"Zeile {row}: {line.Points.Count} Punkte, höchstens {worst:0.0} px daneben");
            Assert.True(worst < 1, $"Zeile bei {row}: {worst:0.0} px neben der Grundlinie");
            Assert.True(line.Points.Max(p => p.X) - line.Points.Min(p => p.X) > 500, "Zeile zu kurz");
        }
    }

    [Fact]
    public void FindsThePageEdges_NoFoldOnASinglePage()
    {
        var page = new Page();
        var found = LineFinder.Find(Render(page), W, H);
        var edges = found.Where(f => f.Source == FoundLineSource.Edge).ToList();
        Assert.Equal(4, edges.Count);
        Assert.Equal(2, edges.Count(e => e.Kind == WarpLineKind.Horizontal));
        Assert.Equal(2, edges.Count(e => e.Kind == WarpLineKind.Vertical));
        var c = page.Corners;
        foreach (var e in edges)
        {
            // Abstand jedes Punkts zur nächsten echten Kante
            var worst = e.Points.Max(p => Enumerable.Range(0, 4).Min(k => Distance(p, c[k], c[(k + 1) % 4])));
            Assert.True(worst < 2, $"Kante {e.Kind}: {worst:0.0} px daneben");
        }
        Assert.DoesNotContain(found, f => f.Source == FoundLineSource.Fold);
    }

    [Fact]
    public void FindsTheFold_OfADoublePage()
    {
        var page = new Page { FoldX = 605, Bend = 0 };
        var found = LineFinder.Find(Render(page), W, H);
        var fold = Assert.Single(found, f => f.Source == FoundLineSource.Fold);
        Assert.Equal(WarpLineKind.Fold, fold.Kind);
        Assert.True(fold.Points.Count >= 6);
        Assert.All(fold.Points, p => Assert.InRange(p.X, 600, 610));
    }

    [Fact]
    public void PageFillingThePhoto_HasNoEdges()
    {
        var page = new Page { Whole = true };
        var found = LineFinder.Find(Render(page), W, H);
        Assert.DoesNotContain(found, f => f.Source == FoundLineSource.Edge);
        Assert.Contains(found, f => f.Source == FoundLineSource.Text);
    }

    [Fact]
    public void FindsDrawnLines_AlsoVertical()
    {
        var page = new Page { Frame = true, Bend = 0 };
        var found = LineFinder.Find(Render(page), W, H);
        var ruled = found.Where(f => f.Source == FoundLineSource.Ruled).ToList();
        output.WriteLine(string.Join("; ", ruled.Select(r => $"{r.Kind} {r.Points.Average(p => p.X):0},{r.Points.Average(p => p.Y):0}")));

        // die drei senkrechten Striche (Rahmen links, rechts, Spalte), auf ± 1,5 px ihrer Mitte
        foreach (var x in new[] { 231.0, 610.0, 989.0 })
        {
            var line = Assert.Single(ruled, r => r.Kind == WarpLineKind.Vertical && Math.Abs(r.Points.Average(p => p.X) - x) < 6);
            Assert.All(line.Points, p => Assert.InRange(p.X, x - 1.5, x + 1.5));
        }
        // Ober- und Unterkante des Rahmens
        foreach (var y in new[] { 161.0, 759.0 })
            Assert.Contains(ruled, r => r.Kind == WarpLineKind.Horizontal && r.Points.All(p => Math.Abs(p.Y - y) < 1.5));
    }

    [Fact]
    public void Refine_PutsAnEdgeOnTheStepInFullResolution()
    {
        // Sprung von dunkel nach hell bei x = 300,25 (Unterpixel), vorgeschlagen 3 px daneben
        const int w = 600, h = 400;
        var gray = new float[w * h];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
            gray[y * w + x] = (float)Math.Clamp(x + 0.5 - 300.25 + 0.5, 0, 1) * 0.6f + 0.2f;
        var edge = new FoundLine(WarpLineKind.Vertical, FoundLineSource.Edge,
            Enumerable.Range(0, 9).Select(i => (303.0, 40.0 + 40 * i)).ToArray());
        var refined = Assert.Single(LineFinder.Refine([edge], gray, w, h, workScale: 0.4));
        Assert.All(refined.Points, p => Assert.InRange(p.X, 299.9, 300.6));
    }

    private static double Distance((double X, double Y) p, (double X, double Y) a, (double X, double Y) b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y, len2 = dx * dx + dy * dy;
        var t = Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len2, 0, 1);
        return Math.Sqrt(Math.Pow(p.X - a.X - t * dx, 2) + Math.Pow(p.Y - a.Y - t * dy, 2));
    }
}
