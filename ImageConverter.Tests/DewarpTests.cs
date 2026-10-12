using ImageConverter.Core.Imaging;
using Xunit.Abstractions;

namespace ImageConverter.Tests;

/// <summary>
/// Gewölbtes Blatt gerade ziehen: künstliche Blätter mit bekannter Form (wie die Testbilder aus Woelbung.cs), daraus
/// Punkte auf Linien wie beim Nachzeichnen — nach dem Geradeziehen müssen die Linien gerade sein, auch zwischen den
/// gesetzten Punkten, und das Seitenverhältnis muss stimmen.
/// </summary>
public class DewarpTests(ITestOutputHelper output)
{
    private const int W = 1800, H = 2400;

    /// <summary>Blatt im Raum, schräg fotografiert: Bogenlänge 0 … Seiten, Höhe 0 … aspect.</summary>
    private sealed class Sheet
    {
        public double Aspect = Math.Sqrt(2);
        public bool Double;
        public double RotX = 24, RotY = -12, RotZ = 4, Dist = 1.85;
        public Func<double, double> Height = c => 0.16 * Math.Sin(Math.PI * c) + 0.06 * Math.Pow(c, 5);
        public Func<double, double, double>? Wave;   // zusätzlich (c, t) → z, nicht abwickelbar
        public int ImageW = W, ImageH = H;   // Doppelseite quer
        public double Focal = 1.15 * W;

        private double[]? _arc;
        private double _chord;

        private void Prepare()
        {
            if (_arc is not null) return;
            const int n = 4000;
            double Arc(double u)
            {
                double len = 0;
                for (var i = 0; i < n; i++)
                    len += Math.Sqrt(Math.Pow(u / n, 2) + Math.Pow(Height((i + 1.0) / n) - Height((double)i / n), 2));
                return len;
            }
            double lo = 0.3, hi = 1;
            for (var k = 0; k < 50; k++) { var m = (lo + hi) / 2; if (Arc(m) > 1) hi = m; else lo = m; }
            _chord = (lo + hi) / 2;
            _arc = new double[n + 1];
            for (var i = 1; i <= n; i++)
                _arc[i] = _arc[i - 1] + Math.Sqrt(Math.Pow(_chord / n, 2) + Math.Pow(Height((double)i / n) - Height((i - 1.0) / n), 2));
        }

        private double ChordAt(double s)
        {
            var arc = _arc!;
            s = Math.Clamp(s, 0, arc[^1]);
            int a = 0, b = arc.Length - 1;
            while (b - a > 1) { var m = (a + b) / 2; if (arc[m] > s) b = m; else a = m; }
            return (a + (arc[b] > arc[a] ? (s - arc[a]) / (arc[b] - arc[a]) : 0)) / (arc.Length - 1);
        }

        /// <summary>Blattpunkt (Bogenlänge s, Höhe t) → Bild, relativ (x / Breite, y / Höhe).</summary>
        public (double X, double Y) Image(double s, double t)
        {
            Prepare();
            double x, z;
            if (!Double)
            {
                var c = ChordAt(s);
                x = _chord * c - _chord / 2;
                z = Height(c) + (Wave?.Invoke(c, t / Aspect) ?? 0);
            }
            else
            {
                var d = s < 1 ? 1 - s : s - 1;
                var c = ChordAt(d);
                x = (s < 1 ? -1 : 1) * _chord * c;
                z = Height(c);
            }
            double px = x, py = t - Aspect / 2, pz = -z;
            var (ax, ay, az) = (RotX * Math.PI / 180, RotY * Math.PI / 180, RotZ * Math.PI / 180);
            // R = Rz · Ry · Rx
            (py, pz) = (Math.Cos(ax) * py - Math.Sin(ax) * pz, Math.Sin(ax) * py + Math.Cos(ax) * pz);
            (px, pz) = (Math.Cos(ay) * px + Math.Sin(ay) * pz, -Math.Sin(ay) * px + Math.Cos(ay) * pz);
            (px, py) = (Math.Cos(az) * px - Math.Sin(az) * py, Math.Sin(az) * px + Math.Cos(az) * py);
            pz += Dist;
            return ((ImageW / 2.0 + Focal * px / pz) / ImageW, (ImageH / 2.0 + Focal * py / pz) / ImageH);
        }

