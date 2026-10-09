using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageConverter.Core.Imaging;
using ImageMagick;

namespace ImageConverter.ViewModels;

/// <summary>Bearbeitungen, die sich ausblenden lassen (Auge in der Liste „Bearbeitungen“).</summary>
[Flags]
public enum EditSteps
{
    None = 0,
    Ai = 1,
    Resize = 2,
    Denoise = 4,
    Dehaze = 8,
    Light = 16,
    Color = 32,
    MatchColor = 64,
    Sharpen = 128,
}

/// <summary>Art eines Bildschritts im Verlauf (verändert das Bild selbst, nicht nur Einstellungen).</summary>
public enum ImageStepKind { Crop, Rotate, Flip, Ai, Applied }

/// <summary>Eine Zeile der Liste „Bearbeitungen“.</summary>
public partial class EditRow(MainViewModel owner, EditSteps step, string name, string detail, bool canHide, string? note)
    : ObservableObject
{
    public string Name { get; } = name;
    public string Detail { get; } = detail;

    /// <summary>Lässt sich ausblenden (Zuschnitt und Drehen nicht — die KI wurde darauf gerechnet).</summary>
    public bool CanHide { get; } = canHide;

    public string? Note { get; } = note;

    /// <summary>Auge offen: fließt in „Nachher“ und in den Export ein.</summary>
    public bool Shown
    {
        get => step == EditSteps.None || (owner.HiddenSteps & step) == 0;
        set
        {
            if (step == EditSteps.None || value == Shown) return;
            owner.HiddenSteps = value ? owner.HiddenSteps & ~step : owner.HiddenSteps | step;
        }
    }
}

// Liste „Bearbeitungen“: je Bearbeitung eine Zeile in der Reihenfolge, in der gerechnet wird; das Auge blendet sie
// aus „Nachher“ und aus dem Export aus, „Neu anwenden“ schreibt die Auswahl fest.
public partial class MainViewModel
{
    /// <summary>Ein Bildschritt im Verlauf: was er war und wie er sich auf ein anderes Bild wiederholen lässt.</summary>
    /// <param name="Replay">Derselbe Schritt auf einem anderen Bild (Zuschnitt, Drehen); null = nicht wiederholbar (KI).</param>
    internal sealed record ImageStep(ImageStepKind Kind, string Label, string? Detail, Func<MagickImage, MagickImage>? Replay);

    /// <summary>Ausgeblendete Bearbeitungen — gehören zum Einstellungsstand (je Bild, Rückgängig, Export).</summary>
    [ObservableProperty] public partial EditSteps HiddenSteps { get; set; }

    /// <summary>Liste „Bearbeitungen“ über der Vorschau zeigen.</summary>
    [ObservableProperty] public partial bool ShowEditList { get; set; }

    public ObservableCollection<EditRow> EditRows { get; } = [];

    public bool HasEditRows => EditRows.Count > 0;

    private bool Shows(EditSteps step) => (HiddenSteps & step) == 0;

    partial void OnHiddenStepsChanged(EditSteps oldValue, EditSteps newValue)
    {
        ApplySelectionCommand.NotifyCanExecuteChanged();
        RefreshEditRows();
        if (((oldValue ^ newValue) & EditSteps.Ai) != 0)
            _ = ShowDocumentAsync();   // KI ein/aus: anderes Bild zeigen
        else
            RequestPreview();
        NotifySizeDerived();
    }

    /// <summary>Einstellungen ohne die ausgeblendeten Bearbeitungen.</summary>
    internal static Adjustments Mask(Adjustments a, EditSteps hidden)
    {
        if ((hidden & EditSteps.Dehaze) != 0) a = a with { Dehaze = 0 };
        if ((hidden & EditSteps.Light) != 0) a = a with { Brightness = 0, Contrast = 0, Shadows = 0, Highlights = 0 };
        if ((hidden & EditSteps.Color) != 0) a = a with { Saturation = 0, Vibrance = 0, Tint = Tint.None };
        if ((hidden & EditSteps.MatchColor) != 0) a = a with { MatchLuminance = 0, Neutralize = 0, Warmth = 0 };
        return a;
    }

    private double EffectiveNoise => Shows(EditSteps.Denoise) && NoiseReduction > 0.01 ? NoiseReduction : 0;
    private double EffectiveSharpen => Shows(EditSteps.Sharpen) && SharpenAmount > 0.01 ? SharpenAmount : 0;

