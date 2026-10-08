namespace ImageConverter.Core.Printing;

/// <summary>Wie Bilder auf die Seiten verteilt werden.</summary>
public enum PackingMode
{
    /// <summary>
    /// Mehrere pro Seite, so dass jede Seite mit geraden Schnitten von Kante zu Kante
    /// zerlegt werden kann (Guillotine-Verfahren) — ideal zum Ausschneiden.
    /// </summary>
    CutFriendly,

    /// <summary>Mehrere pro Seite, möglichst dicht (MaxRects-Verfahren).</summary>
    Dense,

    /// <summary>Jedes Bild auf eine eigene Seite, mittig.</summary>
    OnePerPage,
}

public enum PageOrientation { Portrait, Landscape, Auto }

/// <summary>Papierformat in Millimetern (Hochformat).</summary>
public sealed record PaperSize(string Name, double WidthMm, double HeightMm)
{
    public override string ToString() => Tr.T(Name);   // „Foto 10 × 15 cm“ übersetzt, A4 bleibt

    public static IReadOnlyList<PaperSize> Common { get; } =
    [
        new("A4", 210, 297),
        new("A5", 148, 210),
        new("A3", 297, 420),
        new("Letter", 215.9, 279.4),
        new("Legal", 215.9, 355.6),
        new("Foto 10 × 15 cm", 102, 152),
        new("Foto 13 × 18 cm", 127, 178),
    ];
}

/// <summary>Ein Bild mit seiner Druckgröße in Millimetern.</summary>
public sealed record PrintItem(int Id, double WidthMm, double HeightMm);

public sealed record LayoutOptions(
    PaperSize Paper,
    PageOrientation Orientation,
    double MarginMm,
    double GapMm,
    bool AllowRotation,
    PackingMode Mode,
    bool ShrinkToFit);

/// <summary>
/// Lage eines Bildes auf der Seite (mm, Ursprung oben links). Breite/Höhe sind die
/// gedruckten Maße — bei <see cref="Rotated"/> um 90° gedreht, bei <see cref="Scale"/> &lt; 1
/// verkleinert, weil das Bild sonst nicht auf die Seite passt.
/// </summary>
public sealed record Placement(int ItemId, double X, double Y, double Width, double Height, bool Rotated, double Scale);

public sealed record LayoutPage(double WidthMm, double HeightMm, IReadOnlyList<Placement> Placements);

public sealed record LayoutResult(IReadOnlyList<LayoutPage> Pages, IReadOnlyList<int> NotPlaced);

/// <summary>
/// Verteilt Bilder auf Seiten — ohne Überlappung, mit Mindestabstand und innerhalb
/// des Rands. Grundlage: J. Jylänki, „A Thousand Ways to Pack the Bin" (2010).
///
/// Abstand: Jedes Bild wird rechts und unten um den Abstand vergrößert, die
/// bedruckbare Fläche ebenfalls. Gepackt wird ohne Lücken — dadurch liegen zwischen
/// zwei Bildern immer mindestens <see cref="LayoutOptions.GapMm"/>, und kein Bild
/// ragt in den Rand.
/// </summary>
public static class PrintLayout
{
    private const double Eps = 1e-6;

    public static LayoutResult Arrange(IReadOnlyList<PrintItem> items, LayoutOptions o)
    {
        if (o.Mode == PackingMode.OnePerPage)
            return OnePerPage(items, o);

        if (o.Orientation != PageOrientation.Auto)
            return Pack(items, o, o.Orientation == PageOrientation.Landscape);

        // Automatisch: die Ausrichtung mit weniger Seiten; bei Gleichstand Hochformat
        var portrait = Pack(items, o, landscape: false);
        var landscape = Pack(items, o, landscape: true);
        return Score(landscape).CompareTo(Score(portrait)) < 0 ? landscape : portrait;

        // Weniger nicht platzierbare Bilder vor weniger Seiten vor weniger gedrehten Bildern
        static (int, int, int) Score(LayoutResult r) =>
            (r.NotPlaced.Count, r.Pages.Count, r.Pages.Sum(p => p.Placements.Count(x => x.Rotated)));
    }

    private static (double W, double H) PageSize(PaperSize p, bool landscape) =>
        landscape ? (Math.Max(p.WidthMm, p.HeightMm), Math.Min(p.WidthMm, p.HeightMm))
                  : (Math.Min(p.WidthMm, p.HeightMm), Math.Max(p.WidthMm, p.HeightMm));