        public double Focal35 => Focal * 43.27 / Math.Sqrt(ImageW * (double)ImageW + ImageH * (double)ImageH);

        public double Pages => Double ? 2 : 1;
    }

    private static WarpLine Horizontal(Sheet sheet, double t, int points, double from = 0.03, double to = 0.97) =>
        new(WarpLineKind.Horizontal, Enumerable.Range(0, points)
            .Select(i => sheet.Image((from + (to - from) * i / (points - 1)) * sheet.Pages, t * sheet.Aspect)).ToArray());

    private static WarpLine Vertical(Sheet sheet, double s, int points, WarpLineKind kind = WarpLineKind.Vertical) =>
        new(kind, Enumerable.Range(0, points).Select(i => sheet.Image(s, (0.03 + 0.94 * i / (points - 1)) * sheet.Aspect)).ToArray());

    /// <summary>
    /// Größte Abweichung von der Geraden im Ergebnis, in Tausendsteln der Ergebnisbreite — an vielen Stellen der echten
    /// Linie, nicht nur an den gesetzten Punkten.
    /// </summary>
    private static double Bend(DewarpMap map, Func<double, (double X, double Y)> along)
    {
        var pts = Enumerable.Range(0, 41).Select(i => along(i / 40.0)).Select(p => map.Result(p.X, p.Y))
            .Select(r => (X: r.U * map.RelWidth, Y: r.V * map.RelHeight)).ToArray();
        var (a, b) = (pts[0], pts[^1]);
        double lx = b.X - a.X, ly = b.Y - a.Y, len = Math.Sqrt(lx * lx + ly * ly);
        return pts.Max(p => Math.Abs((p.X - a.X) * ly - (p.Y - a.Y) * lx) / len) / map.RelWidth * 1000;
    }

    private DewarpMap Solve(Sheet sheet, params WarpLine[] lines)
    {
        var map = Dewarp.Solve(new DewarpSpec(lines, sheet.Focal35), (double)sheet.ImageH / sheet.ImageW);
        Assert.NotNull(map);
        output.WriteLine($"Modell {map.Modelled}, Fehler {map.ModelError:0.00}, Seite {map.PageAspect:0.000}, DIN {map.Din}, " +
                         $"Ergebnis {map.SizeFor(sheet.ImageW).Width} × {map.SizeFor(sheet.ImageW).Height}");
        return map;
    }

    [Fact]
    public void CurvedSheet_TextLinesBecomeStraight()
    {
        var sheet = new Sheet();
        // vier Textzeilen mit je 9 Punkten, dazu zwei Seitenkanten
        var map = Solve(sheet,
            Horizontal(sheet, 0.12, 9), Horizontal(sheet, 0.35, 9), Horizontal(sheet, 0.6, 9), Horizontal(sheet, 0.88, 9),
            Vertical(sheet, 0.02, 5), Vertical(sheet, 0.98, 5));
        Assert.True(map.Modelled);

        foreach (var t in new[] { 0.12, 0.25, 0.5, 0.75, 0.88 })   // auch Zeilen, die nicht nachgezeichnet sind
        {
            var bend = Bend(map, f => sheet.Image(0.05 + 0.9 * f, t * sheet.Aspect));
            output.WriteLine($"Zeile {t}: {bend:0.00} ‰");
            Assert.True(bend < 3, $"Zeile bei {t} noch um {bend:0.00} ‰ gebogen");
        }
        var column = Bend(map, f => sheet.Image(0.5, (0.05 + 0.9 * f) * sheet.Aspect));
        Assert.True(column < 3, $"Spalte in der Mitte um {column:0.00} ‰ gebogen");
    }

