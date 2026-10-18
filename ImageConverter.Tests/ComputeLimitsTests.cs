using ImageConverter.Core.Platform;

namespace ImageConverter.Tests;

/// <summary>Obergrenze der CPU: Stränge für Schleifen und KI, Kerne zählen.</summary>
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

            ComputeLimits.SetPercent(3);   // unter 10 % wird auf 10 % angehoben, nie 0 Stränge
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
        // Ändern der Strangzahl während ImageMagick rechnet beschädigt dessen Speicher (Absturz) — nur einmal beim Start
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
