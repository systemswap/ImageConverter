namespace ImageConverter.Core.Printing;

/// <summary>Specifies how images are distributed over the pages.</summary>
public enum PackingMode
{
    /// <summary>
    /// Several images per page, arranged so that each page can be divided by straight edge-to-edge cuts
    /// (guillotine method); best suited for cutting out.
    /// </summary>
    CutFriendly,

    /// <summary>Several images per page, packed as densely as possible (MaxRects method).</summary>
    Dense,

    /// <summary>Each image on its own page, centred.</summary>
    OnePerPage,
}

/// <summary>Page orientation; <see cref="Auto"/> chooses the better one per layout or per image.</summary>
public enum PageOrientation { Portrait, Landscape, Auto }

/// <summary>Paper size in millimetres (portrait).</summary>
/// <param name="Name">Display name; translated through <see cref="Tr.T(string)"/>.</param>
/// <param name="WidthMm">Width in millimetres.</param>
/// <param name="HeightMm">Height in millimetres.</param>
public sealed record PaperSize(string Name, double WidthMm, double HeightMm)
{
    /// <summary>Returns the translated display name.</summary>
    /// <returns>The name in the current language.</returns>
    public override string ToString() => Tr.T(Name);   // "Foto 10 × 15 cm" is translated, "A4" stays as it is

    /// <summary>Common paper sizes offered for selection.</summary>
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

/// <summary>An image with its print size in millimetres.</summary>
/// <param name="Id">Identifier of the image; returned in <see cref="Placement.ItemId"/> and <see cref="LayoutResult.NotPlaced"/>.</param>
/// <param name="WidthMm">Print width in millimetres.</param>
/// <param name="HeightMm">Print height in millimetres.</param>
public sealed record PrintItem(int Id, double WidthMm, double HeightMm);

/// <summary>Settings for <see cref="PrintLayout.Arrange"/>.</summary>
/// <param name="Paper">Paper size.</param>
/// <param name="Orientation">Page orientation.</param>
/// <param name="MarginMm">Margin on every side of the page in millimetres.</param>
/// <param name="GapMm">Minimum gap between two images in millimetres; negative values count as 0.</param>
/// <param name="AllowRotation">Whether images may be rotated by 90°.</param>
/// <param name="Mode">Packing method.</param>
/// <param name="ShrinkToFit">Whether images that are too large for the printable area are scaled down; otherwise they are not placed.</param>
public sealed record LayoutOptions(
    PaperSize Paper,
    PageOrientation Orientation,
    double MarginMm,
    double GapMm,
    bool AllowRotation,
    PackingMode Mode,
    bool ShrinkToFit);

/// <summary>
/// Position of an image on the page (mm, origin at the top left of the page).
/// </summary>
/// <remarks>
/// Width and height are the printed dimensions: swapped for <see cref="Rotated"/> (rotated by 90°), and scaled
/// down for <see cref="Scale"/> &lt; 1 because the image would otherwise not fit on the page.
/// </remarks>
/// <param name="ItemId">Identifier of the <see cref="PrintItem"/>.</param>
/// <param name="X">Left edge in millimetres, including the margin.</param>
/// <param name="Y">Top edge in millimetres, including the margin.</param>
/// <param name="Width">Printed width in millimetres.</param>
/// <param name="Height">Printed height in millimetres.</param>
/// <param name="Rotated">Whether the image is rotated by 90°.</param>
/// <param name="Scale">Scale factor relative to the print size of the item (1 = unchanged).</param>
public sealed record Placement(int ItemId, double X, double Y, double Width, double Height, bool Rotated, double Scale);

/// <summary>One page of the layout.</summary>
/// <param name="WidthMm">Page width in millimetres, according to the orientation.</param>
/// <param name="HeightMm">Page height in millimetres, according to the orientation.</param>
/// <param name="Placements">The images on this page.</param>
public sealed record LayoutPage(double WidthMm, double HeightMm, IReadOnlyList<Placement> Placements);

/// <summary>Result of <see cref="PrintLayout.Arrange"/>.</summary>
/// <param name="Pages">The pages with their placements.</param>
/// <param name="NotPlaced">Identifiers of the images that could not be placed (too large, or invalid size).</param>
public sealed record LayoutResult(IReadOnlyList<LayoutPage> Pages, IReadOnlyList<int> NotPlaced);

/// <summary>
/// Distributes images over pages without overlap, with a minimum gap and within the margin.
/// </summary>
/// <remarks>
/// Based on J. Jylänki, "A Thousand Ways to Pack the Bin – A Practical Approach to Two-Dimensional
/// Rectangle Bin Packing" (2010).
/// <para>
/// Gap: every image is enlarged by the gap on the right and at the bottom, and so is the printable area.
/// Packing is done without spacing, so two images are always at least <see cref="LayoutOptions.GapMm"/>
/// apart and no image reaches into the margin.
/// </para>
/// </remarks>
public static class PrintLayout
{
    private const double Eps = 1e-6;

