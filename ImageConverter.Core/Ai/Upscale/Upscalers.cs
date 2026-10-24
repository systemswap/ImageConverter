namespace ImageConverter.Core.Ai.Upscale;

/// <summary>How the pixels are created when enlarging (the "Upscaling" group under "Size").</summary>
public enum UpscaleMethod
{
    /// <summary>The resampling filter only (Lanczos …), without AI.</summary>
    Filter,

    /// <summary>Real-ESRGAN x4v3: seconds.</summary>
    Fast,

    /// <summary>4x-UltraSharpV2: minutes.</summary>
    Medium,

    /// <summary>PiSA-SR: invents fine details, very slow.</summary>
    Generative,
}

/// <summary>Number of AI passes and size of the image that the last pass enlarges ×4.</summary>
/// <param name="Passes">1 or 2.</param>
/// <param name="BaseWidth">Width of the image the last pass enlarges, in pixels.</param>
/// <param name="BaseHeight">Height of the image the last pass enlarges, in pixels.</param>
public readonly record struct UpscalePlan(int Passes, int BaseWidth, int BaseHeight);

/// <summary>Choice of upscaling model, pass planning and computing-time estimates.</summary>
/// <remarks>The learned timings are guarded by a lock; <see cref="OnGpu"/> is a plain static property.</remarks>
public static class Upscalers
{
    /// <summary>Returns the model for a method.</summary>
    /// <param name="method">Upscaling method.</param>
    /// <returns>The catalogue entry, or null for <see cref="UpscaleMethod.Filter"/>.</returns>
    public static AiModel? ModelOf(UpscaleMethod method) => method switch
    {
        UpscaleMethod.Fast => AiModelCatalog.Allround,
        UpscaleMethod.Medium => AiModelCatalog.UltraSharpV2,
        UpscaleMethod.Generative => AiModelCatalog.PiSaSr,
        _ => null,
    };

    // Defaults: seconds per megapixel of the original, measured on a 500 × 300 → 2000 × 1200 crop of the "Panorama" test
    // photo on an 8-core laptop processor (CPU: x4v3 3.5 s, UltraSharpV2 2 min, PiSA-SR 6 min; GPU = its integrated graphics through
    // DirectML). The estimate uses the computing effort (tiles including overlap, Work) instead of the area: for small
    // images the tiles overlap proportionally more, and the area alone was wrong there. Values measured on this computer
    // (speed test, every run) replace the defaults and are saved (Learned/Restore); otherwise a fast computer would
    // estimate with the laptop values after every start (16-core desktop processor: PiSA-SR about 6× as fast).
    // See docs/FEHLERGEDAECHTNIS.md, No. 73.
    private static readonly Dictionary<(UpscaleMethod, bool Gpu), double> DefaultSecondsPerMegapixel = new()
    {
        [(UpscaleMethod.Fast, false)] = 23,
        [(UpscaleMethod.Medium, false)] = 800,
        [(UpscaleMethod.Generative, false)] = 2370,
        [(UpscaleMethod.Fast, true)] = 13,          // GPU: 2.0 s, 80 s, 97 s (fp16) for 0.15 MP
        [(UpscaleMethod.Medium, true)] = 530,
        [(UpscaleMethod.Generative, true)] = 650,
    };

    /// <summary>Seconds per unit of effort (<see cref="Work"/>) per method and device.</summary>
    private static readonly Dictionary<(UpscaleMethod, bool Gpu), double> SecondsPerWork =
        DefaultSecondsPerMegapixel.ToDictionary(p => p.Key, p => p.Value * 0.15 / Work(p.Key.Item1, 500, 300));

    /// <summary>Returns the computing effort for an original of this size: tiles (tile models) or tile areas (PiSA-SR).</summary>
    /// <param name="method">Upscaling method.</param>
    /// <param name="width">Width of the original in pixels.</param>
    /// <param name="height">Height of the original in pixels.</param>
    /// <returns>Effort in method-specific units; 0 for <see cref="UpscaleMethod.Filter"/>.</returns>
    public static double Work(UpscaleMethod method, int width, int height) => method switch
    {
        UpscaleMethod.Generative => PiSaSrRun.Work(width, height),
        UpscaleMethod.Fast or UpscaleMethod.Medium => UpscaleEngine.TileCount(width, height, ModelOf(method)!.TileSize),
        _ => 0,
    };

