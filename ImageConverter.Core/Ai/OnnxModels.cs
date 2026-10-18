using ImageConverter.Core.Platform;
using Microsoft.ML.OnnxRuntime;

namespace ImageConverter.Core.Ai;

/// <summary>
/// Geöffnete Modelle. Das Laden großer Modelle dauert Sekunden und belegt Hunderte MB —
/// deshalb bleibt immer nur ein großes Modell (plus die kleine Gesichtserkennung) offen.
/// <para>
/// Grafikkarte (<see cref="UseGpu"/>): Windows über DirectML (jeder Hersteller, DirectX 12); eine eigene Laufzeit unter
/// Linux mit MIGraphX/ROCm (AMD), CUDA (NVIDIA) oder WebGPU wird ebenso genutzt, wenn sie in <c>Tools/OnnxRuntime</c>
/// liegt. Geht das Öffnen auf der Grafikkarte schief, rechnet das Modell auf der CPU (<see cref="GpuProblem"/>).
/// </para>
/// </summary>
public sealed class OnnxSessions : IDisposable
{
    /// <summary>Was die Laufzeit an Grafikkarten-Anbindung mitbringt (in dieser Reihenfolge bevorzugt), sonst null.</summary>
    public static string? GpuProvider { get; } = FindGpuProvider();

    private static string? FindGpuProvider()
    {
        try
        {
            EnsureNoTelemetry();
            var available = OrtEnv.Instance().GetAvailableProviders();
            return new[] { "DmlExecutionProvider", "MIGraphXExecutionProvider", "ROCMExecutionProvider", "CUDAExecutionProvider", "WebGpuExecutionProvider" }
                .FirstOrDefault(available.Contains);
        }
        catch (Exception)
        {
            return null;   // Laufzeit fehlt (z. B. Tests ohne native Bibliothek)
        }
    }

    /// <summary>Lesbarer Name der Anbindung („DirectML“ …), null ohne.</summary>
    public static string? GpuName => GpuProvider switch
    {
        "DmlExecutionProvider" => "DirectML",
        "MIGraphXExecutionProvider" => "AMD MIGraphX",
        "ROCMExecutionProvider" => "AMD ROCm",
        "CUDAExecutionProvider" => "NVIDIA CUDA",
        "WebGpuExecutionProvider" => "WebGPU",
        _ => null,
    };

