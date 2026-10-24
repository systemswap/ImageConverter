using ILGPU;
using ILGPU.Algorithms;
using ILGPU.Runtime;
using ImageConverter.Core.Localization;
using ImageConverter.Core.Platform;

namespace ImageConverter.Core.Imaging;

/// <summary>
/// Runs image filters on the GPU (ILGPU: OpenCL for AMD/Intel, CUDA for NVIDIA) with the same computation as on the
/// CPU, one thread per pixel.
/// </summary>
/// <remarks>
/// <para>Worthwhile for filters with many exponential or square-root operations per pixel (noise reduction, dehaze):
/// measured on the Radeon integrated in a Notebook-Prozessor, about 13 times as fast as all CPU cores.</para>
/// <para>Without a GPU, when disabled or after an error, the caller continues on the CPU. An error disables the GPU
/// until the program restarts and is written to the log.</para>
/// <para>The results differ only in rounding (floating-point operations in a different order, relatively about
/// 10⁻⁷).</para>
/// <para>All GPU work is serialised by one lock, because preview, size estimate and export share one device.</para>
/// </remarks>
public static class GpuFilters
{
    private static readonly Lock Gate = new();
    private static Context? _context;
    private static Accelerator? _accelerator;
    private static bool _tried;

    /// <summary>Gets or sets whether the GPU is used when available. Default <see langword="true"/>.</summary>
    public static bool Enabled { get; set; } = true;

    private static readonly AsyncLocal<bool> CpuOnlyHere = new();

    /// <summary>
    /// Computes on the CPU only in the current asynchronous flow (GPU/CPU comparison in tests); other computations running
    /// at the same time stay on the GPU. Disposing the result switches back.
    /// </summary>
    internal static IDisposable CpuOnly()
    {
        CpuOnlyHere.Value = true;
        return new Reset();
    }

    private sealed class Reset : IDisposable
    {
        public void Dispose() => CpuOnlyHere.Value = false;
    }

    private static readonly AsyncLocal<bool> ForceGpuHere = new();

    /// <summary>Computes on the GPU in the current asynchronous flow even if the speed test chose the CPU. Disposing the
    /// result switches back.</summary>
    internal static IDisposable GpuEvenIfSlower()
    {
        ForceGpuHere.Value = true;
        return new ResetForce();
    }

    private sealed class ResetForce : IDisposable
    {
        public void Dispose() => ForceGpuHere.Value = false;
    }

    /// <summary>
    /// Gets or sets the result of the last speed test (settings); <see langword="null"/> = no test.
    /// </summary>
    /// <remarks>If the CPU was faster on this machine, the filters run there. The GPU stays open in case a new test
    /// comes out differently.</remarks>
    public static SpeedMeasure? Measured { get; set; }

    /// <summary>Gets whether the GPU is enabled, open and, according to the speed test (or without one), the faster
    /// choice.</summary>
    public static bool UsesGpu => Enabled && DeviceName is { } device && SpeedMeasure.PreferGpu(Measured, SpeedTest.FilterMachine(device));

    /// <summary>Gets the name of the GPU once it is open; <see langword="null"/> without one.</summary>
    public static string? DeviceName { get; private set; }

    /// <summary>Gets the reason why the GPU is not used (or no longer used); otherwise <see langword="null"/>.</summary>
    public static string? Problem { get; private set; }

    /// <summary>
    /// Opens the GPU and compiles the kernels on a small image, in the background shortly after startup; otherwise the
    /// first preview with noise reduction or dehaze would have to wait for it.
    /// </summary>
    /// <returns>A task that completes when the warm-up is done. Errors are written to the log and not rethrown.</returns>
    public static Task WarmUpAsync() => Task.Run(() =>
    {
        try
        {
            const int w = 96, h = 64;
            var px = new float[w * h * 4];
            var rng = new Random(1);
            for (var i = 0; i < px.Length; i++) px[i] = i % 4 == 3 ? 1 : 0.3f + 0.4f * (float)rng.NextDouble();
            PixelPool.Return(Denoiser.Apply(px, w, h, 0.5, 0));
            PixelPool.Return(Adjust.Dehazed(px, w, h, Adjustments.None with { Dehaze = 50 }));
        }
        catch (Exception e)
        {
            AppLog.Write("Vorwärmen der Grafikkarte fehlgeschlagen", e.ToString());
        }
    });

    /// <summary>
    /// Runs <paramref name="work"/> on the GPU (one after another, never concurrently, because preview, size estimate and
    /// export share one GPU). false: no GPU, CPU preferred by the speed test, or an error; the caller then computes on the
    /// CPU.
    /// </summary>
    internal static bool TryRun(Action<Accelerator> work)
    {
        if (!Enabled || CpuOnlyHere.Value) return false;
        lock (Gate)
        {
            if (Open() is not { } accelerator) return false;
            if (!ForceGpuHere.Value && !SpeedMeasure.PreferGpu(Measured, SpeedTest.FilterMachine(DeviceName))) return false;
            try
            {
                work(accelerator);
                accelerator.Synchronize();
                return true;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                Problem = e.Message;
                AppLog.Write("Grafikkarte für Bildfilter abgeschaltet (rechnet auf der CPU weiter)", e.ToString());
                Close();
                _tried = true;   // Do not open again
                return false;
            }
        }
    }

