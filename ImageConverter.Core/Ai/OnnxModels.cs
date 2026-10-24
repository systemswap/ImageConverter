using ImageConverter.Core.Platform;
using Microsoft.ML.OnnxRuntime;

namespace ImageConverter.Core.Ai;

/// <summary>
/// Cache of open ONNX Runtime sessions, one per model file.
/// </summary>
/// <remarks>
/// <para>
/// Loading a large model takes seconds and occupies hundreds of MB, so only one large model is kept open at a time
/// (all files of that model, plus the small helper models such as face detection). <see cref="Get"/>, <see cref="Release"/>
/// and <see cref="Dispose"/> are synchronised with a lock.
/// </para>
/// <para>
/// Graphics card (<see cref="UseGpu"/>): on Windows through DirectML (any vendor, DirectX 12); on Linux a separate
/// runtime with MIGraphX/ROCm (AMD), CUDA (NVIDIA) or WebGPU is used in the same way if it is installed in
/// <c>Tools/OnnxRuntime</c>. If opening on the graphics card fails, the model runs on the CPU (<see cref="GpuProblem"/>).
/// Only models with <see cref="AiModel.Gpu"/> set are opened on the graphics card.
/// </para>
/// </remarks>
public sealed class OnnxSessions : IDisposable
{
    /// <summary>
    /// Gets the graphics card execution provider of the runtime (preferred in the order DirectML, MIGraphX, ROCm, CUDA,
    /// WebGPU), or null if there is none or the runtime cannot be loaded.
    /// </summary>
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
            return null;   // runtime missing (for example tests without the native library)
        }
    }

    /// <summary>Gets the readable name of the execution provider ("DirectML" …), or null without one.</summary>
    public static string? GpuName => GpuProvider switch
    {
        "DmlExecutionProvider" => "DirectML",
        "MIGraphXExecutionProvider" => "AMD MIGraphX",
        "ROCMExecutionProvider" => "AMD ROCm",
        "CUDAExecutionProvider" => "NVIDIA CUDA",
        "WebGpuExecutionProvider" => "WebGPU",
        _ => null,
    };

    /// <summary>
    /// Gets or sets whether models run on the graphics card when possible. Changing the value closes the open models
    /// (they are reopened on the next <see cref="Get"/>) and clears <see cref="GpuProblem"/>.
    /// </summary>
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
    /// Gets or sets the last speed test (settings). If the CPU was faster on this computer, the models open on the CPU.
    /// Changing the value closes the open models only if it changes that choice (they are reopened on the next
    /// <see cref="Get"/>).
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

    /// <summary>The graphics card according to the test (or without a test).</summary>
    private bool PreferGpu => SpeedMeasure.PreferGpu(Measured, SpeedTest.AiMachine(GpuProvider));

    /// <summary>Gets whether, according to the speed test, the CPU is faster on this computer (false without a graphics card provider).</summary>
    public bool CpuFasterHere => GpuProvider is not null && !PreferGpu;

    /// <summary>
    /// Gets whether models cleared for the graphics card (<see cref="AiModel.Gpu"/>) currently run there: enabled,
    /// provider present, no error while opening and preferred by the speed test.
    /// </summary>
    public bool OnGpu => UseGpu && GpuProvider is not null && GpuProblem is null && PreferGpu;

    /// <summary>Gets why the graphics card is not used (error message from opening a model), otherwise null.</summary>
    public string? GpuProblem { get; private set; }

    private readonly Dictionary<string, InferenceSession> _open = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _modelOf = new(StringComparer.Ordinal);   // path → model ID
    private readonly Lock _lock = new();

    /// <summary>Creates an empty session cache.</summary>
    /// <remarks>
    /// When the CPU limit changes, the thread count takes effect only when a session is opened, so the open sessions are
    /// reopened on the next <see cref="Get"/>. They are not closed immediately, because a running job may still be
    /// using them.
    /// </remarks>
    public OnnxSessions() => ComputeLimits.Changed += (_, _) => _stale = true;

    private volatile bool _stale;

    /// <summary>Returns the open session for a model file, opening it if necessary.</summary>
    /// <param name="model">Catalogue entry of the model.</param>
    /// <param name="path">Path of the model file (<see cref="AiModelStore.PathOf"/> or a file derived from it).</param>
    /// <returns>The session; it is owned by this cache and must not be disposed by the caller.</returns>
    /// <remarks>
    /// Opening a file of a model that is not a helper closes the files of all other large models. Opening takes seconds
    /// for large models.
    /// </remarks>
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
                // Close other large models; files of the same model (SyntheticDoc: with and without shadow) stay open
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

    /// <summary>
    /// Closes all open files of a model, for example after upscaling: PiSA-SR otherwise holds about 3.6 GB while the
    /// result is assembled and exported. The next run reopens it (a few seconds).
    /// </summary>
    /// <param name="modelId">ID of the model (<see cref="AiModel.Id"/>).</param>
    public void Release(string modelId)
    {
        lock (_lock)
        {
            foreach (var key in _modelOf.Where(p => p.Value == modelId).Select(p => p.Key).ToList())
            {
                _open[key].Dispose();
                _open.Remove(key);
                _modelOf.Remove(key);
            }
        }
    }

    /// <summary>Opens a session on the graphics card if enabled, available and allowed for the model; if that fails, on the CPU.</summary>
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

    /// <summary>Options for the CPU: as many threads as the limit allows (Settings › Performance).</summary>
    internal static SessionOptions CpuOptions()
    {
        var o = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
        if (ComputeLimits.OnnxThreads is { } threads) o.IntraOpNumThreads = threads;
        return o;
    }

    internal static SessionOptions GpuOptions(string provider)
    {
        var o = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
        // Even on the graphics card, part of the work runs on the CPU (shape computations, transfers)
        if (ComputeLimits.OnnxThreads is { } threads) o.IntraOpNumThreads = threads;
        switch (provider)
        {
            case "DmlExecutionProvider":
                // DirectML requires memory patterns off and sequential execution
                o.EnableMemoryPattern = false;
                o.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
                o.AppendExecutionProvider_DML(0);
                break;
            case "MIGraphXExecutionProvider": o.AppendExecutionProvider_MIGraphX(0); break;
            case "ROCMExecutionProvider": o.AppendExecutionProvider_ROCm(0); break;
            case "CUDAExecutionProvider": o.AppendExecutionProvider_CUDA(0); break;
            // preserveDevice: otherwise closing crashes when several sessions live on the card (PiSA-SR: three)
            case "WebGpuExecutionProvider": o.AppendExecutionProvider("WebGPU", new Dictionary<string, string> { ["preserveDevice"] = "1" }); break;
        }
        return o;
    }

    private static bool _telemetryOff;

    /// <summary>
    /// No usage data to Microsoft: sets the environment variable before the runtime starts (in case
    /// <see cref="NativeLibraries.Configure"/> did not run, for example in tests), then also disables it through the API.
    /// See docs/FEHLERGEDAECHTNIS.md, No. 1.
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

    /// <summary>Small helper models stay open and do not close others.</summary>
    private static bool StaysOpen(AiModelKind kind) => kind is AiModelKind.Helper;

    /// <summary>Closes all open sessions. The cache stays usable; the next <see cref="Get"/> opens them again.</summary>
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
    /// Runs the model once and returns the first output as a float array; <paramref name="ct"/> also stops a running
    /// computation (then <see cref="OperationCanceledException"/> is thrown).
    /// </summary>
    /// <remarks>
    /// The run options are held in a <c>using</c> variable on purpose. Created as a temporary in the call, the garbage
    /// collector may free them while ONNX Runtime is still computing, causing an access violation that .NET cannot catch.
    /// See docs/FEHLERGEDAECHTNIS.md, No. 69.
    /// </remarks>
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

    /// <summary>Like <see cref="Run(InferenceSession, IReadOnlyDictionary{string, OrtValue}, CancellationToken)"/>, but returns the named outputs (SyntheticDoc: grid and shadow map).</summary>
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

    /// <summary>Like <see cref="Run(InferenceSession, IReadOnlyDictionary{string, OrtValue}, CancellationToken)"/>, but for models with byte output (MI-GAN).</summary>
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

    /// <summary>Returns all outputs (for the face detector with twelve outputs); cannot be cancelled.</summary>
    internal static float[][] RunAll(InferenceSession session, string inputName, OrtValue input)
    {
        using var runOptions = new RunOptions();
        using var outputs = session.Run(runOptions, [inputName], [input], session.OutputNames);
        return outputs.Select(o => o.GetTensorDataAsSpan<float>().ToArray()).ToArray();
    }
}

