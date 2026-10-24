using ImageConverter.Core.Ai.Upscale;
using ImageConverter.Core.Imaging;
using ImageConverter.Core.Platform;

namespace ImageConverter.Tests;

/// <summary>Speed test graphics card/CPU: choice of the faster device and the measurement of the image filters.</summary>
public class SpeedTestTests
{
    private const string Here = "Testkarte · 16";

    [Fact]
    public void PreferGpu_WithoutTest_UsesTheGraphicsCard()
    {
        Assert.True(SpeedMeasure.PreferGpu(null, Here));
    }

    [Fact]
    public void PreferGpu_TakesTheFasterOne_OnTheMeasuredComputer()
    {
        var cpuFaster = new SpeedMeasure(CpuSeconds: 1.0, GpuSeconds: 2.0, Here, DateTime.Now);
        var gpuFaster = new SpeedMeasure(CpuSeconds: 3.0, GpuSeconds: 1.0, Here, DateTime.Now);
        var equal = new SpeedMeasure(CpuSeconds: 1.0, GpuSeconds: 1.0, Here, DateTime.Now);
        Assert.False(SpeedMeasure.PreferGpu(cpuFaster, Here));
        Assert.True(SpeedMeasure.PreferGpu(gpuFaster, Here));
        Assert.False(SpeedMeasure.PreferGpu(equal, Here));   // equally fast: CPU, so that the graphics card stays free
    }

    [Fact]
    public void PreferGpu_IgnoresAResultFromAnotherComputer_OrWithoutGraphicsCard()
    {
        var cpuFaster = new SpeedMeasure(1.0, 2.0, Here, DateTime.Now);
        Assert.True(SpeedMeasure.PreferGpu(cpuFaster, "Andere Karte · 16"));   // different system: test again first
        Assert.True(SpeedMeasure.PreferGpu(cpuFaster, "Testkarte · 8"));
        var noGpu = new SpeedMeasure(1.0, null, Here, DateTime.Now);
        Assert.True(SpeedMeasure.PreferGpu(noGpu, Here));   // measured without a graphics card: nothing to decide
    }

    [Fact]
    public void Settings_KeepTheMeasurement()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ic-speed-" + Guid.NewGuid().ToString("N"));
        var file = Path.Combine(dir, "settings.json");
        try
        {
            var m = new SpeedMeasure(0.5, 0.25, Here, new DateTime(2026, 10, 18, 12, 0, 0));
            new AppSettings(FilterSpeed: m).Save(file);
            Assert.Equal(m, AppSettings.Load(file).FilterSpeed);
            Assert.Null(AppSettings.Load(file).AiSpeed);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Filters_MeasuresTheCpu_AndNamesTheComputer()
    {
        var m = SpeedTest.Filters();
        Assert.True(m.CpuSeconds > 0);
        Assert.Equal(SpeedTest.FilterMachine(GpuFilters.DeviceName), m.Machine);
        Assert.Equal(GpuFilters.DeviceName is not null, m.GpuSeconds is not null);
    }

    [Fact]
    public void UpscaleTimes_FirstMeasurementReplacesTheDefault_AndSurvivesARestart()
    {
        var saved = Upscalers.Learned();
        var key = Upscalers.OnGpu ? "Generative|gpu" : "Generative|cpu";
        try
        {
            var before = Upscalers.Estimate(UpscaleMethod.Generative, 500, 300);
            // a fast computer: one sixth of the applicable default (laptop: CPU 355 s, graphics card 97 s — which one applies
            // depends on whether another test has already opened the graphics card; on Windows with DirectML this is often the case)
            var fast = before.TotalSeconds / 6;
            Upscalers.Measured(UpscaleMethod.Generative, 500, 300, TimeSpan.FromSeconds(fast), replace: true);
            Assert.InRange(Upscalers.Estimate(UpscaleMethod.Generative, 500, 300).TotalSeconds, fast * 0.999, fast * 1.001);
            Assert.True(Upscalers.Estimate(UpscaleMethod.Generative, 500, 300) < before / 3);

            // large images by the work of the tiles, not by the area
            var big = Upscalers.Estimate(UpscaleMethod.Generative, 4000, 3000).TotalSeconds;
            var ratio = Upscalers.Work(UpscaleMethod.Generative, 4000, 3000) / Upscalers.Work(UpscaleMethod.Generative, 500, 300);
            Assert.InRange(big / fast, ratio * 0.999, ratio * 1.001);

            Assert.True(Upscalers.Learned().ContainsKey(key));
            Upscalers.Restore(new Dictionary<string, double> { [key] = 0.01, ["Unsinn|cpu"] = 1, ["Fast|cpu"] = double.NaN });
            Assert.InRange(Upscalers.Estimate(UpscaleMethod.Generative, 500, 300).TotalSeconds,
                0.01 * Upscalers.Work(UpscaleMethod.Generative, 500, 300) * 0.999, 0.01 * Upscalers.Work(UpscaleMethod.Generative, 500, 300) * 1.001);
        }
        finally
        {
            Upscalers.Restore(saved);   // restore the previous state for other tests
        }
    }

    [Fact]
    public void Work_SmallImagesOverlapMore_ThanTheirArea()
    {
        // Work per megapixel is higher for small images (tiles overlap proportionally more) — the area alone
        // would underestimate large images and overestimate small ones
        double PerMp(int w, int h) => Upscalers.Work(UpscaleMethod.Generative, w, h) / (w * (double)h);
        Assert.True(PerMp(256, 256) > PerMp(4000, 3000));
        Assert.Equal(1, UpscaleEngine.TileCount(100, 100, 128));
        Assert.True(UpscaleEngine.TileCount(1000, 1000, 128) > 50);
    }

    [Fact]
    public void Settings_UpdatesFromSeveralThreads_KeepEveryChange_AndLeaveNoTempFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ic-settings-" + Guid.NewGuid().ToString("N"));
        var file = Path.Combine(dir, "settings.json");
        try
        {
            var m = new SpeedMeasure(0.5, 0.25, Here, new DateTime(2026, 10, 18, 12, 0, 0));
            // change different fields concurrently: none may overwrite another
            Parallel.For(0, 40, i =>
            {
                if (i % 2 == 0) AppSettings.Update(s => s with { FilterSpeed = m }, file);
                else AppSettings.Update(s => s with { CpuPercent = 50 }, file);
            });
            var loaded = AppSettings.Load(file);
            Assert.Equal(m, loaded.FilterSpeed);
            Assert.Equal(50, loaded.CpuPercent);
            Assert.False(File.Exists(file + ".tmp"));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }
}