    /// <summary>Auf der Grafikkarte rechnen, wenn möglich. Ändern schließt die offenen Modelle (werden neu geöffnet).</summary>
    public bool UseGpu
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            GpuProblem = null;
            Dispose();
        }
    } = true;

    /// <summary>
    /// Letzter Geschwindigkeitstest (Einstellungen). War die CPU auf diesem Rechner schneller, öffnen die Modelle auf der
    /// CPU. Ändern schließt die offenen Modelle (werden neu geöffnet).
    /// </summary>
    public SpeedMeasure? Measured
    {
        get;
        set
        {
            if (field == value) return;
            var before = PreferGpu;
            field = value;
            if (before != PreferGpu) Dispose();
        }
    }

    /// <summary>Laut Test (oder ohne Test) die Grafikkarte.</summary>
    private bool PreferGpu => SpeedMeasure.PreferGpu(Measured, SpeedTest.AiMachine(GpuProvider));

    /// <summary>Laut Geschwindigkeitstest rechnet auf diesem Rechner die CPU schneller.</summary>
    public bool CpuFasterHere => GpuProvider is not null && !PreferGpu;

    /// <summary>Rechnen die offenen Modelle gerade auf der Grafikkarte?</summary>
    public bool OnGpu => UseGpu && GpuProvider is not null && GpuProblem is null && PreferGpu;

    /// <summary>Warum die Grafikkarte nicht genutzt wird (Fehler beim Öffnen), sonst null.</summary>
    public string? GpuProblem { get; private set; }

    private readonly Dictionary<string, InferenceSession> _open = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _modelOf = new(StringComparer.Ordinal);   // Pfad → Modell-Id
    private readonly Lock _lock = new();

    // Obergrenze geändert: die Stränge gelten erst beim Öffnen — beim nächsten Holen neu öffnen (nicht sofort schließen,
    // ein laufender Auftrag rechnet vielleicht noch mit den offenen)
    public OnnxSessions() => ComputeLimits.Changed += (_, _) => _stale = true;

    private volatile bool _stale;

    public InferenceSession Get(AiModel model, string path)
    {
        lock (_lock)
        {
            if (_stale)
            {
                _stale = false;
                foreach (var s in _open.Values) s.Dispose();
                _open.Clear();
                _modelOf.Clear();
            }
            if (_open.TryGetValue(path, out var session)) return session;

            if (!StaysOpen(model.Kind))
            {
                // anderes großes Modell schließen; Dateien desselben Modells (SyntheticDoc: mit und ohne Schatten) bleiben
                foreach (var (key, other) in _open.Where(p => !IsHelper(p.Key) && _modelOf.GetValueOrDefault(p.Key) != model.Id).ToList())
                {
                    other.Dispose();
                    _open.Remove(key);
                    _modelOf.Remove(key);
                }
            }

            EnsureNoTelemetry();
            session = Open(path, model.Gpu);
            _open[path] = session;
            _modelOf[path] = model.Id;
            return session;
        }
    }

    /// <summary>Öffnen — mit Grafikkarte, wenn gewünscht, vorhanden und für das Modell freigegeben; scheitert das, auf der CPU.</summary>
    private InferenceSession Open(string path, bool allowGpu)
    {
        if (allowGpu && UseGpu && GpuProvider is { } provider && GpuProblem is null && PreferGpu)
        {
            try
            {
                using var gpu = GpuOptions(provider);
                return new InferenceSession(path, gpu);
            }
            catch (Exception e) when (e is OnnxRuntimeException or EntryPointNotFoundException or DllNotFoundException)
            {
                GpuProblem = e.Message;
            }
        }
        using var cpu = CpuOptions();
        return new InferenceSession(path, cpu);
    }

    /// <summary>Für die CPU: so viele Stränge, wie die Obergrenze erlaubt (Einstellungen › Leistung).</summary>
    internal static SessionOptions CpuOptions()
    {
        var o = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
        if (ComputeLimits.OnnxThreads is { } threads) o.IntraOpNumThreads = threads;
        return o;
    }

    internal static SessionOptions GpuOptions(string provider)
    {
        var o = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
        // auch auf der Grafikkarte rechnet ein Teil auf der CPU (Formen, Übergänge)
        if (ComputeLimits.OnnxThreads is { } threads) o.IntraOpNumThreads = threads;
        switch (provider)
        {
            case "DmlExecutionProvider":
                // DirectML verlangt: kein Speichermuster, der Reihe nach
                o.EnableMemoryPattern = false;
                o.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
                o.AppendExecutionProvider_DML(0);
                break;
            case "MIGraphXExecutionProvider": o.AppendExecutionProvider_MIGraphX(0); break;
            case "ROCMExecutionProvider": o.AppendExecutionProvider_ROCm(0); break;
            case "CUDAExecutionProvider": o.AppendExecutionProvider_CUDA(0); break;
            // preserveDevice: sonst stürzt das Schließen ab, wenn mehrere Sitzungen auf der Karte liegen (PiSA-SR: drei)
            case "WebGpuExecutionProvider": o.AppendExecutionProvider("WebGPU", new Dictionary<string, string> { ["preserveDevice"] = "1" }); break;
        }
        return o;
    }

    private static bool _telemetryOff;

    /// <summary>
    /// Keine Nutzungsdaten an Microsoft: die Variable vor dem Start der Laufzeit (falls
    /// <see cref="NativeLibraries.Configure"/> nicht lief, z. B. in Tests), danach auch über die API.
    /// </summary>
    private static void EnsureNoTelemetry()
    {
        if (_telemetryOff) return;
        NativeLibraries.DisableOnnxTelemetry();
        OrtEnv.Instance().DisableTelemetryEvents();
        _telemetryOff = true;
    }

    private static bool IsHelper(string path) =>
        AiModelCatalog.All.Any(m => StaysOpen(m.Kind) && path.EndsWith(m.MainFile, StringComparison.Ordinal));

    /// <summary>Kleine Hilfsmodelle bleiben offen und schließen keine anderen.</summary>
    private static bool StaysOpen(AiModelKind kind) => kind is AiModelKind.Helper;

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var s in _open.Values) s.Dispose();
            _open.Clear();
            _modelOf.Clear();
        }
    }

    /// <summary>
    /// Ein Durchlauf; <paramref name="ct"/> bricht auch eine laufende Berechnung ab.
    /// Liefert die erste Ausgabe als float-Array.
    /// </summary>
    internal static float[] Run(InferenceSession session, IReadOnlyDictionary<string, OrtValue> inputs, CancellationToken ct)
    {
        using var runOptions = new RunOptions();
        using var registration = ct.Register(() => runOptions.Terminate = true);
        try
        {
            var names = inputs.Keys.ToArray();
            using var outputs = session.Run(runOptions, names, names.Select(n => inputs[n]).ToArray(),
                [session.OutputNames[0]]);
            return outputs[0].GetTensorDataAsSpan<float>().ToArray();
        }
        catch (OnnxRuntimeException) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }
    }

    /// <summary>Wie <see cref="Run"/>, aber die genannten Ausgaben (SyntheticDoc: Gitter und Schattenkarte).</summary>
    internal static float[][] Run(InferenceSession session, IReadOnlyDictionary<string, OrtValue> inputs, IReadOnlyList<string> outputNames,
        CancellationToken ct)
    {
        using var runOptions = new RunOptions();
        using var registration = ct.Register(() => runOptions.Terminate = true);
        try
        {
            var names = inputs.Keys.ToArray();
            using var outputs = session.Run(runOptions, names, names.Select(n => inputs[n]).ToArray(), outputNames);
            return outputs.Select(o => o.GetTensorDataAsSpan<float>().ToArray()).ToArray();
        }
        catch (OnnxRuntimeException) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }
    }

    /// <summary>Wie <see cref="Run"/>, aber für Modelle mit Byte-Ausgabe (MI-GAN).</summary>
    internal static byte[] RunBytes(InferenceSession session, IReadOnlyDictionary<string, OrtValue> inputs, CancellationToken ct)
    {
        using var runOptions = new RunOptions();
        using var registration = ct.Register(() => runOptions.Terminate = true);
        try
        {
            var names = inputs.Keys.ToArray();
            using var outputs = session.Run(runOptions, names, names.Select(n => inputs[n]).ToArray(),
                [session.OutputNames[0]]);
            return outputs[0].GetTensorDataAsSpan<byte>().ToArray();
        }
        catch (OnnxRuntimeException) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }
    }

    /// <summary>Alle Ausgaben (für die Gesichtserkennung mit zwölf Ausgaben).</summary>
    internal static float[][] RunAll(InferenceSession session, string inputName, OrtValue input)
    {
        using var runOptions = new RunOptions();
        using var outputs = session.Run(runOptions, [inputName], [input], session.OutputNames);
        return outputs.Select(o => o.GetTensorDataAsSpan<float>().ToArray()).ToArray();
    }
}