    /// <summary>
    /// Gets or sets whether the models currently run on the graphics card; selects the timing values. Set by the user
    /// interface according to the settings.
    /// </summary>
    public static bool OnGpu { get; set; }

    /// <summary>Estimates the computing time for an original of this size (without one-time conversion or loading).</summary>
    /// <param name="method">Upscaling method.</param>
    /// <param name="width">Width of the original in pixels.</param>
    /// <param name="height">Height of the original in pixels.</param>
    /// <returns>The estimate for one pass; zero for <see cref="UpscaleMethod.Filter"/> or an empty size.</returns>
    public static TimeSpan Estimate(UpscaleMethod method, int width, int height)
    {
        if (method == UpscaleMethod.Filter || width <= 0 || height <= 0) return TimeSpan.Zero;
        lock (SecondsPerWork) return TimeSpan.FromSeconds(SecondsPerWork[(method, OnGpu)] * Work(method, width, height));
    }

    /// <summary>
    /// Plans the computation: one AI pass (×4, the filter does the rest) or, beyond 4× and if requested, two.
    /// </summary>
    /// <remarks>
    /// For the second pass the ×4 intermediate image is first reduced to a quarter of the target (at most ×4 of the
    /// image), so the second pass lands exactly on the target and computes no more than necessary. The base is the image
    /// that the last pass enlarges. See docs/FEHLERGEDAECHTNIS.md, No. 68.
    /// </remarks>
    /// <param name="width">Width of the original in pixels.</param>
    /// <param name="height">Height of the original in pixels.</param>
    /// <param name="targetWidth">Target width in pixels.</param>
    /// <param name="targetHeight">Target height in pixels.</param>
    /// <param name="twice">Whether two passes are wanted when the target exceeds 4×.</param>
    /// <returns>The plan.</returns>
    public static UpscalePlan Plan(int width, int height, int targetWidth, int targetHeight, bool twice)
    {
        if (!twice || (targetWidth <= 4 * width && targetHeight <= 4 * height)) return new UpscalePlan(1, width, height);
        return new UpscalePlan(2, Math.Clamp((targetWidth + 3) / 4, width, 4 * width), Math.Clamp((targetHeight + 3) / 4, height, 4 * height));
    }

    /// <summary>Returns whether the target is more than 4× the original along one edge, so that a second pass is possible.</summary>
    /// <param name="width">Width of the original in pixels.</param>
    /// <param name="height">Height of the original in pixels.</param>
    /// <param name="targetWidth">Target width in pixels.</param>
    /// <param name="targetHeight">Target height in pixels.</param>
    /// <returns>True if one edge grows by more than 4×.</returns>
    public static bool BeyondOnePass(int width, int height, int targetWidth, int targetHeight) =>
        targetWidth > 4 * width || targetHeight > 4 * height;

    /// <summary>Estimates the time according to a plan for the image (<paramref name="width"/> × <paramref name="height"/>) or a part of it.</summary>
    /// <param name="method">Upscaling method.</param>
    /// <param name="width">Width of the original in pixels.</param>
    /// <param name="height">Height of the original in pixels.</param>
    /// <param name="plan">Plan from <see cref="Plan"/>.</param>
    /// <param name="partWidth">Width of the part in pixels of the original; 0 = the whole image.</param>
    /// <param name="partHeight">Height of the part in pixels of the original; 0 = the whole image.</param>
    /// <returns>The estimate for all passes.</returns>
    public static TimeSpan Estimate(UpscaleMethod method, int width, int height, UpscalePlan plan, int partWidth = 0, int partHeight = 0)
    {
        if (partWidth <= 0 || partHeight <= 0) (partWidth, partHeight) = (width, height);
        var t = Estimate(method, partWidth, partHeight);
        if (plan.Passes == 2)
            t += Estimate(method, (int)Math.Round(partWidth * (double)plan.BaseWidth / width),
                (int)Math.Round(partHeight * (double)plan.BaseHeight / height));
        return t;
    }

