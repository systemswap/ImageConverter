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

/// <summary>Funktionen, die sich ausschalten lassen (Auge in der Liste „Bearbeitungen“, wie in Referenzwerkzeug).</summary>
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
    SkinDetails = 256,
    ColorNoise = 512,
    WhiteBalance = 1024,
    Retouch = 2048,
}

/// <summary>Art eines Bildschritts im Verlauf (verändert das Bild selbst, nicht nur Einstellungen).</summary>
public enum ImageStepKind { Crop, Rotate, Flip, Ai, Applied, Retouch }

/// <summary>Eine Zeile der Liste „Bearbeitungen“: eine Funktion, die am Bild wirkt.</summary>
public partial class EditRow(MainViewModel owner, EditSteps step, string name, string detail, bool canHide, string? note)
    : ObservableObject
{
    public string Name { get; } = name;
    public string Detail { get; } = detail;

    /// <summary>Lässt sich ausschalten (z. B. nicht, wenn die KI vergrößert hat).</summary>
    public bool CanHide { get; } = canHide;

    public string? Note { get; } = note;

    /// <summary>Auge offen: wirkt in „Nachher“ und im Export.</summary>
    public bool Shown
    {
        get => (owner.HiddenSteps & step) == 0;
        set
        {
            if (value == Shown) return;
            owner.HiddenSteps = value ? owner.HiddenSteps & ~step : owner.HiddenSteps | step;
        }
    }
}

// Liste „Bearbeitungen“: je Funktion eine Zeile (KI, Haut & Details, Größe, Rauschen, Dunst, Licht, Farbe,
// Gleiche Farbe, Schärfe) — nicht jede Reglerbewegung, nicht Drehen und Zuschnitt. Das Auge schaltet eine Funktion
// aus „Nachher“ und dem Export aus, „Neu anwenden“ schreibt die Auswahl fest.
public partial class MainViewModel
{
    /// <summary>Ein Bildschritt im Verlauf: was er war und wie er sich auf ein anderes Bild wiederholen lässt.</summary>
    /// <param name="Replay">Derselbe Schritt auf einem anderen Bild (Zuschnitt, Drehen); null = nicht wiederholbar (KI).</param>
    /// <param name="SkinDetail">KI mit Haut &amp; Details: Kurzinfo dazu; null = keine.</param>
    internal sealed record ImageStep(ImageStepKind Kind, string Label, string? Detail,
        Func<MagickImage, MagickImage>? Replay, string? SkinDetail = null)
    {
        /// <summary>Dieselbe KI ohne Haut &amp; Details (nur wenn es welche gab) — zum Ausschalten.</summary>
        public MagickImage? Plain { get; set; }

        /// <summary>Entfernen: neuer Versuch vom Bild vor dem letzten Entfernen — ersetzt es in der Liste.</summary>
        public bool Replaces { get; init; }
    }

    /// <summary>Ausgeschaltete Funktionen — gehören zum Einstellungsstand (je Bild, Rückgängig, Export).</summary>
    [ObservableProperty] public partial EditSteps HiddenSteps { get; set; }

    /// <summary>Liste „Bearbeitungen“ über der Vorschau zeigen.</summary>
    [ObservableProperty] public partial bool ShowEditList { get; set; }

    public ObservableCollection<EditRow> EditRows { get; } = [];

    public bool HasEditRows => EditRows.Count > 0;

    private bool Shows(EditSteps step) => (HiddenSteps & step) == 0;

    private const EditSteps ImageVariants = EditSteps.Ai | EditSteps.SkinDetails;

    partial void OnHiddenStepsChanged(EditSteps oldValue, EditSteps newValue)
    {
        ApplySelectionCommand.NotifyCanExecuteChanged();
        RefreshEditRows();
        if (((oldValue ^ newValue) & ImageVariants) != 0)
            _ = ShowDocumentAsync();   // KI bzw. Haut & Details ein/aus: anderes Bild zeigen
        else
            RequestPreview();
        NotifySizeDerived();
    }

    /// <summary>Einstellungen ohne die ausgeschalteten Funktionen.</summary>
    internal static Adjustments Mask(Adjustments a, EditSteps hidden)
    {
        if ((hidden & EditSteps.Dehaze) != 0) a = a with { Dehaze = 0 };
        if ((hidden & EditSteps.Light) != 0) a = a with { Brightness = 0, Contrast = 0, Shadows = 0, Highlights = 0 };
        if ((hidden & EditSteps.Color) != 0) a = a with { Saturation = 0, Vibrance = 0, Tint = Tint.None };
        if ((hidden & EditSteps.MatchColor) != 0) a = a with { MatchLuminance = 0, MatchLimit = 100, Neutralize = 0, Warmth = 0 };
        if ((hidden & EditSteps.WhiteBalance) != 0) a = a with { WbNeutral = null, WbBlack = null, WbWhite = null };
        return a;
    }

