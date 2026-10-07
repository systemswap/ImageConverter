using ImageConverter.Core.Platform;
using Microsoft.ML.OnnxRuntime;

namespace ImageConverter.Core.Ai;

/// <summary>
/// Geöffnete Modelle. Das Laden großer Modelle dauert Sekunden und belegt Hunderte MB —
/// deshalb bleibt immer nur ein großes Modell (plus die kleine Gesichtserkennung) offen.
/// </summary>
public sealed class OnnxSessions : IDisposable
{
    private readonly Dictionary<string, InferenceSession> _open = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    public InferenceSession Get(AiModel model, string path)
    {
        lock (_lock)
        {
            if (_open.TryGetValue(path, out var session)) return session;

            if (model.Kind != AiModelKind.Helper)
            {
                // anderes großes Modell schließen
                foreach (var (key, other) in _open.Where(p => !IsHelper(p.Key)).ToList())
                {
                    other.Dispose();
                    _open.Remove(key);
                }
            }

            EnsureNoTelemetry();
            using var options = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
            session = new InferenceSession(path, options);
            _open[path] = session;
            return session;
        }
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
        AiModelCatalog.All.Any(m => m.Kind == AiModelKind.Helper && path.EndsWith(m.MainFile, StringComparison.Ordinal));

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var s in _open.Values) s.Dispose();
            _open.Clear();
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

/// <summary>Gesichtsmodell über ONNX (GFPGAN, CodeFormer): 512 × 512, RGB, −1…1.</summary>
public sealed class OnnxFaceModel(InferenceSession session, AiModel model) : IFaceModel
{
    /// <summary>CodeFormer: 0 = stärkste Verbesserung, 1 = möglichst nah am Original.</summary>
    public double Fidelity { get; init; } = 0.6;

    public int Size => model.TileSize;

    public float[] Run(float[] input, CancellationToken ct)
    {
        using var image = OrtValue.CreateTensorValueFromMemory(input, [1, 3, Size, Size]);
        var inputs = new Dictionary<string, OrtValue> { ["input"] = image };

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
