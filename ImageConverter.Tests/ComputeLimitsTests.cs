using ImageConverter.Core.Platform;

namespace ImageConverter.Tests;

/// <summary>CPU limit: threads for parallel loops and AI, counting cores.</summary>
public class ComputeLimitsTests
{
    [Fact]
    public void PhysicalCores_AreBetweenOneAndTheLogicalProcessors()
    {
        Assert.InRange(ComputeLimits.PhysicalCores, 1, Environment.ProcessorCount);
    }

    [Fact]
    public void Percent_LimitsLoopsAndAi_And100MeansNoLimit()
    {
        try
        {
            ComputeLimits.SetPercent(50);
            Assert.Equal(Math.Max(1, (int)Math.Round(Environment.ProcessorCount * 0.5)), ComputeLimits.Options.MaxDegreeOfParallelism);
            Assert.Equal(Math.Max(1, (int)Math.Round(ComputeLimits.PhysicalCores * 0.5)), ComputeLimits.OnnxThreads);

            ComputeLimits.SetPercent(3);   // below 10 % is raised to 10 %, never 0 threads
            Assert.Equal(10, ComputeLimits.Percent);
            Assert.True(ComputeLimits.Options.MaxDegreeOfParallelism >= 1);
            Assert.True(ComputeLimits.OnnxThreads >= 1);
        }
        finally
        {
            ComputeLimits.SetPercent(100);
        }
        Assert.Equal(-1, ComputeLimits.Options.MaxDegreeOfParallelism);
        Assert.Null(ComputeLimits.OnnxThreads);
    }

    [Fact]
    public void Limit_IsPartOfTheMachineKey_SoOldMeasurementsDoNotApply()
    {
        var full = SpeedTest.AiMachine("DmlExecutionProvider");
        try
        {
            ComputeLimits.SetPercent(50);
            Assert.NotEqual(full, SpeedTest.AiMachine("DmlExecutionProvider"));
        }
        finally
        {
            ComputeLimits.SetPercent(100);
        }
        Assert.Equal(full, SpeedTest.AiMachine("DmlExecutionProvider"));
    }

    [Fact]
    public void ImageMagickThreads_AreSetOnce_NeverWhileItMayBeWorking()
    {
        // Changing the thread count while ImageMagick is working corrupts its memory (crash), so it is set only once at
        // start-up. See docs/FEHLERGEDAECHTNIS.md, No. 77.
        ComputeLimits.SetPercent(ComputeLimits.Percent);
        var threads = ImageMagick.ResourceLimits.Thread;
        try
        {
            ComputeLimits.SetPercent(10);
            Assert.Equal(threads, ImageMagick.ResourceLimits.Thread);
            Assert.True(ComputeLimits.MagickNeedsRestart || ComputeLimits.MagickPercent == 10);
        }
        finally
        {
            ComputeLimits.SetPercent(100);
        }
        Assert.Equal(threads, ImageMagick.ResourceLimits.Thread);
    }
}