    // ------------------------------------------------------------ Packen

    // Sortierungen, die durchprobiert werden — „große zuerst" in verschiedenen Lesarten
    private static readonly Comparison<(PrintItem Item, double W, double H, double Scale)>[] SortOrders =
    [
        (a, b) => Desc(a.W * a.H, b.W * b.H, Math.Max(a.W, a.H), Math.Max(b.W, b.H)),   // Fläche
        (a, b) => Desc(Math.Max(a.W, a.H), Math.Max(b.W, b.H), a.W * a.H, b.W * b.H),   // längste Seite
        (a, b) => Desc(a.H, b.H, a.W, b.W),                                             // Höhe
        (a, b) => Desc(a.W, b.W, a.H, b.H),                                             // Breite
    ];

    private static int Desc(double a1, double b1, double a2, double b2)
    {
        var c = b1.CompareTo(a1);
        return c != 0 ? c : b2.CompareTo(a2);
    }

    /// <summary>
    /// Einzelne Heuristiken haben Ausreißer (z. B. drehen sie bei Gleichstand ein Bild,
    /// und der Rest passt danach schlechter). Deshalb mehrere Varianten rechnen und die
    /// beste nehmen: wenigste nicht platzierte Bilder, dann wenigste Seiten, dann
    /// wenigste gedrehte Bilder — Fotos bleiben möglichst aufrecht.
    /// </summary>
    private static LayoutResult Pack(IReadOnlyList<PrintItem> items, LayoutOptions o, bool landscape)
    {
        LayoutResult? best = null;
        (int, int, int) bestScore = (int.MaxValue, int.MaxValue, int.MaxValue);

        foreach (var rotate in o.AllowRotation ? new[] { false, true } : [false])
        foreach (var order in SortOrders)
        {
            var r = PackOnce(items, o, landscape, rotate, order);
            var score = (r.NotPlaced.Count, r.Pages.Count, r.Pages.Sum(p => p.Placements.Count(x => x.Rotated)));
            if (score.CompareTo(bestScore) < 0)
            {
                best = r;
                bestScore = score;
            }
        }

        return best!;
    }

    private static LayoutResult PackOnce(IReadOnlyList<PrintItem> items, LayoutOptions o, bool landscape,
        bool allowRotation, Comparison<(PrintItem Item, double W, double H, double Scale)> order)
    {
        var (pw, ph) = PageSize(o.Paper, landscape);
        var areaW = pw - 2 * o.MarginMm;
        var areaH = ph - 2 * o.MarginMm;
        var gap = Math.Max(0, o.GapMm);

        var notPlaced = new List<int>();
        var prepared = new List<(PrintItem Item, double W, double H, double Scale)>();

        foreach (var item in items)
        {
            if (FitScale(item.WidthMm, item.HeightMm, areaW, areaH, allowRotation) is not { } scale)
            {
                notPlaced.Add(item.Id);
                continue;
            }
            if (scale < 1 && !o.ShrinkToFit)
            {
                notPlaced.Add(item.Id);
                continue;
            }
            prepared.Add((item, item.WidthMm * scale, item.HeightMm * scale, scale));
        }

        prepared.Sort(order);

        var bins = new List<IBin>();
        var pages = new List<List<Placement>>();

        foreach (var (item, w, h, scale) in prepared)
        {
            // Mit Abstand als Puffer rechts/unten; Fläche ebenfalls um den Abstand größer
            var bw = w + gap;
            var bh = h + gap;

            Rect? spot = null;
            var pageIndex = -1;
            for (var i = 0; i < bins.Count && spot is null; i++)
            {
                spot = bins[i].Insert(bw, bh, allowRotation);
                if (spot is not null) pageIndex = i;
            }

            if (spot is null)
            {
                IBin bin = o.Mode == PackingMode.Dense
                    ? new MaxRectsBin(areaW + gap, areaH + gap)
                    : new GuillotineBin(areaW + gap, areaH + gap);
                bins.Add(bin);
                pages.Add([]);
                pageIndex = bins.Count - 1;
                spot = bin.Insert(bw, bh, allowRotation);
            }

            if (spot is not { } r) { notPlaced.Add(item.Id); continue; }   // sollte nach FitScale nie passieren

            var rotated = Math.Abs(r.W - bw) > Eps;
            var placedW = rotated ? h : w;
            var placedH = rotated ? w : h;
            pages[pageIndex].Add(new Placement(item.Id,
                o.MarginMm + r.X, o.MarginMm + r.Y, placedW, placedH, rotated, scale));
        }

        return new LayoutResult(
            pages.Select(p => new LayoutPage(pw, ph, p)).ToList(),
            notPlaced);
    }