    [Fact]
    public void FramedSheet_FindsTheAspectRatio_AndSnapsToDin()
    {
        var a4 = new Sheet();
        var map = Solve(a4, Horizontal(a4, 0.0, 9, 0, 1), Horizontal(a4, 1.0, 9, 0, 1), Vertical(a4, 0, 6), Vertical(a4, 1, 6),
            Horizontal(a4, 0.5, 9, 0, 1));
        Assert.True(map.Din);
        Assert.Equal(Math.Sqrt(2), map.PageAspect!.Value, 6);

        // kein DIN-Format: nicht einrasten, aber gut schätzen
        var odd = new Sheet { Aspect = 1.2, RotX = 18, RotY = 10 };
        var other = Solve(odd, Horizontal(odd, 0.0, 9, 0, 1), Horizontal(odd, 1.0, 9, 0, 1), Vertical(odd, 0, 6), Vertical(odd, 1, 6),
            Horizontal(odd, 0.5, 9, 0, 1));
        Assert.False(other.Din);
        Assert.InRange(other.PageAspect!.Value, 1.2 * 0.95, 1.2 * 1.05);
    }

    [Fact]
    public void DoublePage_BothPagesBecomeFlat()
    {
        var book = new Sheet { Double = true, Height = d => 0.22 * Math.Sqrt(d) * (1.25 - 0.6 * d), RotX = 22, RotY = 6, RotZ = -3, Dist = 2.3, ImageW = H, ImageH = W };
        var map = Solve(book,
            Horizontal(book, 0.1, 15), Horizontal(book, 0.5, 15), Horizontal(book, 0.9, 15),
            Vertical(book, 1, 6, WarpLineKind.Fold), Vertical(book, 0.03, 5), Vertical(book, 1.97, 5));
        Assert.True(map.Modelled && map.DoublePage);
        foreach (var t in new[] { 0.1, 0.3, 0.7 })
        {
            var left = Bend(map, f => book.Image(0.05 + 0.9 * f, t * book.Aspect));
            var right = Bend(map, f => book.Image(1.05 + 0.9 * f, t * book.Aspect));
            output.WriteLine($"Zeile {t}: links {left:0.00} ‰, rechts {right:0.00} ‰");
            Assert.True(left < 4 && right < 4, $"Zeile {t}: links {left:0.00} ‰, rechts {right:0.00} ‰");
        }
    }

    [Fact]
    public void WavySheet_TheGridStraightensWhatTheModelMisses()
    {
        var wavy = new Sheet
        {
            RotX = 17, RotY = 14, RotZ = -6, Height = c => 0.10 * Math.Sin(Math.PI * c),
            Wave = (c, t) => 0.012 * Math.Sin(2 * Math.PI * (1.6 * c + 0.8 * t)),
        };
        var lines = Enumerable.Range(0, 7).Select(k => Horizontal(wavy, 0.08 + 0.14 * k, 13)).ToArray();
        var map = Solve(wavy, lines);
        foreach (var k in Enumerable.Range(0, 7))
        {
            var t = 0.08 + 0.14 * k;
            var bend = Bend(map, f => wavy.Image(0.03 + 0.94 * f, t * wavy.Aspect));
            Assert.True(bend < 3, $"Zeile {t:0.00} noch um {bend:0.00} ‰ gebogen");
        }
    }

    [Fact]
    public void OneLine_OnlyThatLineIsStraightened_SizeStays()
    {
        var sheet = new Sheet { RotX = 0, RotY = 0, RotZ = 0 };
        var map = Solve(sheet, Horizontal(sheet, 0.5, 11));
        Assert.False(map.Modelled);
        var bend = Bend(map, f => sheet.Image(0.05 + 0.9 * f, 0.5 * sheet.Aspect));
        Assert.True(bend < 2, $"Linie noch um {bend:0.00} ‰ gebogen");
        var (w, h) = map.SizeFor(W);
        Assert.InRange(w, W * 0.9, W);
        Assert.InRange(h, H * 0.85, H);
    }

