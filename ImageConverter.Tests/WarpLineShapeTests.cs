using Avalonia;
using ImageConverter.Controls;
using ImageConverter.Core.Imaging;

namespace ImageConverter.Tests;

/// <summary>
/// Linien im Werkzeug „Gewölbtes Blatt“: ein Vorschlag, der auf einer eigenen Linie liegt, gilt als doppelt (wird nicht
/// noch einmal vorgeschlagen); übernommene Vorschläge sind eigene Linien.
/// </summary>
public class WarpLineShapeTests
{
    private static WarpLineShape Line(params (double X, double Y)[] pts) =>
        new(WarpLineKind.Horizontal, pts.Select(p => new Point(p.X, p.Y)).ToArray());

    [Fact]
    public void Overlaps_SameLine_ShortOwnLineOnLongSuggestion_ButNotTheNextRow()
    {
        var suggestion = Line((100, 200), (500, 205), (900, 200));
        Assert.True(Line((102, 203), (898, 198)).Overlaps(suggestion, 6));              // fast dieselbe
        Assert.True(Line((400, 206), (600, 204)).Overlaps(suggestion, 6));              // kurze eigene Linie auf dem langen Vorschlag
        Assert.False(Line((100, 240), (900, 240)).Overlaps(suggestion, 6));             // nächste Zeile, 40 px tiefer
        Assert.False(Line((100, 200)).Overlaps(suggestion, 6));                          // ein Punkt allein ist keine Linie
    }

    [Fact]
    public void Adopted_IsAnOwnLine_AndCountsWithTwoPoints()
    {
        var s = Line((0, 0), (10, 0)) with { Suggested = true };
        var own = s.Adopted();
        Assert.False(own.Suggested);
        Assert.True(own.KindChosen);
        Assert.True(own.Counts);
        Assert.False(Line((0, 0)).Counts);
    }
}
