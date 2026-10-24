using ImageConverter.Core.Printing;
using Xunit.Abstractions;

namespace ImageConverter.Tests;

public class PrintLayoutTests(ITestOutputHelper output)
{
    private const double Eps = 1e-6;
    private static readonly PaperSize A4 = PaperSize.Common[0];

    private static LayoutOptions Options(PackingMode mode, PageOrientation orientation = PageOrientation.Auto,
        double margin = 5, double gap = 3, bool rotate = true, bool shrink = true) =>
        new(A4, orientation, margin, gap, rotate, mode, shrink);

    private static List<PrintItem> Many(int count, double w, double h) =>
        Enumerable.Range(1, count).Select(i => new PrintItem(i, w, h)).ToList();

    // ------------------------------------------------------------ Specific cases

    [Theory]
    [InlineData(PackingMode.CutFriendly)]
    [InlineData(PackingMode.Dense)]
    public void FourPhotos9x13_FitOnOneA4(PackingMode mode)
    {
        // 200 × 287 mm printable: 2 side by side (90+3+90), 2 one above the other (130+3+130)
        var r = PrintLayout.Arrange(Many(4, 90, 130), Options(mode));

        Assert.Single(r.Pages);
        Assert.Equal(4, r.Pages[0].Placements.Count);
    }

    [Fact]
    public void FourPhotos10x15_AutoOrientation_TwoPerPage()
    {
        // Portrait: only 1 per page (100+3+100 > 200). Landscape (287 × 200): 2 side by side.
        var r = PrintLayout.Arrange(Many(4, 100, 150), Options(PackingMode.CutFriendly));

        Assert.Equal(2, r.Pages.Count);
        Assert.True(r.Pages[0].WidthMm > r.Pages[0].HeightMm, "sollte Querformat wählen");
    }

    [Fact]
    public void Oversized_WithoutShrink_IsReported()
    {
        var r = PrintLayout.Arrange([new PrintItem(1, 297, 420)], Options(PackingMode.CutFriendly, shrink: false));

        Assert.Empty(r.Pages);
        Assert.Equal([1], r.NotPlaced);
    }

    [Fact]
    public void Oversized_WithShrink_FitsPrintableArea()
    {
        var r = PrintLayout.Arrange([new PrintItem(1, 297, 420)], Options(PackingMode.CutFriendly));

        var p = Assert.Single(Assert.Single(r.Pages).Placements);
        Assert.True(p.Scale < 1);
        Assert.True(p.Width <= 200 + Eps && p.Height <= 287 + Eps);
    }

    [Fact]
    public void OnePerPage_EachCentered_PageTurnsWithImage()
    {
        var items = new List<PrintItem> { new(1, 100, 150), new(2, 150, 100), new(3, 60, 60) };
        var r = PrintLayout.Arrange(items, Options(PackingMode.OnePerPage));

        Assert.Equal(3, r.Pages.Count);
        Assert.True(r.Pages[1].WidthMm > r.Pages[1].HeightMm);   // landscape image → landscape page
        foreach (var page in r.Pages)
        {
            var p = Assert.Single(page.Placements);
            Assert.Equal(page.WidthMm / 2, p.X + p.Width / 2, 6);
            Assert.Equal(page.HeightMm / 2, p.Y + p.Height / 2, 6);
        }
    }

    [Fact]
    public void Gap_IsRespected_EvenWhenItWouldOtherwiseFit()
    {
        // Without a gap, 2 × 100 mm would fit exactly into 200 mm — with a 3 mm gap they no longer do
        var r = PrintLayout.Arrange(Many(2, 100, 280), Options(PackingMode.CutFriendly, PageOrientation.Portrait));

        Assert.Equal(2, r.Pages.Count);
    }

    // ------------------------------------------------------------ Bulk test

    public static IEnumerable<object[]> RandomCases()
    {
        foreach (var mode in new[] { PackingMode.CutFriendly, PackingMode.Dense, PackingMode.OnePerPage })
        foreach (var rotate in new[] { true, false })
        foreach (var gap in new[] { 0.0, 3.0, 8.0 })
            yield return [mode, rotate, gap];
    }