    // ------------------------------------------------------------ Bildschritte

    /// <summary>Die Bildschritte einer Datei, die zum aktuellen Stand geführt haben (älteste zuerst).</summary>
    private List<Snapshot> ImageStepsOf(string path) =>
        _undo.Where(s => s.FilePath == path && s.Image is not null && s.Step is not null).Reverse().ToList();


    /// <summary>
    /// Index des ersten KI-Schritts, der noch zählt: nach dem letzten „Auswahl angewendet“ (das eine KI schon aus dem
    /// Bild genommen hat); −1 = keiner.
    /// </summary>
    private static int FirstLiveAi(List<Snapshot> steps)
    {
        var applied = steps.FindLastIndex(s => s.Step!.Kind == ImageStepKind.Applied);
        return steps.FindIndex(applied + 1, s => s.Step!.Kind == ImageStepKind.Ai);
    }
    /// <summary>
    /// Lässt sich die KI ausblenden? Nur wenn sie die Bildgröße behalten hat und alles danach wiederholbar ist
    /// (Zuschnitt, Drehen) — dann entsteht das Bild ohne KI aus dem Stand davor plus den späteren Schritten.
    /// </summary>
    private bool CanHideAi(string path, EditHistory h, out string? reason)
    {
        reason = null;
        var steps = ImageStepsOf(path);
        var first = FirstLiveAi(steps);
        if (first < 0) return false;

        for (var i = first; i < steps.Count; i++)
        {
            var kind = steps[i].Step!.Kind;
            var after = i + 1 < steps.Count ? steps[i + 1].Image! : h.Current;
            if (kind == ImageStepKind.Ai && !SameSize(steps[i].Image!, after))
            {
                reason = Tr.T("Vergrößert — lässt sich nicht ausblenden, nur mit Rückgängig zurücknehmen.");
                return false;
            }
            if (kind != ImageStepKind.Ai && steps[i].Step!.Replay is null)
            {
                reason = Tr.T("Danach wurde das Bild festgeschrieben — nur mit Rückgängig zurückzunehmen.");
                return false;
            }
        }
        return true;
    }

    private static bool SameSize(MagickImage a, MagickImage b)
    {
        lock (a) lock (b) return a.Width == b.Width && a.Height == b.Height;
    }

    // Bild ohne KI, je Datei und deren aktuellem Bild einmal gerechnet
    private readonly Dictionary<string, (MagickImage Current, MagickImage WithoutAi)> _withoutAi = new(StringComparer.Ordinal);

    /// <summary>
    /// Das Bild, das angezeigt und exportiert wird: das aktuelle — oder bei ausgeblendeter KI das Bild vor dem
    /// ersten KI-Schritt mit den späteren Schritten (Zuschnitt, Drehen) noch einmal angewendet.
    /// </summary>
    private MagickImage DocumentFor(string path, EditHistory h, EditSteps hidden)
    {
        if ((hidden & EditSteps.Ai) == 0 || !CanHideAi(path, h, out _)) return h.Current;
        if (_withoutAi.TryGetValue(path, out var cached) && ReferenceEquals(cached.Current, h.Current)) return cached.WithoutAi;

        var steps = ImageStepsOf(path);
        var first = FirstLiveAi(steps);
        var doc = steps[first].Image!;
        var owned = false;
        foreach (var step in steps.Skip(first + 1).Select(s => s.Step!).Where(s => s.Kind != ImageStepKind.Ai))
        {
            var next = step.Replay!(doc);
            if (owned) doc.Dispose();
            (doc, owned) = (next, true);
        }

        // Der vorige Stand dieser Datei wird nicht mehr gebraucht (Bild hat sich geändert — die Vorschau ist gestoppt)
        if (_withoutAi.TryGetValue(path, out var old) && !ReferenceEquals(old.WithoutAi, doc) && !IsHeldByHistory(old.WithoutAi))
            old.WithoutAi.Dispose();
        _withoutAi[path] = (h.Current, doc);
        return doc;
    }

    /// <summary>Das gerade angezeigte Bild (mit oder ohne KI, je nach Liste „Bearbeitungen“).</summary>
    private MagickImage? ShownDocument =>
        _history is { } h && CurrentPath is { } path ? DocumentFor(path, h, HiddenSteps) : null;