    private static Accelerator? Open()
    {
        if (_accelerator is not null || _tried) return _accelerator;
        _tried = true;
        try
        {
            _context = Context.Create(b => b.Default().EnableAlgorithms().Optimize(OptimizationLevel.O2));
            // A real GPU (CUDA before OpenCL); with several, the one with the most compute units, not by memory: an
            // integrated GPU reports the shared main memory (31 GB versus 8 GB of a dedicated card).
            // The CPU emulation of ILGPU is slower than the program's own CPU loops, so it is not used.
            var candidates = _context.Devices.Where(d => d.AcceleratorType is AcceleratorType.Cuda or AcceleratorType.OpenCL).ToList();
            var device = candidates
                .OrderBy(d => d.AcceleratorType == AcceleratorType.Cuda ? 0 : 1)
                .ThenByDescending(d => d.NumMultiprocessors)
                .FirstOrDefault();
            var found = string.Join(", ", candidates.Select(d => $"{d.AcceleratorType} {d.Name} ({d.NumMultiprocessors} Recheneinheiten, {d.MemorySize >> 20} MB)"));
            if (device is null)
            {
                Problem = Tr.T("Keine Grafikkarte mit OpenCL 2.0 oder CUDA gefunden.");
                AppLog.Write("Bildfilter: keine Grafikkarte (OpenCL 2.0 oder CUDA) gefunden — rechnen auf der CPU",
                    "Geräte laut ILGPU: " + string.Join(", ", _context.Devices.Select(d => $"{d.AcceleratorType} {d.Name}")));
                return null;
            }
            _accelerator = device.CreateAccelerator(_context);
            DeviceName = device.Name;
            AppLog.Write($"Bildfilter auf der Grafikkarte: {device.Name}", "gefunden: " + found);
            return _accelerator;
        }
        catch (Exception e)
        {
            Problem = e.Message;
            AppLog.Write("Grafikkarte für Bildfilter nicht verfügbar", e.ToString());
            Close();
            return null;
        }
    }

    private static void Close()
    {
        _nlm = null;
        Dehaze.ForgetGpuKernels();
        _accelerator?.Dispose();
        _accelerator = null;
        _context?.Dispose();
        _context = null;
        DeviceName = null;
    }

    // ------------------------------------------------------------ Noise reduction (Non-Local Means)

    private static Action<Index1D, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, NlmParams>? _nlm;

    /// <summary>Constant parameters of the Non-Local Means kernel.</summary>
    internal readonly record struct NlmParams(int Width, int Height, int PatchRadius, int SearchRadius, float Mix);

    /// <summary>
    /// Non-Local Means on the luminance as in <see cref="Denoiser"/>: per pixel all offsets in the search window, the
    /// distance of the neighbourhoods as the mean of the squared differences (at the border only over what lies inside
    /// the image), the weight from the noise, the pixel itself with the largest weight, mixed according to the strength.
    /// null: compute on the CPU.
    /// </summary>
    internal static float[]? NonLocalMeans(float[] y, float[] sigma2, float[] invH2, int w, int h, int patchRadius, int searchRadius,
        float mix)
    {
        float[]? result = null;
        var ok = TryRun(acc =>
        {
            _nlm ??= acc.LoadAutoGroupedStreamKernel<Index1D, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, NlmParams>(
                NlmKernel);
            using var dy = acc.Allocate1D(y);
            using var ds = acc.Allocate1D(sigma2);
            using var dh = acc.Allocate1D(invH2);
            using var dr = acc.Allocate1D<float>(y.Length);
            _nlm(y.Length, dy.View, ds.View, dh.View, dr.View, new NlmParams(w, h, patchRadius, searchRadius, mix));
            result = dr.GetAsArray1D();
        });
        return ok ? result : null;
    }

    private static void NlmKernel(Index1D index, ArrayView<float> y, ArrayView<float> sigma2, ArrayView<float> invH2,
        ArrayView<float> result, NlmParams p)
    {
        int i = index, w = p.Width, h = p.Height, pr = p.PatchRadius, sr = p.SearchRadius;
        var row = i / w;
        var x = i - row * w;
        int r0 = XMath.Max(0, row - pr), r1 = XMath.Min(h - 1, row + pr);
        int c0 = XMath.Max(0, x - pr), c1 = XMath.Min(w - 1, x + pr);
        var count = (float)((r1 - r0 + 1) * (c1 - c0 + 1));
        var s2 = sigma2[i];
        var ih = invH2[i];
        float sum = 0, weightSum = 0, maxWeight = 0;
        for (var dy = -sr; dy <= sr; dy++)
        for (var dx = -sr; dx <= sr; dx++)
        {
            if (dx == 0 && dy == 0) continue;
            float acc = 0;
            for (var rr = r0; rr <= r1; rr++)
            {
                var sRow = XMath.Clamp(rr + dy, 0, h - 1) * w;
                for (var cc = c0; cc <= c1; cc++)
                {
                    var d = y[rr * w + cc] - y[sRow + XMath.Clamp(cc + dx, 0, w - 1)];
                    acc += d * d;
                }
            }
            var patch = acc / count;
            var wt = XMath.Exp(-XMath.Max(patch - 2 * s2, 0f) * ih);
            sum += wt * y[XMath.Clamp(row + dy, 0, h - 1) * w + XMath.Clamp(x + dx, 0, w - 1)];
            weightSum += wt;
            if (wt > maxWeight) maxWeight = wt;
        }
        var self = XMath.Max(maxWeight, 1e-3f);
        var filtered = (sum + self * y[i]) / (weightSum + self);
        result[i] = y[i] + p.Mix * (filtered - y[i]);
    }
}
