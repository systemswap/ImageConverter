using ImageConverter.Core.Imaging;

namespace ImageConverter.Tests;

public class ResizeCalculatorTests
{
    [Fact]
    public void HeightFollowsWidth_WhenAspectLocked()
    {
        var calc = new ResizeCalculator(4000, 3000);
        Assert.Equal(1440, calc.HeightFor(1920));
    }

    [Fact]
    public void WidthFollowsHeight_WhenAspectLocked()
    {
        var calc = new ResizeCalculator(4000, 3000);
        Assert.Equal(1920, calc.WidthFor(1440));
    }

    [Fact]
    public void RatioDoesNotDrift_WhenTypingBackAndForth()
    {
        // Das Verhaeltnis stammt aus der Quelle, nicht aus den gerundeten Feldern:
        // wiederholtes Hin- und Herrechnen darf nicht wegwandern.
        var calc = new ResizeCalculator(1920, 1080);
        var width = 1337;
        for (var i = 0; i < 50; i++)
            width = calc.WidthFor(calc.HeightFor(width));

        Assert.InRange(width, 1335, 1339);
    }

    [Fact]
    public void NeverReturnsZero_ForExtremeAspect()
    {
        var calc = new ResizeCalculator(5000, 10);
        Assert.Equal(1, calc.HeightFor(16));
    }

    [Fact]
    public void LongestEdge_PicksTheLongerSide()
    {
        Assert.Equal((1920, 1080), new ResizeCalculator(3840, 2160).FromLongestEdge(1920));
        Assert.Equal((1080, 1920), new ResizeCalculator(2160, 3840).FromLongestEdge(1920));
    }

    [Fact]
    public void FitStaysInsideBox_FillCoversIt()
    {
        var calc = new ResizeCalculator(4000, 3000);
        Assert.Equal((1333, 1000), calc.Fit(1920, 1000));
        Assert.Equal((1920, 1440), calc.Fill(1920, 1000));
    }

    [Fact]
    public void Percent_ScalesBothEdges()
    {
        Assert.Equal((960, 540), new ResizeCalculator(1920, 1080).FromPercent(50));
    }

    [Fact]
    public void PhysicalSize_TimesDpi_GivesPixels()
    {
        Assert.Equal((2480, 3508), ResizeCalculator.FromPhysicalSize(8.2677, 11.6929, 300));
    }
}
