namespace ImageConverter.Core.Imaging;

/// <summary>Stand einer längeren Bildrechnung: Anteil 0–1 und der Schritt, der gerade läuft.</summary>
public readonly record struct WorkProgress(double Fraction, string Step);

/// <summary>
/// Fortschritt über alle Schritte einer Rechnung (Skalieren, Dunst je Durchlauf, Speichern …).
/// Die Schritte werden vorab mit ihrem geschätzten Aufwand eingetragen; der Balken läuft so
/// einmal von 0 bis 100 % durch, statt bei jedem Schritt neu anzufangen, und darunter steht,
/// was gerade passiert.
/// </summary>
/// <remarks>
/// Aufwand in Sekunden je Megapixel, gemessen an 0.4.45 (16 Kerne, Release). Die Werte müssen
/// nicht genau stimmen — sie verteilen nur den Balken auf die Schritte.
/// </remarks>
/// <param name="cancel">Abbrechen zwischen zwei Schritten (die Vorschau bricht ab, wenn der Regler weiterläuft).</param>
public sealed class WorkPlan(IProgress<WorkProgress>? sink, CancellationToken cancel = default)
{
    // Aufwand je Megapixel in Sekunden
    internal const double ResizeCost = 0.05;
    public const double DenoiseCost = 0.7;   // Non-Local Means auf der Helligkeit, gemessen ~0,7 s je MP
    internal const double DehazePassCost = 0.24;
    internal const double ToneMapCost = 0.15;
    internal const double ToolsCost = 0.03;
    internal const double SharpenCost = 0.03;
    public const double GrainCost = 0.06;
    internal const double ColorCost = 0.02;

    private readonly List<(string Text, double Weight)> _steps = [];
    private double _total;
    private double _before;   // Aufwand der abgeschlossenen Schritte
    private int _index = -1;

    /// <summary>Einen Schritt vormerken; die Reihenfolge muss der Ausführung entsprechen.</summary>
    public void Add(string text, double weight)
    {
        _steps.Add((text, Math.Max(weight, 1e-6)));
        _total += Math.Max(weight, 1e-6);
    }

    /// <summary>Den nächsten vorgemerkten Schritt beginnen — vorher abbrechen, falls verlangt.</summary>
    public void Next()
    {
        cancel.ThrowIfCancellationRequested();
        if (_index >= 0 && _index < _steps.Count) _before += _steps[_index].Weight;
        _index++;
        if (_index < _steps.Count) sink?.Report(new WorkProgress(_before / _total, _steps[_index].Text));
    }

    /// <summary>Alles fertig.</summary>
    public void Done(string text) => sink?.Report(new WorkProgress(1, text));
}