/// <summary>Kachelmodell über ONNX (Real-ESRGAN u. ä.): NCHW, RGB, 0…1.</summary>
public sealed class OnnxTileModel(InferenceSession session, AiModel model) : ITileModel
{
    public int Scale => model.Scale;
    public int TileSize => model.TileSize;

    public float[] Run(float[] input, int width, int height, CancellationToken ct)
    {
        using var value = OrtValue.CreateTensorValueFromMemory(input, [1, 3, height, width]);
        return OnnxSessions.Run(session, new Dictionary<string, OrtValue> { [session.InputNames[0]] = value }, ct);
    }
}

/// <summary>Gesichtsmodell über ONNX (GFPGAN, CodeFormer, RestoreFormer++): 512 × 512, RGB, −1…1.</summary>
public sealed class OnnxFaceModel(InferenceSession session, AiModel model) : IFaceModel
{
    /// <summary>CodeFormer: 0 = stärkste Verbesserung, 1 = möglichst nah am Original.</summary>
    public double Fidelity { get; init; } = 0.6;

    public int Size => model.TileSize;

    public float[] Run(float[] input, CancellationToken ct)
    {
        using var image = OrtValue.CreateTensorValueFromMemory(input, [1, 3, Size, Size]);
        // Bildeingang: „input“ bei GFPGAN/CodeFormer — bei anderen Modellen der erste, der nicht „weight“ heißt
        var name = session.InputNames.First(n => n != "weight");
        var inputs = new Dictionary<string, OrtValue> { [name] = image };

        OrtValue? weight = null;
        try
        {
            if (session.InputNames.Contains("weight"))
                inputs["weight"] = weight = OrtValue.CreateTensorValueFromMemory(new[] { Fidelity }, []);
            return OnnxSessions.Run(session, inputs, ct);
        }
        finally
        {
            weight?.Dispose();
        }
    }
}