    [Fact]
    public void Recipe_DewarpComesFirst_PointsMapBothWays_StageCanBeHidden()
    {
        var sheet = new Sheet();
        var spec = new DewarpSpec([Horizontal(sheet, 0.2, 9), Horizontal(sheet, 0.8, 9), Vertical(sheet, 0.1, 5)], sheet.Focal35);
        var r = new GeometryRecipe(Turns: 1, Dewarp: spec);
        Assert.Equal(GeometryStages.Dewarp | GeometryStages.Orient, r.Present);

        var (fw, fh) = r.FlatSize(W, H);
        Assert.Equal((fh, fw), r.SizeFor(W, H));                                  // erst gerade ziehen, dann drehen
        Assert.Equal((H, W), r.SizeFor(W, H, GeometryStages.All & ~GeometryStages.Dewarp));   // Stufe aus: nur gedreht

        foreach (var (x, y) in new[] { (900.0, 1200.0), (700.0, 900.0), (1100.0, 1500.0) })
        {
            var (rx, ry) = r.ToResult(x, y, W, H);
            var (bx, by) = r.ToSource(rx, ry, W, H);
            Assert.True(Math.Abs(bx - x) < 0.5 && Math.Abs(by - y) < 0.5, $"{x},{y} → {rx:0.0},{ry:0.0} → {bx:0.0},{by:0.0}");
        }

        // Anwenden: Größe wie geplant (auch auf einer kleinen Fassung — die Linien sind relativ)
        using var small = new ImageMagick.MagickImage(ImageMagick.MagickColors.LightGray, W / 5, H / 5);
        using var result = r.Apply(small)!;
        Assert.Equal(r.SizeFor(W / 5, H / 5), ((int)result.Width, (int)result.Height));
    }

    [Fact]
    public void Outliers_OnlyWrongSuggestionsAreDeselected()
    {
        var sheet = new Sheet();
        // eine „Kante“, die in der Mitte 30 px nach oben ausbeult — so erkennt man manchmal einen unregelmäßigen Rand
        var bumped = new WarpLine(WarpLineKind.Horizontal, Enumerable.Range(0, 9).Select(i =>
        {
            var (x, y) = sheet.Image(0.03 + 0.94 * i / 8.0, 0.95 * sheet.Aspect);
            return (x, y - 30.0 / H * Math.Sin(Math.PI * i / 8.0));
        }).ToArray());
        WarpLine[] lines =
        [
            Horizontal(sheet, 0.1, 9), Horizontal(sheet, 0.3, 9), Horizontal(sheet, 0.5, 9), Horizontal(sheet, 0.7, 9),
            Vertical(sheet, 0.05, 6), Vertical(sheet, 0.95, 6), bumped,
        ];
        var spec = new DewarpSpec(lines, sheet.Focal35);

        var keep = Dewarp.RejectOutliers(spec, lines.Select(_ => true).ToArray(), W, H);
        Assert.Equal([true, true, true, true, true, true, false], keep);

        // von Hand gesetzt (kein Vorschlag): bleibt, auch wenn sie nicht passt
        var manual = Dewarp.RejectOutliers(spec, lines.Select((_, i) => i < 6).ToArray(), W, H);
        Assert.All(manual, Assert.True);
    }

    [Fact]
    public void Result_IsTheInverseOfSource()
    {
        var sheet = new Sheet();
        var map = Solve(sheet, Horizontal(sheet, 0.2, 9), Horizontal(sheet, 0.8, 9), Vertical(sheet, 0.1, 5));
        foreach (var (u, v) in new[] { (0.2, 0.3), (0.5, 0.5), (0.9, 0.1), (0.05, 0.95) })
        {
            var (x, y) = map.Source(u, v);
            var (bu, bv) = map.Result(x, y);
            Assert.Equal(u, bu, 4);
            Assert.Equal(v, bv, 4);
        }
    }
}
