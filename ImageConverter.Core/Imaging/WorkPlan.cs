namespace ImageConverter.Core.Imaging;

/// <summary>Progress of a longer image computation: the completed fraction and the step that is running.</summary>
/// <param name="Fraction">Completed fraction of the total estimated work, 0…1.</param>
/// <param name="Step">Display text of the step that is running.</param>
public readonly record struct WorkProgress(double Fraction, string Step);

/// <summary>
/// Reports progress across all steps of a computation (scaling, dehaze per pass, saving …).
/// </summary>
/// <remarks>
/// <para>The steps are registered in advance with their estimated cost. The progress bar therefore runs once from 0 to
/// 100 % instead of restarting with every step, and the text shows what is happening.</para>
/// <para>The cost constants are in seconds per megapixel, measured on 16 cores in a Release build. They need not be
/// exact; they only distribute the bar across the steps.</para>
/// <para>The class has no synchronisation; <see cref="Add"/> and <see cref="Next"/> must not be called concurrently.</para>
/// </remarks>
/// <param name="sink">Receives the progress; <see langword="null"/> discards it.</param>
/// <param name="cancel">Checked between two steps, so that the computation can stop there (the preview cancels when the
/// slider keeps moving).</param>
public sealed class WorkPlan(IProgress<WorkProgress>? sink, CancellationToken cancel = default)
{
    // Cost per megapixel in seconds
    internal const double ResizeCost = 0.05;
    /// <summary>Cost of the noise reduction in seconds per megapixel (measured for Non-Local Means on the luminance:
    /// about 0.7 s per megapixel).</summary>
    public const double DenoiseCost = 0.7;
    internal const double DehazePassCost = 0.24;
    internal const double ToneMapCost = 0.15;
    internal const double ToolsCost = 0.03;
    internal const double SharpenCost = 0.03;
    /// <summary>Cost of the film grain in seconds per megapixel.</summary>
    public const double GrainCost = 0.06;
    internal const double ColorCost = 0.02;

    private readonly List<(string Text, double Weight)> _steps = [];
    private double _total;
    private double _before;   // Cost of the completed steps
    private int _index = -1;

    /// <summary>Registers a step; the order must match the order of execution.</summary>
    /// <param name="text">Display text of the step.</param>
    /// <param name="weight">Estimated cost (seconds); values below 1e-6 are raised to 1e-6.</param>
    public void Add(string text, double weight)
    {
        _steps.Add((text, Math.Max(weight, 1e-6)));
        _total += Math.Max(weight, 1e-6);
    }

    /// <summary>Starts the next registered step and reports the progress up to it.</summary>
    /// <remarks>Calls beyond the last registered step report nothing.</remarks>
    /// <exception cref="OperationCanceledException">Cancellation was requested through the token passed to the
    /// constructor.</exception>
    public void Next()
    {
        cancel.ThrowIfCancellationRequested();
        if (_index >= 0 && _index < _steps.Count) _before += _steps[_index].Weight;
        _index++;
        if (_index < _steps.Count) sink?.Report(new WorkProgress(_before / _total, _steps[_index].Text));
    }

    /// <summary>Reports completion (fraction 1).</summary>
    /// <param name="text">Display text for the finished state.</param>
    public void Done(string text) => sink?.Report(new WorkProgress(1, text));
}