    private double EffectiveNoise => Shows(EditSteps.Denoise) && NoiseReduction > 0.005 ? NoiseReduction : 0;
    private double EffectiveColorNoise => Shows(EditSteps.ColorNoise) && ColorNoiseReduction > 0.005 ? ColorNoiseReduction : 0;
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

    /// <summary>Das Bild nach Schritt <paramref name="i"/> (vor dem nächsten bzw. das aktuelle).</summary>
    private static MagickImage After(List<Snapshot> steps, int i, EditHistory h) =>
        i + 1 < steps.Count ? steps[i + 1].Image! : h.Current;

    /// <summary>Alles ab Schritt <paramref name="from"/> wiederholbar (Zuschnitt, Drehen) — KI-Schritte ausgenommen?</summary>
    private static bool Replayable(List<Snapshot> steps, int from) =>
        steps.Skip(from).All(s => s.Step!.Kind == ImageStepKind.Ai || s.Step.Replay is not null);

    /// <summary>
    /// Lässt sich die KI ausschalten? Nur wenn sie die Bildgröße behalten hat und alles danach wiederholbar ist
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
            if (steps[i].Step!.Kind == ImageStepKind.Ai && !SameSize(steps[i].Image!, After(steps, i, h)))
            {
                reason = Tr.T("Vergrößert — lässt sich nicht ausschalten, nur mit Rückgängig zurücknehmen.");
                return false;
            }
        }
        if (!Replayable(steps, first))
        {
            reason = Tr.T("Danach wurde das Bild festgeschrieben — nur mit Rückgängig zurückzunehmen.");
            return false;
        }
        return true;
    }

    /// <summary>Der letzte KI-Schritt hat Haut &amp; Details, die sich ausschalten lassen?</summary>
    private bool CanHideSkin(string path, EditHistory h)
    {
        var steps = ImageStepsOf(path);
        if (FirstLiveAi(steps) < 0) return false;
        var last = steps.FindLastIndex(s => s.Step!.Kind == ImageStepKind.Ai);
        return steps[last].Step!.Plain is { } plain && SameSize(plain, After(steps, last, h)) && Replayable(steps, last + 1);
    }

    private static bool SameSize(MagickImage a, MagickImage b)
    {
        lock (a) lock (b) return a.Width == b.Width && a.Height == b.Height;
    }

    // Bild ohne KI bzw. ohne Haut & Details, je Datei, Fassung und aktuellem Bild einmal gerechnet
    private readonly Dictionary<(string Path, EditSteps Variant), (MagickImage Current, MagickImage Image)> _variants = [];

    /// <summary>
    /// Das Bild, das angezeigt und exportiert wird: das aktuelle — oder bei ausgeschalteter KI das Bild vor dem ersten
    /// KI-Schritt, bei ausgeschalteten Haut &amp; Details die letzte KI ohne sie; die späteren Schritte (Zuschnitt,
    /// Drehen) jeweils noch einmal angewendet.
    /// </summary>
    private MagickImage DocumentFor(string path, EditHistory h, EditSteps hidden)
    {
        var variant = (hidden & EditSteps.Ai) != 0 && CanHideAi(path, h, out _) ? EditSteps.Ai
            : (hidden & EditSteps.SkinDetails) != 0 && CanHideSkin(path, h) ? EditSteps.SkinDetails
            : EditSteps.None;
        if (variant == EditSteps.None) return h.Current;
        if (_variants.TryGetValue((path, variant), out var cached) && ReferenceEquals(cached.Current, h.Current))
            return cached.Image;

        var steps = ImageStepsOf(path);
        int from;
        MagickImage doc;
        if (variant == EditSteps.Ai)
        {
            var first = FirstLiveAi(steps);
            (doc, from) = (steps[first].Image!, first + 1);
        }
        else
        {
            var last = steps.FindLastIndex(s => s.Step!.Kind == ImageStepKind.Ai);
            (doc, from) = (steps[last].Step!.Plain!, last + 1);
        }

        var owned = false;
        foreach (var step in steps.Skip(from).Select(s => s.Step!).Where(s => s.Kind != ImageStepKind.Ai))
        {
            var next = step.Replay!(doc);
            if (owned) doc.Dispose();
            (doc, owned) = (next, true);
        }

        // Der vorige Stand wird nicht mehr gebraucht (Bild hat sich geändert — die Vorschau ist gestoppt)
        if (_variants.TryGetValue((path, variant), out var old) && !ReferenceEquals(old.Image, doc) && !IsHeldByHistory(old.Image))
            old.Image.Dispose();
        _variants[(path, variant)] = (h.Current, doc);
        return doc;
    }

    /// <summary>Das gerade angezeigte Bild (mit oder ohne KI bzw. Haut &amp; Details, je nach Liste).</summary>
    private MagickImage? ShownDocument =>
        _history is { } h && CurrentPath is { } path ? DocumentFor(path, h, HiddenSteps) : null;

    /// <summary>
    /// Das aktuelle Bild ohne KI (Zuschnitt, Drehen danach noch einmal angewendet) — Ausgangsbild für einen neuen
    /// KI-Lauf, damit sich KI nicht auf KI häuft. null: keine KI im Bild oder nicht abtrennbar.
    /// </summary>
    internal MagickImage? CurrentWithoutAi =>
        _history is { } h && CurrentPath is { } path && CanHideAi(path, h, out _) ? DocumentFor(path, h, EditSteps.Ai) : null;

    /// <summary>Was bei einer Datei ausgeschaltet ist — ihr eigener Stand, sonst der gemeinsame bzw. die Vorlage.</summary>
    internal EditSteps HiddenFor(string path)
    {
        if (path == CurrentPath || ApplyToAll) return HiddenSteps;
        if (_fileSettings.TryGetValue(path, out var own)) return own.Hidden;
        return _individualTemplate?.Hidden ?? EditSteps.None;
    }

    /// <summary>Alle Fassungen freigeben (Bilder geschlossen).</summary>
    private void ClearWithoutAi()
    {
        foreach (var (_, entry) in _variants)
            if (!IsHeldByHistory(entry.Image)) entry.Image.Dispose();
        _variants.Clear();
    }

    private bool IsHeldByHistory(MagickImage image) =>
        _histories.Values.Any(h => ReferenceEquals(h.Current, image) || ReferenceEquals(h.Original, image))
        || _undo.Concat(_redo).Any(s => ReferenceEquals(s.Image, image) || ReferenceEquals(s.Step?.Plain, image));

    /// <summary>Angezeigtes Bild neu setzen (KI bzw. Haut &amp; Details ein- oder ausgeschaltet).</summary>
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
            // KI als eine Zeile (spätere Läufe ersetzen frühere), Haut & Details der letzten KI als eigene
            var steps = ImageStepsOf(path);
            if (FirstLiveAi(steps) >= 0)
            {
                var last = steps.Last(s => s.Step!.Kind == ImageStepKind.Ai).Step!;
                var canHideAi = CanHideAi(path, h, out var aiReason);
                rows.Add(new EditRow(this, EditSteps.Ai, last.Label, last.Detail ?? "", canHideAi, aiReason));
                if (last.SkinDetail is { } skin)
                {
                    var canHideSkin = CanHideSkin(path, h);
                    rows.Add(new EditRow(this, EditSteps.SkinDetails, Tr.T("Haut & Details"), skin, canHideSkin,
                        canHideSkin ? null : Tr.T("Lässt sich nicht ausschalten — nur mit Rückgängig zurücknehmen.")));
                }
            }

            // Entfernen: eine Zeile für alle Stellen seit dem letzten Festschreiben
            var applied = steps.FindLastIndex(s => s.Step!.Kind == ImageStepKind.Applied);
            var retouch = new List<ImageStep>();
            foreach (var s in steps.Skip(applied + 1).Select(s => s.Step!).Where(s => s.Kind == ImageStepKind.Retouch))
            {
                if (s.Replaces && retouch.Count > 0) retouch.RemoveAt(retouch.Count - 1);
                retouch.Add(s);
            }
            if (retouch.Count > 0)
                rows.Add(new EditRow(this, EditSteps.Retouch, Tr.T("Entfernt"),
                    retouch.Count == 1 ? retouch[0].Detail ?? "" : Tr.T("{0}× · zuletzt {1}", retouch.Count, retouch[^1].Detail ?? ""),
                    false, Tr.T("Lässt sich nicht ausschalten — nur mit Rückgängig zurücknehmen.")));
            if (TargetWidth != SourceWidth || TargetHeight != SourceHeight)
                rows.Add(Row(EditSteps.Resize, Tr.T("Größe"), $"{TargetWidth} × {TargetHeight} px"));
            if (NoiseReduction > 0.005)
                rows.Add(Row(EditSteps.Denoise, Tr.T("Rauschen"), $"{NoiseReduction * 100:0} %"));
            if (ColorNoiseReduction > 0.005)
                rows.Add(Row(EditSteps.ColorNoise, Tr.T("Farbrauschen"), $"{ColorNoiseReduction * 100:0} %"));
            // Weißabgleich rechnet als Erstes der Kette (vor Dunst, Licht und Farbe)
            if (HasWhiteBalance)
                rows.Add(Row(EditSteps.WhiteBalance, Tr.T("Weißabgleich"), string.Join(" · ",
                    new[] { WbNeutral is null ? null : WbNeutralText, WbBlack is null ? null : WbBlackText, WbWhite is null ? null : WbWhiteText }
                        .Where(t => t is not null))));
            if (Math.Round(Dehaze) != 0)
                rows.Add(Row(EditSteps.Dehaze, Tr.T("Dunst"), $"{Math.Round(Dehaze):+0;−0} · {Math.Round(DehazePasses)}×"));
            // Reihenfolge der Kette: Gleiche Farbe (Weißabgleich) vor Licht und Farbe
            AddIfAny(rows, EditSteps.MatchColor, Tr.T("Gleiche Farbe"),
                CanLimitLuminance && MatchLimit < 99.5 ? $"{Tr.T("Schwellwert")} {Math.Round(MatchLimit):0} %" : null,
                (Tr.T("Luminanz"), MatchLuminance), (Tr.T("Ausgleichen"), Neutralize), (Tr.T("Farbe"), Warmth));
            AddIfAny(rows, EditSteps.Light, Tr.T("Licht"),
                (Tr.T("Helligkeit"), Brightness), (Tr.T("Kontrast"), Contrast), (Tr.T("Tiefen"), Shadows), (Tr.T("Lichter"), Highlights));
            var tint = TintOf(SelectedTint) switch
            {
                Tint.BlackWhite => Tr.T("Schwarzweiß"),
                Tint.Color => Tr.T("Färben {0:0}°", TintHue),
                _ => null,
            };
            AddIfAny(rows, EditSteps.Color, Tr.T("Farbe"), tint, (Tr.T("Dynamik"), Vibrance), (Tr.T("Sättigung"), Saturation));
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
    /// Nur die eingeschalteten Funktionen festschreiben: ausgeschaltete Regler auf null, eine ausgeschaltete KI bzw.
    /// Haut &amp; Details aus dem Bild — ein Schritt, rückgängig mit Strg+Z.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanApplySelection))]
    private async Task ApplySelection()
    {
        CommitPendingSettings();
        var hidden = HiddenSteps;
        SettingsState Cleared(SettingsState s) => WithoutHidden(s, hidden);

        if (_history is { } h && CurrentPath is { } path && ShownDocument is { } shown && !ReferenceEquals(shown, h.Current))
        {
            var copy = ImageOps.CloneOf(shown);
            var aiOut = (hidden & EditSteps.Ai) != 0;
            var steps = ImageStepsOf(path);
            var last = steps.Last(s => s.Step!.Kind == ImageStepKind.Ai).Step!;
            // KI ganz raus: ab hier zählt keine KI mehr. Nur Haut & Details raus: die KI bleibt, nur ohne sie.
            var step = aiOut
                ? new ImageStep(ImageStepKind.Applied, Tr.T("Auswahl angewendet"), Tr.T("ohne KI"), null)
                : new ImageStep(ImageStepKind.Ai, last.Label, last.Detail, null);
            await ApplyEditAsync(_ => copy, Cleared, step);
            return;
        }

        var before = CaptureSettings();
        ApplySettings(Cleared(before));
        _undo.Push(new Snapshot(ApplyToAll ? null : CurrentPath, null, before));
        ClearRedo();
        if (!ApplyToAll && CurrentPath is { } current) _fileSettings[current] = CaptureSettings();
        NotifyUndoRedo();
    }

    /// <summary>Stand ohne die ausgeschalteten Funktionen — deren Regler auf null, nichts mehr ausgeschaltet.</summary>
    internal static SettingsState WithoutHidden(SettingsState s, EditSteps hidden)
    {
        if ((hidden & EditSteps.Resize) != 0) s = s with { TargetWidth = s.SourceWidth, TargetHeight = s.SourceHeight,
            PrintWidthIn = s.SourceWidth / (double)Math.Max(1, s.Dpi), PrintHeightIn = s.SourceHeight / (double)Math.Max(1, s.Dpi) };
        if ((hidden & EditSteps.Denoise) != 0) s = s with { NoiseReduction = 0 };
        if ((hidden & EditSteps.ColorNoise) != 0) s = s with { ColorNoiseReduction = 0 };
        if ((hidden & EditSteps.Dehaze) != 0) s = s with { Dehaze = 0 };
        if ((hidden & EditSteps.Light) != 0) s = s with { Brightness = 0, Contrast = 0, Shadows = 0, Highlights = 0 };
        if ((hidden & EditSteps.Color) != 0) s = s with { Saturation = 0, Vibrance = 0, SelectedTint = TintLabels[0].Label };
        if ((hidden & EditSteps.MatchColor) != 0) s = s with { MatchLuminance = 0, MatchLimit = 100, Neutralize = 0, Warmth = 0 };
        if ((hidden & EditSteps.WhiteBalance) != 0) s = s with { WbNeutral = null, WbBlack = null, WbWhite = null };
        if ((hidden & EditSteps.Sharpen) != 0) s = s with { SharpenAmount = 0 };
        return s with { Hidden = EditSteps.None };
    }
}