    /// <summary>Was bei einer Datei ausgeblendet ist — ihr eigener Stand, sonst der gemeinsame bzw. die Vorlage.</summary>
    internal EditSteps HiddenFor(string path)
    {
        if (path == CurrentPath || ApplyToAll) return HiddenSteps;
        if (_fileSettings.TryGetValue(path, out var own)) return own.Hidden;
        return _individualTemplate?.Hidden ?? EditSteps.None;
    }

    /// <summary>Alle Bilder ohne KI freigeben (Bilder geschlossen).</summary>
    private void ClearWithoutAi()
    {
        foreach (var (_, entry) in _withoutAi)
            if (!IsHeldByHistory(entry.WithoutAi)) entry.WithoutAi.Dispose();
        _withoutAi.Clear();
    }
    private bool IsHeldByHistory(MagickImage image) =>
        _histories.Values.Any(h => ReferenceEquals(h.Current, image) || ReferenceEquals(h.Original, image))
        || _undo.Any(s => ReferenceEquals(s.Image, image)) || _redo.Any(s => ReferenceEquals(s.Image, image));

    /// <summary>Angezeigtes Bild neu setzen (KI ein- oder ausgeblendet).</summary>
    private async Task ShowDocumentAsync()
    {
        if (ShownDocument is not { } doc) return;
        await StopPreviewAsync();
        await SetDocumentAsync(doc);
        RequestPreview();
    }

    // ------------------------------------------------------------ Liste


    private bool _editRowsRequested;