/// <summary>Tile model through ONNX Runtime (Real-ESRGAN and similar): NCHW, RGB, 0…1.</summary>
/// <param name="session">Open session of the model; it stays owned by the caller.</param>
/// <param name="model">Catalogue entry that supplies scale and tile size.</param>
public sealed class OnnxTileModel(InferenceSession session, AiModel model) : ITileModel
{
    /// <inheritdoc/>
    public int Scale => model.Scale;

    /// <inheritdoc/>
    public int TileSize => model.TileSize;

    /// <inheritdoc/>
    public float[] Run(float[] input, int width, int height, CancellationToken ct)
    {
        using var value = OrtValue.CreateTensorValueFromMemory(input, [1, 3, height, width]);
        return OnnxSessions.Run(session, new Dictionary<string, OrtValue> { [session.InputNames[0]] = value }, ct);
    }
}

/// <summary>Face model through ONNX Runtime (GFPGAN, CodeFormer, RestoreFormer++): 512 × 512, RGB, −1…1.</summary>
/// <param name="session">Open session of the model; it stays owned by the caller.</param>
/// <param name="model">Catalogue entry that supplies the crop size.</param>
public sealed class OnnxFaceModel(InferenceSession session, AiModel model) : IFaceModel
{
    /// <summary>
    /// Gets the CodeFormer fidelity weight: 0 = strongest restoration, 1 = as close to the original as possible.
    /// Used only by models with a "weight" input.
    /// </summary>
    public double Fidelity { get; init; } = 0.6;

    /// <inheritdoc/>
    public int Size => model.TileSize;

    /// <inheritdoc/>
    public float[] Run(float[] input, CancellationToken ct)
    {
        using var image = OrtValue.CreateTensorValueFromMemory(input, [1, 3, Size, Size]);
        // Image input: "input" for GFPGAN/CodeFormer; for other models the first one not named "weight"
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
/// Inpainting model through ONNX Runtime.
/// </summary>
/// <remarks>
/// LaMa: image and mask as float (image 0 … 1, mask 1 = fill), result 0 … 255. The area to be filled must already be
/// cleared in the image; otherwise LaMa repaints the old content translucently. MI-GAN (pipeline export): bytes, mask
/// inverted (255 = keep, 0 = fill), result 0 … 255. <see cref="Run"/> converts both to the 0 … 1 layout of
/// <see cref="IInpaintModel"/>. See docs/FEHLERGEDAECHTNIS.md, No. 35.
/// </remarks>
/// <param name="session">Open session of the model; it stays owned by the caller.</param>
/// <param name="model">Catalogue entry that supplies the crop size.</param>
public sealed class OnnxInpaintModel(InferenceSession session, AiModel model) : IInpaintModel
{
    /// <inheritdoc/>
    public int Size => model.TileSize;

    /// <inheritdoc/>
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
