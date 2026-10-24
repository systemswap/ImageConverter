using Avalonia;
using ImageConverter.Controls;
using ImageConverter.Core.Imaging;

namespace ImageConverter.Tests;

/// <summary>
/// Lines in the curved-page tool: a suggestion that lies on an own line counts as a duplicate (it is not
/// suggested again); adopted suggestions become own lines.
/// </summary>
public class WarpLineShapeTests
{
    private static WarpLineShape Line(params (double X, double Y)[] pts) =>
        new(WarpLineKind.Horizontal, pts.Select(p => new Point(p.X, p.Y)).ToArray());

    [Fact]
    public void Overlaps_SameLine_ShortOwnLineOnLongSuggestion_ButNotTheNextRow()
    {
        var suggestion = Line((100, 200), (500, 205), (900, 200));
        Assert.True(Line((102, 203), (898, 198)).Overlaps(suggestion, 6));              // almost the same line
        Assert.True(Line((400, 206), (600, 204)).Overlaps(suggestion, 6));              // short own line on the long suggestion
        Assert.False(Line((100, 240), (900, 240)).Overlaps(suggestion, 6));             // next row, 40 px lower
        Assert.False(Line((100, 200)).Overlaps(suggestion, 6));                          // a single point is not a line
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
