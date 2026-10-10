using ImageConverter.Core.Imaging;

namespace ImageConverter.Tests;

/// <summary>Standardformate: Hoch-/Querformat wie das Bild, Einpassen bei geschlossener Kette.</summary>
public class PaperFormatTests
{
    private static readonly PaperFormat A4 = PaperFormats.Find("A4")!;

    [Fact]
    public void Landscape3To2_OnA4_FillsWidth()
    {
        var (w, h, widthLeads) = PaperFormats.Fit(1.5, A4, keepAspect: true);
        Assert.Equal(297, w, 6);           // quer: lange Kante 297 mm
        Assert.Equal(198, h, 6);           // 297 / 1,5 — passt in 210
        Assert.True(widthLeads);
    }

    [Fact]
    public void Portrait3To4_OnA4_FillsWidthBecauseA4IsSlimmer()
    {
        // 3:4 (0,75) ist breiter als A4 hoch (0,707) → die Breite stößt zuerst an
        var (w, h, widthLeads) = PaperFormats.Fit(0.75, A4, keepAspect: true);
        Assert.Equal(210, w, 6);
        Assert.Equal(280, h, 6);
        Assert.True(widthLeads);
    }

    [Fact]
    public void TallPortrait_OnA4_FillsHeight()
    {
        var (w, h, widthLeads) = PaperFormats.Fit(0.5, A4, keepAspect: true);
        Assert.Equal(148.5, w, 6);
        Assert.Equal(297, h, 6);
        Assert.False(widthLeads);
    }

    [Fact]
    public void Square_IsPortraitAndFillsWidth()
    {
        var (w, h, _) = PaperFormats.Fit(1, A4, keepAspect: true);
        Assert.Equal(210, w, 6);
        Assert.Equal(210, h, 6);
    }

    [Fact]
    public void ChainOpen_TakesExactFormatInImageOrientation()
    {
        Assert.Equal((297, 210, true), PaperFormats.Fit(1.5, A4, keepAspect: false));
        Assert.Equal((210, 297, true), PaperFormats.Fit(0.6, A4, keepAspect: false));
    }

    [Fact]
    public void Din_GoesFromA0ToA7()
    {
        Assert.Equal(["A0", "A1", "A2", "A3", "A4", "A5", "A6", "A7"],
            PaperFormats.Din.Where(f => f.Key.StartsWith('A')).Select(f => f.Key));
        Assert.Equal((841, 1189), (PaperFormats.Find("A0")!.WidthMm, PaperFormats.Find("A0")!.HeightMm));
    }

    [Fact]
    public void Formats_AreUniqueAndPortrait()
    {
        var all = PaperFormats.All.ToList();
        Assert.Equal(all.Count, all.Select(f => f.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(all, f => Assert.True(f.WidthMm <= f.HeightMm, f.Key));
    }
}
