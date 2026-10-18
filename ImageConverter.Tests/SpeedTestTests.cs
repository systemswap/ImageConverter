using ImageConverter.Core.Imaging;
using ImageConverter.Core.Platform;

namespace ImageConverter.Tests;

/// <summary>Geschwindigkeitstest Grafikkarte/CPU: Auswahl des Schnelleren und die Messung der Bildfilter.</summary>
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
        Assert.False(SpeedMeasure.PreferGpu(equal, Here));   // gleich schnell: CPU, die Grafikkarte bleibt frei
    }

    [Fact]
    public void PreferGpu_IgnoresAResultFromAnotherComputer_OrWithoutGraphicsCard()
    {
        var cpuFaster = new SpeedMeasure(1.0, 2.0, Here, DateTime.Now);
        Assert.True(SpeedMeasure.PreferGpu(cpuFaster, "Andere Karte · 16"));   // neues System: erst wieder testen
        Assert.True(SpeedMeasure.PreferGpu(cpuFaster, "Testkarte · 8"));
        var noGpu = new SpeedMeasure(1.0, null, Here, DateTime.Now);
        Assert.True(SpeedMeasure.PreferGpu(noGpu, Here));   // ohne Grafikkarte gemessen: nichts zu entscheiden
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
}
