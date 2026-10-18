using ILGPU;
using ILGPU.Algorithms;
using ILGPU.Runtime;
using ImageConverter.Core.Localization;
using ImageConverter.Core.Platform;

namespace ImageConverter.Core.Imaging;

/// <summary>
/// Bildfilter auf der Grafikkarte (ILGPU: OpenCL für AMD/Intel, CUDA für NVIDIA) — dieselbe Rechnung wie auf der CPU, je
/// Pixel ein Faden. Lohnt bei Filtern mit viel Exponential-/Wurzelrechnung je Pixel (Entrauschen, Dunst): gemessen auf der
/// Radeon im Notebook-Prozessor etwa 13× so schnell wie alle CPU-Kerne. Ohne Grafikkarte, abgeschaltet oder bei einem Fehler
/// rechnet der Aufrufer auf der CPU weiter (der Fehler schaltet die Grafikkarte bis zum Neustart ab und steht im Log).
/// Die Ergebnisse weichen nur in der Rundung ab (Gleitkomma in anderer Reihenfolge, relativ um 10⁻⁷).
/// </summary>
public static class GpuFilters
{
    private static readonly Lock Gate = new();
    private static Context? _context;
    private static Accelerator? _accelerator;
    private static bool _tried;

    /// <summary>Grafikkarte benutzen, wenn vorhanden.</summary>
    public static bool Enabled { get; set; } = true;

    private static readonly AsyncLocal<bool> CpuOnlyHere = new();

    /// <summary>
    /// Nur in diesem Ablauf auf der CPU rechnen (Vergleich Grafikkarte/CPU in Tests) — andere, die gleichzeitig rechnen,
    /// bleiben auf der Grafikkarte.
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

    /// <summary>Nur in diesem Ablauf auf der Grafikkarte rechnen, auch wenn der Geschwindigkeitstest die CPU wählte.</summary>
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
    /// Letzter Geschwindigkeitstest (Einstellungen). War die CPU auf diesem Rechner schneller, rechnen die Filter dort —
    /// die Grafikkarte bleibt geöffnet, falls ein neuer Test anders ausfällt.
    /// </summary>
    public static SpeedMeasure? Measured { get; set; }

    /// <summary>Die Grafikkarte ist offen und laut Test (oder ohne Test) die schnellere Wahl.</summary>
    public static bool UsesGpu => Enabled && DeviceName is { } device && SpeedMeasure.PreferGpu(Measured, SpeedTest.FilterMachine(device));

    /// <summary>Name der Grafikkarte, sobald sie geöffnet ist; null ohne.</summary>
    public static string? DeviceName { get; private set; }

    /// <summary>Warum die Grafikkarte nicht (mehr) benutzt wird, sonst null.</summary>
    public static string? Problem { get; private set; }

    /// <summary>
    /// Kurz nach dem Start im Hintergrund: Grafikkarte öffnen und die Kernel übersetzen (an einem kleinen Bild) — sonst
    /// wartete die erste Vorschau mit Rauschen oder Dunst darauf.
    /// </summary>
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
    /// <paramref name="work"/> auf der Grafikkarte ausführen (nacheinander, nie gleichzeitig — Vorschau, Schätzung und
    /// Export teilen sich eine). false: keine Grafikkarte oder Fehler — dann auf der CPU rechnen.
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
                _tried = true;   // nicht wieder öffnen
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
            // Erste echte Grafikkarte (CUDA vor OpenCL); die CPU-Nachbildung von ILGPU ist langsamer als unsere Schleifen
            var device = _context.Devices.Where(d => d.AcceleratorType is AcceleratorType.Cuda or AcceleratorType.OpenCL)
                .OrderBy(d => d.AcceleratorType == AcceleratorType.Cuda ? 0 : 1)
                .FirstOrDefault();
            if (device is null)
            {
                Problem = Tr.T("Keine Grafikkarte mit OpenCL oder CUDA gefunden.");
                return null;
            }
            _accelerator = device.CreateAccelerator(_context);
            DeviceName = device.Name;
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

    // ------------------------------------------------------------ Entrauschen (Non-Local Means)

    private static Action<Index1D, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, NlmParams>? _nlm;

    /// <summary>Feste Werte des Non-Local-Means-Kernels.</summary>
    internal readonly record struct NlmParams(int Width, int Height, int PatchRadius, int SearchRadius, float Mix);

    /// <summary>
    /// Non-Local Means auf der Helligkeit wie <see cref="Denoiser"/>: je Punkt alle Verschiebungen im Suchfenster, Abstand
    /// der Umgebungen als Mittel der quadrierten Differenzen (am Rand nur, was im Bild liegt), Gewicht aus dem Rauschen,
    /// der Punkt selbst mit dem größten Gewicht, mit der Stärke gemischt. null: auf der CPU rechnen.
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
