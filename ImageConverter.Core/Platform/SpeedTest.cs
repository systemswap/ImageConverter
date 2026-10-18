using System.Diagnostics;
using ImageConverter.Core.Ai;
using ImageConverter.Core.Ai.Upscale;
using ImageConverter.Core.Imaging;
using ImageMagick;
using Microsoft.ML.OnnxRuntime;

namespace ImageConverter.Core.Platform;

/// <summary>
/// Ergebnis des Geschwindigkeitstests für einen Bereich (Bildfilter oder KI): beste Zeit auf der CPU und auf der
/// Grafikkarte (null: keine nutzbar). <see cref="Machine"/> beschreibt, worauf gemessen wurde — passt es nicht mehr zum
/// Rechner (andere Grafikkarte, anderer Prozessor), gilt das Ergebnis nicht.
/// </summary>
public sealed record SpeedMeasure(double CpuSeconds, double? GpuSeconds, string Machine, DateTime When)
{
    /// <summary>Die Grafikkarte war im Test langsamer (oder gleich schnell) als die CPU.</summary>
    public bool CpuFaster => GpuSeconds is { } gpu && gpu >= CpuSeconds;

    /// <summary>
    /// Auf der Grafikkarte rechnen? Ohne gültiges Ergebnis ja (Grafikkarte, wenn vorhanden — wie vor dem Test); mit
    /// Ergebnis für genau diesen Rechner das Schnellere.
    /// </summary>
    public static bool PreferGpu(SpeedMeasure? measure, string machine) =>
        measure is null || measure.Machine != machine || !measure.CpuFaster;
}

/// <summary>
/// Geschwindigkeitstest (Einstellungen › Leistung): rechnet dieselbe Arbeit einmal auf der Grafikkarte und einmal auf
/// der CPU. Auf schnellen Prozessoren ist die CPU oft gleich schnell oder schneller (gemessen: Ryzen 9 7950X3D gegen
/// RX 6650 XT, docs/KI-GRAFIKKARTE.md), auf Laptops die Grafikkarte deutlich schneller — deshalb messen statt annehmen.
/// Je Gerät erst ein Aufwärmlauf (Kernel übersetzen, Modell laden), dann die beste von zwei Zeiten.
/// </summary>
public static class SpeedTest
{
    /// <summary>Größe des Testbildes für die Bildfilter (etwa 1 MP).</summary>
    internal const int FilterWidth = 1200, FilterHeight = 900;

    /// <summary>Größe des Testbildes für die KI (x4v3: 3 × 3 Kacheln).</summary>
    internal const int AiSize = 300;

    /// <summary>Kennung des Rechners für die Bildfilter: Grafikkarte (ILGPU), Zahl der Kerne und CPU-Obergrenze.</summary>
    public static string FilterMachine(string? device) => $"{device ?? "-"} · {Environment.ProcessorCount}{Limit}";

    /// <summary>Kennung des Rechners für die KI: Anbindung der Laufzeit (DirectML …), Zahl der Kerne und CPU-Obergrenze.</summary>
    public static string AiMachine(string? provider) => $"{provider ?? "-"} · {Environment.ProcessorCount}{Limit}";

    // Andere Obergrenze, andere CPU-Zeiten — dann gelten Messungen nicht mehr (100 % ohne Zusatz wie bisher)
    private static string Limit => ComputeLimits.Percent >= 100 ? "" : $" · {ComputeLimits.Percent} %";

    /// <summary>Entrauschen und Dunst an einem Testbild, Grafikkarte und CPU.</summary>
    public static SpeedMeasure Filters(CancellationToken ct = default)
    {
        var px = TestPixels(FilterWidth, FilterHeight);
        double? gpu = null;
        using (GpuFilters.GpuEvenIfSlower())
        {
            var seconds = Best(() => FilterWork(px), ct);
            if (GpuFilters.DeviceName is not null && GpuFilters.Problem is null) gpu = seconds;   // sonst lief es auf der CPU
        }
        double cpu;
        using (GpuFilters.CpuOnly()) cpu = Best(() => FilterWork(px), ct);
        return new SpeedMeasure(cpu, gpu, FilterMachine(GpuFilters.DeviceName), DateTime.Now);
    }

    private static void FilterWork(float[] px)
    {
        PixelPool.Return(Denoiser.Apply(px, FilterWidth, FilterHeight, 0.5, 0.3));
        PixelPool.Return(Adjust.Dehazed(px, FilterWidth, FilterHeight, Adjustments.None with { Dehaze = 50 }));
    }

    /// <summary>
    /// Ein ×4-Kachelmodell (x4v3) an einem Testbild, Grafikkarte und CPU — eigene Sitzungen, die geöffneten Modelle der
    /// App bleiben unberührt.
    /// </summary>
    public static SpeedMeasure Ai(string modelPath, AiModel model, CancellationToken ct = default)
    {
        var provider = OnnxSessions.GpuProvider;   // schaltet auch die Telemetrie der Laufzeit ab
        using var source = new MagickImage(new MagickColor(30000, 32000, 28000), AiSize, AiSize);
        source.AddNoise(NoiseType.Gaussian, 1.5);
        source.Alpha(AlphaOption.Off);

        double Run(SessionOptions options)
        {
            using var session = new InferenceSession(modelPath, options);
            var tiles = new OnnxTileModel(session, model);
            return Best(() => UpscaleEngine.RunTiles(source, tiles, null, ct), ct);
        }

        double? gpu = null;
        if (provider is not null)
        {
            try
            {
                using var options = OnnxSessions.GpuOptions(provider);
                gpu = Run(options);
            }
            catch (Exception e) when (e is OnnxRuntimeException or EntryPointNotFoundException or DllNotFoundException)
            {
                AppLog.Write("Geschwindigkeitstest: Grafikkarte für die KI nicht nutzbar", e.ToString());
            }
        }
        using var cpuOptions = OnnxSessions.CpuOptions();
        var cpu = Run(cpuOptions);
        return new SpeedMeasure(cpu, gpu, AiMachine(provider), DateTime.Now);
    }

    /// <summary>Ein Aufwärmlauf, dann die beste von zwei Zeiten in Sekunden.</summary>
    private static double Best(Action work, CancellationToken ct)
    {
        work();
        var best = double.MaxValue;
        for (var i = 0; i < 2; i++)
        {
            ct.ThrowIfCancellationRequested();
            var clock = Stopwatch.StartNew();
            work();
            best = Math.Min(best, clock.Elapsed.TotalSeconds);
        }
        return best;
    }

    /// <summary>Verrauschter Verlauf mit etwas Dunst — gibt Entrauschen und Dunst echte Arbeit.</summary>
    private static float[] TestPixels(int w, int h)
    {
        var px = new float[w * h * 4];
        var rng = new Random(7);
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var i = (y * w + x) * 4;
            var v = 0.25f + 0.5f * x / w;
            px[i] = v + 0.2f * y / h + 0.05f * (float)rng.NextDouble();
            px[i + 1] = v + 0.05f * (float)rng.NextDouble();
            px[i + 2] = v - 0.1f * y / h + 0.05f * (float)rng.NextDouble();
            px[i + 3] = 1;
        }
        return px;
    }
}