    /// <summary>Distributes the images over pages.</summary>
    /// <param name="items">Images with their print sizes.</param>
    /// <param name="o">Paper, margin, gap and packing method.</param>
    /// <returns>The pages, and the images that could not be placed.</returns>
    public static LayoutResult Arrange(IReadOnlyList<PrintItem> items, LayoutOptions o)
    {
        if (o.Mode == PackingMode.OnePerPage)
            return OnePerPage(items, o);

        if (o.Orientation != PageOrientation.Auto)
            return Pack(items, o, o.Orientation == PageOrientation.Landscape);

        // Automatic: the orientation with the better score (see below); portrait on a tie.
        var portrait = Pack(items, o, landscape: false);
        var landscape = Pack(items, o, landscape: true);
        return Score(landscape).CompareTo(Score(portrait)) < 0 ? landscape : portrait;

        // Fewer unplaced images before fewer pages before fewer rotated images.
        static (int, int, int) Score(LayoutResult r) =>
            (r.NotPlaced.Count, r.Pages.Count, r.Pages.Sum(p => p.Placements.Count(x => x.Rotated)));
    }

    private static (double W, double H) PageSize(PaperSize p, bool landscape) =>
        landscape ? (Math.Max(p.WidthMm, p.HeightMm), Math.Min(p.WidthMm, p.HeightMm))
                  : (Math.Min(p.WidthMm, p.HeightMm), Math.Max(p.WidthMm, p.HeightMm));

    // ------------------------------------------------------------ Packing

    // Sort orders that are tried in turn: "largest first" in different interpretations.
    private static readonly Comparison<(PrintItem Item, double W, double H, double Scale)>[] SortOrders =
    [
        (a, b) => Desc(a.W * a.H, b.W * b.H, Math.Max(a.W, a.H), Math.Max(b.W, b.H)),   // area
        (a, b) => Desc(Math.Max(a.W, a.H), Math.Max(b.W, b.H), a.W * a.H, b.W * b.H),   // longest side
        (a, b) => Desc(a.H, b.H, a.W, b.W),                                             // height
        (a, b) => Desc(a.W, b.W, a.H, b.H),                                             // width
    ];

    private static int Desc(double a1, double b1, double a2, double b2)
    {
        var c = b1.CompareTo(a1);
        return c != 0 ? c : b2.CompareTo(a2);
    }

    /// <summary>
    /// Packs with every sort order, with and without rotation (if allowed), and keeps the best result.
    /// </summary>
    /// <remarks>
    /// Single heuristics have outliers (for example, they rotate an image on a tie and the rest then fits
    /// worse). The best result has the fewest unplaced images, then the fewest pages, then the fewest rotated
    /// images, so that photos stay upright where possible.
    /// </remarks>
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
            // With the gap as a buffer on the right and at the bottom; the area is also larger by the gap.
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