    [Theory]
    [MemberData(nameof(RandomCases))]
    public void RandomMixes_NeverOverlap_RespectGapAndMargin_PlaceEachOnce(PackingMode mode, bool rotate, double gap)
    {
        var rng = new Random(4711 + (int)mode * 100 + (rotate ? 10 : 0) + (int)gap);
        var totalPages = 0;

        for (var run = 0; run < 150; run++)
        {
            var count = rng.Next(1, 26);
            var items = Enumerable.Range(1, count)
                .Select(i => new PrintItem(i, rng.Next(20, 301), rng.Next(20, 301)))
                .ToList();
            var margin = rng.Next(0, 16);
            var o = Options(mode, PageOrientation.Auto, margin, gap, rotate, shrink: rng.Next(2) == 0);

            var r = PrintLayout.Arrange(items, o);
            totalPages += r.Pages.Count;

            // Every image exactly once — placed or reported as "does not fit"
            var placedIds = r.Pages.SelectMany(p => p.Placements).Select(p => p.ItemId).ToList();
            Assert.Equal(count, placedIds.Count + r.NotPlaced.Count);
            Assert.Equal(count, placedIds.Concat(r.NotPlaced).Distinct().Count());

            foreach (var page in r.Pages)
            {
                var rects = page.Placements;
                foreach (var p in rects)
                {
                    var item = items.First(i => i.Id == p.ItemId);
                    var (ew, eh) = p.Rotated ? (item.HeightMm, item.WidthMm) : (item.WidthMm, item.HeightMm);
                    Assert.Equal(ew * p.Scale, p.Width, 6);
                    Assert.Equal(eh * p.Scale, p.Height, 6);
                    if (!o.ShrinkToFit) Assert.Equal(1, p.Scale, 9);
                    if (!rotate) Assert.False(p.Rotated);

                    // Inside the margin
                    Assert.True(p.X >= margin - Eps && p.Y >= margin - Eps, "ragt links/oben in den Rand");
                    Assert.True(p.X + p.Width <= page.WidthMm - margin + Eps, "ragt rechts in den Rand");
                    Assert.True(p.Y + p.Height <= page.HeightMm - margin + Eps, "ragt unten in den Rand");
                }

                // No overlap, minimum gap between all pairs
                for (var i = 0; i < rects.Count; i++)
                for (var j = i + 1; j < rects.Count; j++)
                    Assert.True(Separated(rects[i], rects[j], gap),
                        $"Bilder {rects[i].ItemId} und {rects[j].ItemId} zu nah oder überlappend");

                if (mode == PackingMode.CutFriendly)
                    Assert.True(IsGuillotine(rects.ToList()), "Seite nicht mit durchgehenden Schnitten zerlegbar");
            }
        }

        output.WriteLine($"{mode}, Drehen {rotate}, Abstand {gap} mm: {totalPages} Seiten für 150 Mischungen");
    }

    [Fact]
    public void Dense_NeedsNoMorePagesThanCutFriendly_OnAverage()
    {
        var rng = new Random(99);
        int cut = 0, dense = 0;
        for (var run = 0; run < 200; run++)
        {
            var items = Enumerable.Range(1, rng.Next(5, 30))
                .Select(i => new PrintItem(i, rng.Next(30, 160), rng.Next(30, 160))).ToList();
            cut += PrintLayout.Arrange(items, Options(PackingMode.CutFriendly)).Pages.Count;
            dense += PrintLayout.Arrange(items, Options(PackingMode.Dense)).Pages.Count;
        }

        output.WriteLine($"Schneidefreundlich: {cut} Seiten, dicht: {dense} Seiten");
        Assert.True(dense <= cut, $"dicht {dense} > schneidefreundlich {cut}");
    }

    // ------------------------------------------------------------ Check helpers

    private static bool Separated(Placement a, Placement b, double gap) =>
        a.X + a.Width + gap <= b.X + Eps || b.X + b.Width + gap <= a.X + Eps ||
        a.Y + a.Height + gap <= b.Y + Eps || b.Y + b.Height + gap <= a.Y + Eps;

    /// <summary>
    /// Determines whether the set can be split by a continuous vertical or horizontal cut
    /// into two non-empty parts that can each be split in the same way again.
    /// </summary>
    private static bool IsGuillotine(List<Placement> rects)
    {
        if (rects.Count <= 1) return true;

        foreach (var cut in rects.Select(r => r.X + r.Width).Distinct())
        {
            if (rects.Any(r => r.X < cut - Eps && r.X + r.Width > cut + Eps)) continue;   // the cut would hit an image
            var left = rects.Where(r => r.X + r.Width <= cut + Eps).ToList();
            var right = rects.Where(r => r.X >= cut - Eps).ToList();
            if (left.Count > 0 && right.Count > 0 && left.Count + right.Count == rects.Count
                && IsGuillotine(left) && IsGuillotine(right))
                return true;
        }

        foreach (var cut in rects.Select(r => r.Y + r.Height).Distinct())
        {
            if (rects.Any(r => r.Y < cut - Eps && r.Y + r.Height > cut + Eps)) continue;
            var top = rects.Where(r => r.Y + r.Height <= cut + Eps).ToList();
            var bottom = rects.Where(r => r.Y >= cut - Eps).ToList();
            if (top.Count > 0 && bottom.Count > 0 && top.Count + bottom.Count == rects.Count
                && IsGuillotine(top) && IsGuillotine(bottom))
                return true;
        }

        return false;
    }
}