    /// <summary>1 = passt, &lt; 1 = so stark verkleinern, null = passt gar nicht (Fläche zu klein).</summary>
    internal static double? FitScale(double w, double h, double areaW, double areaH, bool allowRotation)
    {
        if (w <= 0 || h <= 0 || areaW <= 0 || areaH <= 0) return null;

        var straight = Math.Min(areaW / w, areaH / h);
        var turned = allowRotation ? Math.Min(areaW / h, areaH / w) : 0;
        var best = Math.Max(straight, turned);
        return best >= 1 - Eps ? 1 : best;
    }

    // ------------------------------------------------------------ Eins pro Seite

    private static LayoutResult OnePerPage(IReadOnlyList<PrintItem> items, LayoutOptions o)
    {
        var pages = new List<LayoutPage>();
        var notPlaced = new List<int>();

        foreach (var item in items)
        {
            // Automatisch: Seite so drehen wie das Bild
            var landscape = o.Orientation switch
            {
                PageOrientation.Landscape => true,
                PageOrientation.Portrait => false,
                _ => item.WidthMm > item.HeightMm,
            };
            var (pw, ph) = PageSize(o.Paper, landscape);
            var areaW = pw - 2 * o.MarginMm;
            var areaH = ph - 2 * o.MarginMm;

            if (areaW <= 0 || areaH <= 0 || item.WidthMm <= 0 || item.HeightMm <= 0)
            {
                notPlaced.Add(item.Id);
                continue;
            }

            // Drehen nur, wenn das Bild gedreht größer (bzw. überhaupt in Originalgröße) passt
            var straight = Math.Min(areaW / item.WidthMm, areaH / item.HeightMm);
            var turned = o.AllowRotation ? Math.Min(areaW / item.HeightMm, areaH / item.WidthMm) : 0;
            var rotated = straight < 1 - Eps && turned > straight + Eps;
            var scale = Math.Min(1, rotated ? turned : straight);

            if (scale < 1 - Eps && !o.ShrinkToFit)
            {
                notPlaced.Add(item.Id);
                continue;
            }

            var w = item.WidthMm * scale;
            var h = item.HeightMm * scale;
            if (rotated) (w, h) = (h, w);

            pages.Add(new LayoutPage(pw, ph,
            [
                new Placement(item.Id, o.MarginMm + (areaW - w) / 2, o.MarginMm + (areaH - h) / 2, w, h, rotated, scale),
            ]));
        }

        return new LayoutResult(pages, notPlaced);
    }

    // ------------------------------------------------------------ Verfahren

    internal readonly record struct Rect(double X, double Y, double W, double H)
    {
        public double Right => X + W;
        public double Bottom => Y + H;

        public bool Contains(Rect o) =>
            o.X >= X - Eps && o.Y >= Y - Eps && o.Right <= Right + Eps && o.Bottom <= Bottom + Eps;

        public bool Intersects(Rect o) =>
            o.X < Right - Eps && o.Right > X + Eps && o.Y < Bottom - Eps && o.Bottom > Y + Eps;
    }

    private interface IBin
    {
        /// <summary>Platz für w × h (oder gedreht) suchen und belegen; null = passt nicht.</summary>
        Rect? Insert(double w, double h, bool allowRotation);
    }

    /// <summary>
    /// Guillotine: Jede Platzierung teilt ein freies Rechteck mit EINEM geraden Schnitt
    /// in zwei. Ohne Zusammenlegen freier Flächen — so bleibt garantiert, dass die
    /// Seite mit durchgehenden Schnitten zerlegbar ist.
    /// Auswahl: kleinste Restfläche (Best Area Fit); Schnitt: entlang der kürzeren
    /// Restachse (Shorter Leftover Axis).
    /// </summary>
    private sealed class GuillotineBin(double width, double height) : IBin
    {
        private readonly List<Rect> _free = [new Rect(0, 0, width, height)];