/// <summary>
/// Füllmodell über ONNX. LaMa: Bild und Maske als float (Bild 0 … 1, Maske 1 = füllen), Ergebnis 0 … 255; die zu
/// füllende Fläche muss im Bild schon gelöscht sein, sonst malt LaMa das Alte durchscheinend nach.
/// MI-GAN (Pipeline): Bytes, Maske umgekehrt (255 = bleibt, 0 = füllen), Ergebnis 0 … 255.
/// </summary>
public sealed class OnnxInpaintModel(InferenceSession session, AiModel model) : IInpaintModel
{
    public int Size => model.TileSize;

    public float[] Run(float[] rgb, float[] mask, CancellationToken ct)
    {
        var s = Size;
        var bytes = session.InputMetadata["image"].ElementType == typeof(byte);
        if (!bytes)
        {
            var image = new float[rgb.Length];
            for (var c = 0; c < 3; c++)
            for (var i = 0; i < s * s; i++)
                image[c * s * s + i] = mask[i] > 0 ? 0 : rgb[c * s * s + i];
            using var a = OrtValue.CreateTensorValueFromMemory(image, [1, 3, s, s]);
            using var b = OrtValue.CreateTensorValueFromMemory(mask, [1, 1, s, s]);
            var output = OnnxSessions.Run(session, new Dictionary<string, OrtValue> { ["image"] = a, ["mask"] = b }, ct);
            for (var i = 0; i < output.Length; i++) output[i] /= 255f;
            return output;
        }

        var image8 = new byte[rgb.Length];
        for (var i = 0; i < rgb.Length; i++) image8[i] = (byte)Math.Clamp(rgb[i] * 255f + 0.5f, 0, 255);
        var mask8 = new byte[mask.Length];
        for (var i = 0; i < mask.Length; i++) mask8[i] = mask[i] > 0 ? (byte)0 : (byte)255;
        using var ia = OrtValue.CreateTensorValueFromMemory(image8, [1, 3, s, s]);
        using var ib = OrtValue.CreateTensorValueFromMemory(mask8, [1, 1, s, s]);
        var result = OnnxSessions.RunBytes(session, new Dictionary<string, OrtValue> { ["image"] = ia, ["mask"] = ib }, ct);
        var outFloat = new float[result.Length];
        for (var i = 0; i < result.Length; i++) outFloat[i] = result[i] / 255f;
        return outFloat;
    }
}