            if (spot is not { } r) { notPlaced.Add(item.Id); continue; }   // should never happen after FitScale

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

    /// <summary>
    /// Scale at which an image fits into the area: 1 = fits as it is, &lt; 1 = scale down by this factor,
    /// <see langword="null"/> = does not fit at all (invalid size or empty area).
    /// </summary>
    internal static double? FitScale(double w, double h, double areaW, double areaH, bool allowRotation)
    {
        if (w <= 0 || h <= 0 || areaW <= 0 || areaH <= 0) return null;

        var straight = Math.Min(areaW / w, areaH / h);
        var turned = allowRotation ? Math.Min(areaW / h, areaH / w) : 0;
        var best = Math.Max(straight, turned);
        return best >= 1 - Eps ? 1 : best;
    }

    // ------------------------------------------------------------ One per page

    private static LayoutResult OnePerPage(IReadOnlyList<PrintItem> items, LayoutOptions o)
    {
        var pages = new List<LayoutPage>();
        var notPlaced = new List<int>();

        foreach (var item in items)
        {
            // Automatic: orient the page like the image.
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

            // Rotate only if the image does not fit at its original size and fits larger when rotated.
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

    // ------------------------------------------------------------ Packing methods

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
        /// <summary>Finds and occupies space for w × h (or rotated); <see langword="null"/> if it does not fit.</summary>
        Rect? Insert(double w, double h, bool allowRotation);
    }

    /// <summary>
    /// Guillotine: every placement splits a free rectangle into two with ONE straight cut.
    /// </summary>
    /// <remarks>
    /// Free areas are never merged, which guarantees that the page can be divided by edge-to-edge cuts.
    /// Selection: smallest leftover area (Best Area Fit); split: along the shorter leftover axis
    /// (Shorter Leftover Axis).
    /// </remarks>
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

            // One straight cut through the rest, along the shorter leftover axis.
            var restW = free.W - bestW;
            var restH = free.H - bestH;
            Rect right, bottom;
            if (restW <= restH)
            {
                // Horizontal cut across the full width.
                right = new Rect(free.X + bestW, free.Y, restW, bestH);
                bottom = new Rect(free.X, free.Y + bestH, free.W, restH);
            }
            else
            {
                // Vertical cut across the full height.
                right = new Rect(free.X + bestW, free.Y, restW, free.H);
                bottom = new Rect(free.X, free.Y + bestH, bestW, restH);
            }

            if (right.W > Eps && right.H > Eps) _free.Add(right);
            if (bottom.W > Eps && bottom.H > Eps) _free.Add(bottom);
            return placed;
        }
    }

    /// <summary>
    /// MaxRects: keeps all maximal free rectangles (which may overlap) and chooses the position with the
    /// smallest leftover on the shorter side (Best Short Side Fit).
    /// </summary>
    /// <remarks>Packs more densely than guillotine, but the page cannot always be divided by edge-to-edge cuts.</remarks>
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

            // Split every free rectangle that the new position intersects into up to four remainders.
            var next = new List<Rect>(_free.Count + 4);
            foreach (var f in _free)
            {
                if (!f.Intersects(placed)) { next.Add(f); continue; }

                if (placed.X > f.X + Eps) next.Add(new Rect(f.X, f.Y, placed.X - f.X, f.H));
                if (placed.Right < f.Right - Eps) next.Add(new Rect(placed.Right, f.Y, f.Right - placed.Right, f.H));
                if (placed.Y > f.Y + Eps) next.Add(new Rect(f.X, f.Y, f.W, placed.Y - f.Y));
                if (placed.Bottom < f.Bottom - Eps) next.Add(new Rect(f.X, placed.Bottom, f.W, f.Bottom - placed.Bottom));
            }

            // Remove rectangles that lie completely inside others (of two identical ones, keep the first).
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