        public Rect? Insert(double w, double h, bool allowRotation)
        {
            var bestIndex = -1;
            var bestFit = double.MaxValue;
            var bestShort = double.MaxValue;
            var bestW = 0.0;
            var bestH = 0.0;

            for (var i = 0; i < _free.Count; i++)
            {
                var f = _free[i];
                Try(w, h);
                if (allowRotation) Try(h, w);

                void Try(double cw, double ch)
                {
                    if (cw > f.W + Eps || ch > f.H + Eps) return;
                    var areaFit = f.W * f.H - cw * ch;
                    var shortFit = Math.Min(f.W - cw, f.H - ch);
                    if (areaFit < bestFit - Eps || (Math.Abs(areaFit - bestFit) <= Eps && shortFit < bestShort))
                    {
                        bestIndex = i; bestFit = areaFit; bestShort = shortFit; bestW = cw; bestH = ch;
                    }
                }
            }

            if (bestIndex < 0) return null;

            var free = _free[bestIndex];
            _free.RemoveAt(bestIndex);
            var placed = new Rect(free.X, free.Y, bestW, bestH);

            // Ein gerader Schnitt durch den Rest: entlang der kürzeren Restachse
            var restW = free.W - bestW;
            var restH = free.H - bestH;
            Rect right, bottom;
            if (restW <= restH)
            {
                // Waagerechter Schnitt über die ganze Breite
                right = new Rect(free.X + bestW, free.Y, restW, bestH);
                bottom = new Rect(free.X, free.Y + bestH, free.W, restH);
            }
            else
            {
                // Senkrechter Schnitt über die ganze Höhe
                right = new Rect(free.X + bestW, free.Y, restW, free.H);
                bottom = new Rect(free.X, free.Y + bestH, bestW, restH);
            }

            if (right.W > Eps && right.H > Eps) _free.Add(right);
            if (bottom.W > Eps && bottom.H > Eps) _free.Add(bottom);
            return placed;
        }
    }

    /// <summary>
    /// MaxRects: führt alle maximalen freien Rechtecke (dürfen sich überlappen) und
    /// wählt den Platz mit dem kleinsten Rest an der kürzeren Seite (Best Short Side Fit).
    /// Packt dichter als Guillotine, die Seite ist aber nicht immer durchgehend schneidbar.
    /// </summary>
    private sealed class MaxRectsBin(double width, double height) : IBin
    {
        private readonly List<Rect> _free = [new Rect(0, 0, width, height)];

        public Rect? Insert(double w, double h, bool allowRotation)
        {
            Rect? best = null;
            var bestShort = double.MaxValue;
            var bestLong = double.MaxValue;

            foreach (var f in _free)
            {
                Try(w, h);
                if (allowRotation) Try(h, w);

                void Try(double cw, double ch)
                {
                    if (cw > f.W + Eps || ch > f.H + Eps) return;
                    var leftW = f.W - cw;
                    var leftH = f.H - ch;
                    var shortSide = Math.Min(leftW, leftH);
                    var longSide = Math.Max(leftW, leftH);
                    if (shortSide < bestShort - Eps || (Math.Abs(shortSide - bestShort) <= Eps && longSide < bestLong))
                    {
                        best = new Rect(f.X, f.Y, cw, ch); bestShort = shortSide; bestLong = longSide;
                    }
                }
            }

            if (best is not { } placed) return null;

            // Jedes freie Rechteck, das der neue Platz schneidet, in bis zu vier Reste teilen
            var next = new List<Rect>(_free.Count + 4);
            foreach (var f in _free)
            {
                if (!f.Intersects(placed)) { next.Add(f); continue; }

                if (placed.X > f.X + Eps) next.Add(new Rect(f.X, f.Y, placed.X - f.X, f.H));
                if (placed.Right < f.Right - Eps) next.Add(new Rect(placed.Right, f.Y, f.Right - placed.Right, f.H));
                if (placed.Y > f.Y + Eps) next.Add(new Rect(f.X, f.Y, f.W, placed.Y - f.Y));
                if (placed.Bottom < f.Bottom - Eps) next.Add(new Rect(f.X, placed.Bottom, f.W, f.Bottom - placed.Bottom));
            }

            // Rechtecke entfernen, die vollständig in anderen liegen
            _free.Clear();
            for (var i = 0; i < next.Count; i++)
            {
                var contained = false;
                for (var j = 0; j < next.Count && !contained; j++)
                    contained = i != j && next[j].Contains(next[i]) && (!next[i].Contains(next[j]) || j < i);
                if (!contained) _free.Add(next[i]);
            }

            return placed;
        }
    }
}