    /// <summary>Keys whose values were measured on this computer (not the defaults).</summary>
    private static readonly HashSet<(UpscaleMethod, bool)> LearnedKeys = [];

    /// <summary>
    /// Records the measured time of a run, refining the estimate; small runs count less (full weight from 0.3 MP).
    /// </summary>
    /// <remarks>
    /// The first measurement on a computer and every one from the speed test (<paramref name="replace"/>) replace the
    /// value completely; otherwise the default from another computer would keep distorting the estimate.
    /// Runs below 0.002 MP and <see cref="UpscaleMethod.Filter"/> are ignored. See docs/FEHLERGEDAECHTNIS.md, No. 73.
    /// </remarks>
    /// <param name="method">Upscaling method.</param>
    /// <param name="width">Width of the original in pixels.</param>
    /// <param name="height">Height of the original in pixels.</param>
    /// <param name="elapsed">Measured time of the pass.</param>
    /// <param name="replace">Whether the value replaces the current one completely.</param>
    public static void Measured(UpscaleMethod method, int width, int height, TimeSpan elapsed, bool replace = false)
    {
        var mp = width * (double)height / 1e6;
        var work = Work(method, width, height);
        if (method == UpscaleMethod.Filter || mp < 0.002 || work <= 0) return;
        var key = (method, OnGpu);
        lock (SecondsPerWork)
        {
            var weight = LearnedKeys.Add(key) || replace ? 1 : Math.Min(1, mp / 0.3);
            SecondsPerWork[key] = SecondsPerWork[key] * (1 - weight) + elapsed.TotalSeconds / work * weight;
        }
    }

    /// <summary>Returns the measured values for saving ("Generative|cpu" → seconds per unit of effort).</summary>
    /// <returns>A new dictionary with only the values measured on this computer.</returns>
    public static Dictionary<string, double> Learned()
    {
        lock (SecondsPerWork)
            return LearnedKeys.ToDictionary(k => $"{k.Item1}|{(k.Item2 ? "gpu" : "cpu")}", k => SecondsPerWork[k]);
    }

    /// <summary>Restores saved values of this computer; unknown or invalid entries are skipped.</summary>
    /// <param name="saved">Values from <see cref="Learned"/>, or null.</param>
    public static void Restore(IReadOnlyDictionary<string, double>? saved)
    {
        if (saved is null) return;
        lock (SecondsPerWork)
            foreach (var (name, seconds) in saved)
            {
                var parts = name.Split('|');
                if (parts.Length != 2 || !Enum.TryParse<UpscaleMethod>(parts[0], out var method) || method == UpscaleMethod.Filter
                    || parts[1] is not ("cpu" or "gpu") || !double.IsFinite(seconds) || seconds <= 0) continue;
                var key = (method, parts[1] == "gpu");
                SecondsPerWork[key] = seconds;
                LearnedKeys.Add(key);
            }
    }

    /// <summary>
    /// Formats a duration together with the expected end time, for example "4 min, done around 15:42" (in the UI language;
    /// with the date if the end falls on another day). Below one minute only the duration.
    /// </summary>
    /// <param name="t">Duration.</param>
    /// <param name="now">Current local time.</param>
    /// <returns>The formatted text.</returns>
    public static string FormatWithEnd(TimeSpan t, DateTime now)
    {
        if (t.TotalSeconds < 60) return Format(t);
        var end = now + t;
        var clock = end.Date == now.Date ? end.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture)
            : end.ToString("dd.MM. HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        return Tr.T("{0}, fertig gegen {1}", Format(t), clock);
    }

    /// <summary>Formats a duration as "3 s", "4 min" or "2 h 10 min".</summary>
    /// <param name="t">Duration.</param>
    /// <returns>The formatted text (at least "1 s").</returns>
    public static string Format(TimeSpan t) =>
        t.TotalSeconds < 60 ? $"{Math.Max(1, Math.Round(t.TotalSeconds)):0} s"
        : t.TotalMinutes < 60 ? $"{Math.Round(t.TotalMinutes):0} min"
        : $"{(int)t.TotalHours} h {t.Minutes:00} min";
}
