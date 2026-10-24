using System.Diagnostics;
using ImageConverter.Core.Ai;
using ImageConverter.Core.Ai.Upscale;
using ImageConverter.Core.Imaging;
using ImageMagick;
using Microsoft.ML.OnnxRuntime;

namespace ImageConverter.Core.Platform;

/// <summary>
/// Result of the speed test for one area (image filters or AI): the best time on the CPU and on the graphics card.
/// </summary>
/// <remarks>
/// <see cref="Machine"/> describes what the measurement was taken on; if it no longer matches the computer
/// (different graphics card, different processor, different CPU limit), the result does not apply.
/// </remarks>
/// <param name="CpuSeconds">Best time on the CPU in seconds.</param>
/// <param name="GpuSeconds">Best time on the graphics card in seconds; <see langword="null"/> if none was usable.</param>
/// <param name="Machine">Identifier from <see cref="SpeedTest.FilterMachine"/> or <see cref="SpeedTest.AiMachine"/>.</param>
/// <param name="When">Local time of the measurement.</param>
public sealed record SpeedMeasure(double CpuSeconds, double? GpuSeconds, string Machine, DateTime When)
{
    /// <summary>Whether the graphics card was slower than (or as fast as) the CPU in the test.</summary>
    public bool CpuFaster => GpuSeconds is { } gpu && gpu >= CpuSeconds;

    /// <summary>
    /// Decides whether to process on the graphics card.
    /// </summary>
    /// <param name="measure">Stored result; may be <see langword="null"/>.</param>
    /// <param name="machine">Identifier of the current computer.</param>
    /// <returns>
    /// Without a valid result <see langword="true"/> (the graphics card if available, as without a test); with a
    /// result for exactly this computer, whether the graphics card was the faster device.
    /// </returns>
    public static bool PreferGpu(SpeedMeasure? measure, string machine) =>
        measure is null || measure.Machine != machine || !measure.CpuFaster;
}

/// <summary>
/// Speed test (Settings › Performance): runs the same work once on the graphics card and once on the CPU.
/// </summary>
/// <remarks>
/// On fast processors the CPU is often as fast or faster (measured: Ryzen 9 7950X3D against RX 6650 XT, see
/// docs/KI-GRAFIKKARTE.md), on laptops the graphics card is considerably faster; hence the speed is measured
/// instead of assumed. Each device first gets a warm-up run (compiling kernels, loading the model), then the best
/// of two times counts.
/// </remarks>
public static class SpeedTest
{
    /// <summary>Size of the test image for the image filters in pixels (about 1 MP).</summary>
    internal const int FilterWidth = 1200, FilterHeight = 900;

    /// <summary>Edge length of the square test image for the AI in pixels (x4v3: 3 × 3 tiles).</summary>
    internal const int AiSize = 300;

    /// <summary>Returns the computer identifier for the image filters.</summary>
    /// <param name="device">Name of the graphics card (ILGPU); <see langword="null"/> if none.</param>
    /// <returns>Graphics card, number of logical processors and CPU limit.</returns>
    public static string FilterMachine(string? device) => $"{device ?? "-"} · {Environment.ProcessorCount}{Limit}";

    /// <summary>Returns the computer identifier for the AI.</summary>
    /// <param name="provider">Execution provider of the runtime (DirectML …); <see langword="null"/> if none.</param>
    /// <returns>Execution provider, number of logical processors and CPU limit.</returns>
    public static string AiMachine(string? provider) => $"{provider ?? "-"} · {Environment.ProcessorCount}{Limit}";

    // A different limit gives different CPU times, so measurements no longer apply. At 100 % there is no suffix, so
    // that identifiers stored without a limit remain valid.
    private static string Limit => ComputeLimits.Percent >= 100 ? "" : $" · {ComputeLimits.Percent} %";

    /// <summary>Measures denoising and dehazing of a test image on the graphics card and on the CPU.</summary>
    /// <param name="ct">Cancels the test between runs.</param>
    /// <returns>The measurement for <see cref="FilterMachine"/>.</returns>
    public static SpeedMeasure Filters(CancellationToken ct = default)
    {
        var px = TestPixels(FilterWidth, FilterHeight);
        double? gpu = null;
        using (GpuFilters.GpuEvenIfSlower())
        {
            var seconds = Best(() => FilterWork(px), ct);
            if (GpuFilters.DeviceName is not null && GpuFilters.Problem is null) gpu = seconds;   // otherwise it ran on the CPU
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
    /// Measures a ×4 tile model (x4v3) on a test image on the graphics card and on the CPU.
    /// </summary>
    /// <remarks>Uses its own sessions; the models opened by the application are not affected.</remarks>
    /// <param name="modelPath">Path of the ONNX model file.</param>
    /// <param name="model">Description of the model.</param>
    /// <param name="ct">Cancels the test between runs.</param>
    /// <returns>The measurement for <see cref="AiMachine"/>; the GPU time is <see langword="null"/> if the graphics card was not usable.</returns>
    public static SpeedMeasure Ai(string modelPath, AiModel model, CancellationToken ct = default)
    {
        var provider = OnnxSessions.GpuProvider;   // also disables the telemetry of the runtime
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

    /// <summary>One warm-up run, then the best of two times in seconds.</summary>
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

    /// <summary>Noisy gradient with some haze, so that denoising and dehazing have real work to do.</summary>
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