    /// <summary>Liste einmal je Bedienvorgang neu aufbauen (nicht bei jedem Zwischenwert eines Reglers einzeln).</summary>
    private void RequestEditRows()
    {
        if (_editRowsRequested) return;
        _editRowsRequested = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _editRowsRequested = false;
            RefreshEditRows();
        }, Avalonia.Threading.DispatcherPriority.Background);
    }
    /// <summary>Zeilen neu aufbauen — nach jeder Einstellung, jedem Bildschritt, jedem Bildwechsel.</summary>
    private void RefreshEditRows()
    {
        var rows = new List<EditRow>();
        if (_history is { } h && CurrentPath is { } path)
        {
            // Bildschritte in ihrer Reihenfolge; die KI als eine Zeile (spätere KI-Läufe ersetzen frühere)
            // — KI-Schritte vor einem „Auswahl angewendet“ sind schon aus dem Bild genommen
            var aiDone = false;
            var canHideAi = CanHideAi(path, h, out var aiReason);
            var steps = ImageStepsOf(path);
            var firstLive = FirstLiveAi(steps);
            for (var i = 0; i < steps.Count; i++)
            {
                var step = steps[i].Step!;
                if (step.Kind == ImageStepKind.Ai)
                {
                    if (aiDone || i < firstLive || firstLive < 0) continue;
                    aiDone = true;
                    var last = steps.Last(s => s.Step!.Kind == ImageStepKind.Ai).Step!;
                    rows.Add(new EditRow(this, EditSteps.Ai, last.Label, last.Detail ?? "", canHideAi, aiReason));
                }
                else
                {
                    rows.Add(new EditRow(this, EditSteps.None, step.Label, step.Detail ?? "", false,
                        Tr.T("Lässt sich nicht ausblenden — nur mit Rückgängig zurücknehmen.")));
                }
            }
        }

        if (_history is not null)
        {
            if (TargetWidth != SourceWidth || TargetHeight != SourceHeight)
                rows.Add(Row(EditSteps.Resize, Tr.T("Größe"), $"{TargetWidth} × {TargetHeight} px"));
            if (NoiseReduction > 0.01)
                rows.Add(Row(EditSteps.Denoise, Tr.T("Rauschen"), $"{NoiseReduction:0.00}"));
            if (Math.Round(Dehaze) != 0)
                rows.Add(Row(EditSteps.Dehaze, Tr.T("Dunst"), $"{Math.Round(Dehaze):+0;−0} · {Math.Round(DehazePasses)}×"));
            AddIfAny(rows, EditSteps.Light, Tr.T("Licht"),
                (Tr.T("Helligkeit"), Brightness), (Tr.T("Kontrast"), Contrast), (Tr.T("Tiefen"), Shadows), (Tr.T("Lichter"), Highlights));
            var tint = TintOf(SelectedTint) switch
            {
                Tint.BlackWhite => Tr.T("Schwarzweiß"),
                Tint.Color => Tr.T("Färben {0:0}°", TintHue),
                _ => null,
            };
            AddIfAny(rows, EditSteps.Color, Tr.T("Farbe"), tint, (Tr.T("Dynamik"), Vibrance), (Tr.T("Sättigung"), Saturation));
            AddIfAny(rows, EditSteps.MatchColor, Tr.T("Gleiche Farbe"),
                (Tr.T("Luminanz"), MatchLuminance), (Tr.T("Ausgleichen"), Neutralize), (Tr.T("Farbe"), Warmth));
            if (SharpenAmount > 0.01)
                rows.Add(Row(EditSteps.Sharpen, Tr.T("Schärfe"), Tr.T("{0:0.00} · Radius {1:0.0}", SharpenAmount, SharpenRadius)));
        }

        EditRows.Clear();
        foreach (var row in rows) EditRows.Add(row);
        OnPropertyChanged(nameof(HasEditRows));
        ApplySelectionCommand.NotifyCanExecuteChanged();
    }

    private EditRow Row(EditSteps step, string name, string detail) => new(this, step, name, detail, true, null);

    private void AddIfAny(List<EditRow> rows, EditSteps step, string name, params (string Label, double Value)[] values) =>
        AddIfAny(rows, step, name, null, values);

    private void AddIfAny(List<EditRow> rows, EditSteps step, string name, string? extra, params (string Label, double Value)[] values)
    {
        var parts = values.Where(v => Math.Round(v.Value) != 0)
            .Select(v => $"{v.Label} {Math.Round(v.Value):+0;−0}").ToList();
        if (extra is not null) parts.Insert(0, extra);
        if (parts.Count > 0) rows.Add(Row(step, name, string.Join(" · ", parts)));
    }

    // ------------------------------------------------------------ Neu anwenden

    private bool CanApplySelection => HiddenSteps != EditSteps.None && EditRows.Any(r => !r.Shown) && !IsEditing;

    /// <summary>
    /// Nur die angehakten Bearbeitungen festschreiben: ausgeblendete Einstellungen auf null, ausgeblendete KI aus dem
    /// Bild — ein Schritt, rückgängig mit Strg+Z.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanApplySelection))]
    private async Task ApplySelection()
    {
        CommitPendingSettings();
        var hidden = HiddenSteps;
        SettingsState Cleared(SettingsState s) => WithoutHidden(s, hidden);

        if ((hidden & EditSteps.Ai) != 0 && ShownDocument is { } withoutAi && _history is { } h && !ReferenceEquals(withoutAi, h.Current))
        {
            var copy = ImageOps.CloneOf(withoutAi);
            await ApplyEditAsync(_ => copy, Cleared,
                new ImageStep(ImageStepKind.Applied, Tr.T("Auswahl angewendet"), Tr.T("ohne KI"), null));
            return;
        }

        var before = CaptureSettings();
        ApplySettings(Cleared(before));
        _undo.Push(new Snapshot(ApplyToAll ? null : CurrentPath, null, before));
        ClearRedo();
        if (!ApplyToAll && CurrentPath is { } path) _fileSettings[path] = CaptureSettings();
        NotifyUndoRedo();
    }

    /// <summary>Stand ohne die ausgeblendeten Bearbeitungen — deren Regler auf null, nichts mehr ausgeblendet.</summary>
    internal static SettingsState WithoutHidden(SettingsState s, EditSteps hidden)
    {
        if ((hidden & EditSteps.Resize) != 0) s = s with { TargetWidth = s.SourceWidth, TargetHeight = s.SourceHeight,
            PrintWidthIn = s.SourceWidth / (double)Math.Max(1, s.Dpi), PrintHeightIn = s.SourceHeight / (double)Math.Max(1, s.Dpi) };
        if ((hidden & EditSteps.Denoise) != 0) s = s with { NoiseReduction = 0 };
        if ((hidden & EditSteps.Dehaze) != 0) s = s with { Dehaze = 0 };
        if ((hidden & EditSteps.Light) != 0) s = s with { Brightness = 0, Contrast = 0, Shadows = 0, Highlights = 0 };
        if ((hidden & EditSteps.Color) != 0) s = s with { Saturation = 0, Vibrance = 0, SelectedTint = TintLabels[0].Label };
        if ((hidden & EditSteps.MatchColor) != 0) s = s with { MatchLuminance = 0, Neutralize = 0, Warmth = 0 };
        if ((hidden & EditSteps.Sharpen) != 0) s = s with { SharpenAmount = 0 };
        return s with { Hidden = EditSteps.None };
    }
}
