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
    // Ausrichten (Rezept, GeometryRecipe): je Stufe eine Zeile
    Straighten = 4096,
    Level = 8192,
    CropStep = 16384,
    Orient = 32768,
    LumMask = 65536,
    Grain = 131072,
    Dewarp = 262144,
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

    /// <summary>Die Stufen des Ausrichtens in der Liste.</summary>
    internal const EditSteps GeometrySteps = EditSteps.Dewarp | EditSteps.Straighten | EditSteps.Level | EditSteps.CropStep | EditSteps.Orient;

    partial void OnHiddenStepsChanged(EditSteps oldValue, EditSteps newValue)
    {
        ApplySelectionCommand.NotifyCanExecuteChanged();
        RefreshEditRows();
        var changed = oldValue ^ newValue;
        if ((changed & GeometrySteps) != 0)
            _ = ShowDocumentAsync(sizeChanges: true);   // Ausrichten ein/aus: anderes Format
        else if ((changed & ImageVariants) != 0)
            _ = ShowDocumentAsync();   // KI bzw. Haut & Details ein/aus: anderes Bild zeigen
        else
            RequestPreview();
        NotifySizeDerived();
    }

    /// <summary>Welche Stufen des Ausrichtens eingeschaltet sind.</summary>
    internal static GeometryStages StagesShown(EditSteps hidden) =>
        GeometryStages.All
        & ~((hidden & EditSteps.Dewarp) != 0 ? GeometryStages.Dewarp : 0)
        & ~((hidden & EditSteps.Straighten) != 0 ? GeometryStages.Straighten : 0)
        & ~((hidden & EditSteps.Level) != 0 ? GeometryStages.Level : 0)
        & ~((hidden & EditSteps.CropStep) != 0 ? GeometryStages.Crop : 0)
        & ~((hidden & EditSteps.Orient) != 0 ? GeometryStages.Orient : 0);

    /// <summary>Einstellungen ohne die ausgeschalteten Funktionen.</summary>
    internal static Adjustments Mask(Adjustments a, EditSteps hidden)
    {
        if ((hidden & EditSteps.Dehaze) != 0) a = a with { Dehaze = 0 };
        if ((hidden & EditSteps.Light) != 0) a = a with { Brightness = 0, Contrast = 0, Shadows = 0, Highlights = 0 };
        if ((hidden & EditSteps.Color) != 0) a = a with { Saturation = 0, Vibrance = 0, Tint = Tint.None };
        if ((hidden & EditSteps.MatchColor) != 0) a = a with { MatchLuminance = 0, MatchLimit = 100, MatchTarget = 0, MatchGlobal = 0, Neutralize = 0, Warmth = 0 };
        if ((hidden & EditSteps.WhiteBalance) != 0) a = a with { WbNeutral = null, WbBlack = null, WbWhite = null };
        if ((hidden & EditSteps.LumMask) != 0) a = a with { MaskAmount = 0, MaskPreview = false };
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
    private MagickImage PixelsFor(string path, EditHistory h, EditSteps hidden)
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

    // Pixelbild + Rezept, je Datei die letzten Fassungen (Ansicht der Werkzeuge, Augen ein/aus)
    private readonly Dictionary<string, List<(MagickImage Pixels, GeometryRecipe Recipe, GeometryStages Stages, MagickImage Image)>> _geometryDocs = [];
    private readonly List<MagickImage> _geometryTrash = [];
    // Je Fassung bis ~230 MB bei 28 MP — höchstens drei je Bild, andere Bilder beim Wechsel freigeben
    private const int GeometryDocsPerFile = 3;

    /// <summary>
    /// Das Bild, wie es angezeigt und gespeichert wird: Pixelbild (mit oder ohne KI …, <see cref="PixelsFor"/>) plus
    /// Ausrichten (<see cref="GeometryRecipe"/>) mit den eingeschalteten Stufen. <paramref name="view"/> schränkt die
    /// Stufen für die Ansicht eines Werkzeugs weiter ein (Frei drehen, Entzerren).
    /// </summary>
    private MagickImage DocumentFor(string path, EditHistory h, EditSteps hidden, GeometryStages view = GeometryStages.All) =>
        WithGeometry(path, h.Recipe, PixelsFor(path, h, hidden), StagesShown(hidden) & view);

    /// <summary>Ein Pixelbild mit Rezept (zwischengespeichert je Datei) — auch für die Vorher-Seite des Vergleichs.</summary>
    private MagickImage WithGeometry(string path, GeometryRecipe recipe, MagickImage pixels, GeometryStages stages)
    {
        if ((recipe.Present & stages) == 0) return pixels;

        lock (_geometryDocs)
        {
            if (_geometryDocs.TryGetValue(path, out var list)
                && list.FindIndex(e => ReferenceEquals(e.Pixels, pixels) && e.Recipe == recipe && e.Stages == stages) is var i and >= 0)
                return list[i].Image;
        }

        var doc = recipe.Apply(pixels, stages) ?? pixels;
        lock (_geometryDocs)
        {
            if (!_geometryDocs.TryGetValue(path, out var list)) _geometryDocs[path] = list = [];
            if (list.FindIndex(e => ReferenceEquals(e.Pixels, pixels) && e.Recipe == recipe && e.Stages == stages) is var i and >= 0)
            {
                // gleichzeitig schon gerechnet (Vorschau und Anzeige): eine Fassung behalten
                if (!ReferenceEquals(doc, pixels)) _geometryTrash.Add(doc);
                return list[i].Image;
            }
            list.Insert(0, (pixels, recipe, stages, doc));
            while (list.Count > GeometryDocsPerFile)
            {
                // Freigegeben wird erst, wenn keine Vorschau mehr darauf rechnet (EmptyGeometryTrash)
                if (!ReferenceEquals(list[^1].Image, list[^1].Pixels)) _geometryTrash.Add(list[^1].Image);
                list.RemoveAt(list.Count - 1);
            }
        }
        return doc;
    }

    /// <summary>Verdrängte Fassungen freigeben — nur aufrufen, wenn die Vorschau steht.</summary>
    private void EmptyGeometryTrash()
    {
        lock (_geometryDocs)
        {
            var inUse = _geometryDocs.Values.SelectMany(l => l.Select(e => e.Image)).ToHashSet(ReferenceEqualityComparer.Instance);
            foreach (var img in _geometryTrash.Where(i => !inUse.Contains(i) && !IsHeldByHistory(i))) img.Dispose();
            _geometryTrash.Clear();
        }
    }

    /// <summary>Fassungen anderer Bilder verwerfen (Bildwechsel) — für einen Export werden sie neu gerechnet.</summary>
    private void DropGeometryDocsExcept(string? path)
    {
        lock (_geometryDocs)
        {
            foreach (var other in _geometryDocs.Keys.Where(k => k != path).ToList())
            {
                _geometryTrash.AddRange(_geometryDocs[other].Where(e => !ReferenceEquals(e.Image, e.Pixels)).Select(e => e.Image));
                _geometryDocs.Remove(other);
            }
        }
    }

    /// <summary>Alle Fassungen mit Ausrichten freigeben (Bilder geschlossen).</summary>
    private void ClearGeometryDocs()
    {
        lock (_geometryDocs)
        {
            foreach (var e in _geometryDocs.Values.SelectMany(l => l))
                if (!ReferenceEquals(e.Image, e.Pixels) && !IsHeldByHistory(e.Image)) e.Image.Dispose();
            _geometryDocs.Clear();
            foreach (var img in _geometryTrash.Where(i => !IsHeldByHistory(i))) img.Dispose();
            _geometryTrash.Clear();
        }
    }

    /// <summary>Stufen, die ein offenes Werkzeug zeigt: Frei drehen ohne Drehung/Zuschnitt, Entzerren ohne alles bis auf Drehen/Spiegeln.</summary>
    private GeometryStages ToolView =>
        RotationTool ? GeometryRecipe.LevelView : PerspectiveTool ? GeometryRecipe.StraightenView
        : DewarpTool ? GeometryRecipe.DewarpView : GeometryStages.All;

    /// <summary>Das gerade angezeigte Bild — mit oder ohne KI …, mit den eingeschalteten Stufen, beim Werkzeug in dessen Ansicht.</summary>
    private MagickImage? ShownDocument =>
        _history is { } h && CurrentPath is { } path ? DocumentFor(path, h, HiddenSteps, ToolView) : null;

    /// <summary>Das fertige Bild (für Export, Größe, Druck) — nie die Ansicht eines Werkzeugs.</summary>
    private MagickImage? ResultDocument =>
        _history is { } h && CurrentPath is { } path ? DocumentFor(path, h, HiddenSteps) : null;

    /// <summary>Größe des Pixelbilds (vor dem Ausrichten).</summary>
    private (int Width, int Height) PixelSize()
    {
        if (_history is not { } h) return (0, 0);
        lock (h.Current) return ((int)h.Current.Width, (int)h.Current.Height);
    }

    /// <summary>Punkt im angezeigten Bild → Punkt im Pixelbild (Pipette, Pinsel beim Entfernen).</summary>
    internal (double X, double Y) ShownToPixels(double x, double y)
    {
        if (_history is not { } h) return (x, y);
        var (w, ht) = PixelSize();
        return h.Recipe.ToSource(x, y, w, ht, StagesShown(HiddenSteps) & ToolView);
    }

    /// <summary>Wie groß ein Abstand <paramref name="r"/> um (x, y) im Pixelbild ist (Pinselradius beim Entzerren verzerrt).</summary>
    internal double ShownLengthToPixels(double x, double y, double r)
    {
        var (cx, cy) = ShownToPixels(x, y);
        var (ax, ay) = ShownToPixels(x + r, y);
        var (bx, by) = ShownToPixels(x, y + r);
        return (Math.Sqrt((ax - cx) * (ax - cx) + (ay - cy) * (ay - cy)) + Math.Sqrt((bx - cx) * (bx - cx) + (by - cy) * (by - cy))) / 2;
    }

    // ------------------------------------------------------------ KI und Ausrichten
    //
    // Die KI bekommt das Pixelbild nur gedreht/gespiegelt — Gesichter stehen dann aufrecht, wie man sie sieht (die
    // Gesichtserkennung findet liegende Gesichter schlecht). Das Ergebnis wird exakt zurückgedreht, bevor es ins
    // Pixelbild kommt; Zwischenstände und Vorschau werden für die Anzeige zurückgedreht und mit dem Rezept gezeigt.

    /// <summary>Vierteldrehungen und Spiegeln, die die KI am Pixelbild vornimmt (nur, wenn Drehen/Spiegeln eingeschaltet ist).</summary>
    internal (int Turns, bool Flip) AiOrientation =>
        (CurrentStages & GeometryStages.Orient) != 0 ? (CurrentRecipe.NormalTurns, CurrentRecipe.Flip) : (0, false);

    /// <summary>Bild gedreht/gespiegelt; null = nichts zu tun.</summary>
    internal static MagickImage? Orient(MagickImage image, int turns, bool flip) =>
        new GeometryRecipe(Turns: turns, Flip: flip).Apply(image, GeometryStages.Orient);

    /// <summary>Umkehrung von <see cref="Orient"/> (Spiegeln samt Drehung ist sich selbst die Umkehrung).</summary>
    internal static MagickImage? Unorient(MagickImage image, int turns, bool flip) =>
        flip ? Orient(image, turns, true) : Orient(image, (4 - turns) % 4, false);

    /// <summary>Rezept und Stufen, wie das Bild gerade angezeigt wird (auf dem UI-Thread abholen, dann im Hintergrund benutzen).</summary>
    internal (GeometryRecipe Recipe, GeometryStages Stages) ShownGeometry => (CurrentRecipe, CurrentStages & ToolView);

    /// <summary>Vorschau-Pixel der KI (gedrehtes Pixelbild, verkleinert) so, wie das Bild angezeigt wird.</summary>
    internal static Core.Ai.AiPixels ShowWithGeometry(Core.Ai.AiPixels frame, int turns, bool flip, GeometryRecipe recipe, GeometryStages stages)
    {
        if ((recipe.Present & stages) == 0 && turns == 0 && !flip) return frame;
        using var image = Pixels.ToMagick16(frame.Bgra, frame.Width, frame.Height, alpha: true);
        using var upright = Unorient(image, turns, flip);
        var source = upright ?? image;
        using var shaped = recipe.Apply(source, stages);
        var result = shaped ?? source;
        return new Core.Ai.AiPixels(Pixels.FromMagick(result), (int)result.Width, (int)result.Height);
    }

    /// <summary>Wie <see cref="ShowWithGeometry(Core.Ai.AiPixels, int, bool, GeometryRecipe, GeometryStages)"/> für 8-Bit-Zwischenstände.</summary>
    internal static Core.Ai.AiPreviewFrame ShowWithGeometry(Core.Ai.AiPreviewFrame frame, int turns, bool flip, GeometryRecipe recipe, GeometryStages stages)
    {
        if ((recipe.Present & stages) == 0 && turns == 0 && !flip) return frame;
        var shown = ShowWithGeometry(new Core.Ai.AiPixels(Pixels.FromBytes(frame.Bgra), frame.Width, frame.Height), turns, flip, recipe, stages);
        return new Core.Ai.AiPreviewFrame(Pixels.ToBytes(shown.Bgra, dither: true), shown.Width, shown.Height);
    }

    /// <summary>Das Ausrichten des aktuellen Bildes (für die KI: nur Drehen/Spiegeln, damit Gesichter aufrecht stehen).</summary>
    internal GeometryRecipe CurrentRecipe => _history?.Recipe ?? GeometryRecipe.None;

    /// <summary>Eingeschaltete Stufen des aktuellen Bildes (ohne Werkzeug-Ansicht).</summary>
    internal GeometryStages CurrentStages => StagesShown(HiddenSteps);

    /// <summary>
    /// Das aktuelle Bild ohne KI (Zuschnitt, Drehen danach noch einmal angewendet) — Ausgangsbild für einen neuen
    /// KI-Lauf, damit sich KI nicht auf KI häuft. null: keine KI im Bild oder nicht abtrennbar.
    /// </summary>
    internal MagickImage? CurrentWithoutAi =>
        _history is { } h && CurrentPath is { } path && CanHideAi(path, h, out _) ? PixelsFor(path, h, EditSteps.Ai) : null;

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

    /// <summary>Angezeigtes Bild neu setzen (KI bzw. Haut &amp; Details oder eine Stufe des Ausrichtens ein/aus, Werkzeug auf/zu).</summary>
    /// <param name="sizeChanges">Das fertige Bild hat ein anderes Format — Größe, Druck und Export folgen.</param>
    private async Task ShowDocumentAsync(bool sizeChanges = false)
    {
        if (_history is null) return;
        // Im Hintergrund: Entzerren eines großen Bildes dauert Sekunden
        var doc = await Task.Run(() => ShownDocument);
        if (doc is null) return;
        await StopPreviewAsync();
        EmptyGeometryTrash();
        await SetDocumentAsync(doc);
        if (sizeChanges && await Task.Run(() => ResultDocument) is { } result)
        {
            int w, ht;
            lock (result) (w, ht) = ((int)result.Width, (int)result.Height);
            using (SuppressHistory()) FollowSourceSize(w, ht);
            UpdateFileDetails();
        }
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
            // Ausrichten: je Stufe eine Zeile, in der festen Reihenfolge des Rezepts (immer aus dem Pixelbild gerechnet)
            var recipe = h.Recipe;
            var (pw, ph) = PixelSize();
            if (recipe.HasDewarp && recipe.Dewarp is { } spec)
            {
                var map = recipe.DewarpMapFor(pw, ph);
                var (fw, fh) = recipe.FlatSize(pw, ph);
                var lines = spec.UsableLines.Count();
                var parts = new List<string>
                {
                    spec.Grid is not null ? Tr.T("KI (SyntheticDoc)") : lines == 1 ? Tr.T("1 Linie") : Tr.T("{0} Linien", lines),
                    $"{fw} × {fh} px",
                };
                if (map?.Din == true) parts.Add("DIN");
                if (spec.Grid?.Shading is { On: true }) parts.Add(Tr.T("Schatten entfernt"));
                rows.Add(Row(EditSteps.Dewarp, spec.Grid is not null ? Tr.T("KI begradigt") : Tr.T("Gerade gezogen"), string.Join(" · ", parts)));
            }
            if (recipe.HasStraighten)
            {
                var (sw, sh) = recipe.SizeFor(pw, ph, GeometryStages.Dewarp | GeometryStages.Straighten);
                rows.Add(Row(EditSteps.Straighten, Tr.T("Entzerrt"), $"{sw} × {sh} px"));
            }
            if (recipe.HasLevel)
                rows.Add(Row(EditSteps.Level, Tr.T("Gerade gerichtet"),
                    $"{recipe.Angle.ToString("+0.0;−0.0", System.Globalization.CultureInfo.CurrentCulture)}°"));
            if (recipe.HasCrop)
            {
                var (cw, ch) = recipe.SizeFor(pw, ph, GeometryStages.Dewarp | GeometryStages.Straighten | GeometryStages.Level | GeometryStages.Crop);
                rows.Add(Row(EditSteps.CropStep, Tr.T("Zuschnitt"),
                    recipe.CornerRadius > 0 ? Tr.T("{0} × {1} px · runde Ecken", cw, ch) : $"{cw} × {ch} px"));
            }
            if (recipe.HasOrient)
            {
                // Vierteldrehungen netto: dreimal rechts = 270°, ein Auge für alle
                var parts = new List<string>();
                if (recipe.NormalTurns != 0) parts.Add($"{recipe.NormalTurns * 90}°");
                if (recipe.Flip) parts.Add(Tr.T("gespiegelt"));
                rows.Add(Row(EditSteps.Orient, recipe.NormalTurns != 0 ? Tr.T("Gedreht") : Tr.T("Gespiegelt"), string.Join(" · ", parts)));
            }
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
                CanLimitLuminance ? string.Join(" · ", new[]
                {
                    MatchTarget >= 0.5 ? $"{Tr.T("Zielwert")} {Math.Round(MatchTarget):0} %" : null,
                    MatchLimit < 99.5 ? $"{Tr.T("Schwellwert")} {Math.Round(MatchLimit):0} %" : null,
                }.Where(t => t is not null)) is { Length: > 0 } extra ? extra : null : null,
                (Tr.T("Luminanz"), MatchLuminance), (Tr.T("Gesamt"), MatchGlobal), (Tr.T("Ausgleichen"), Neutralize), (Tr.T("Farbe"), Warmth));
            if (MaskAmount >= 0.5)
                rows.Add(Row(EditSteps.LumMask, Tr.T("Luminanzmaske"),
                    Tr.T("+{0:0} % · bis {1:0} % · Verlauf {2:0}", MaskAmount, MaskEnd, MaskCurve)));
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
            // Körnung zuletzt, wie in der Kette
            if (Math.Round(GrainAmount) >= 1)
                rows.Add(Row(EditSteps.Grain, Tr.T("Körnung"), string.Join(" · ", new[]
                {
                    $"{Math.Round(GrainAmount):0} %",
                    GrainSizeText(GrainSize),
                    Math.Round(GrainRoughness) >= 1 ? $"{Tr.T("Rauheit")} {Math.Round(GrainRoughness):0}" : null,
                    Math.Round(GrainColor) >= 1 ? $"{Tr.T("Farbe")} {Math.Round(GrainColor):0} %" : null,
                    Math.Round(GrainHardness) >= 1 ? $"{Tr.T("Härte")} {Math.Round(GrainHardness):0}" : null,
                }.Where(t => t is not null))));
        }

        EditRows.Clear();
        foreach (var row in rows) EditRows.Add(row);
        OnPropertyChanged(nameof(HasEditRows));
        OnPropertyChanged(nameof(AiStraightenActive));   // Symbol „KI begradigen“ folgt dem Rezept (auch nach Rückgängig)
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

        // Ausrichten: ausgeschaltete Stufen fliegen aus dem Rezept (im selben Schritt)
        var recipeAfter = _history is { } hr ? WithoutHiddenStages(hr.Recipe, hidden) : null;
        if (recipeAfter is not null && recipeAfter == _history!.Recipe) recipeAfter = null;

        if (_history is { } h && CurrentPath is { } path && PixelsFor(path, h, hidden) is var shown && !ReferenceEquals(shown, h.Current))
        {
            var copy = ImageOps.CloneOf(shown);
            var aiOut = (hidden & EditSteps.Ai) != 0;
            var steps = ImageStepsOf(path);
            var last = steps.Last(s => s.Step!.Kind == ImageStepKind.Ai).Step!;
            // KI ganz raus: ab hier zählt keine KI mehr. Nur Haut & Details raus: die KI bleibt, nur ohne sie.
            var step = aiOut
                ? new ImageStep(ImageStepKind.Applied, Tr.T("Auswahl angewendet"), Tr.T("ohne KI"), null)
                : new ImageStep(ImageStepKind.Ai, last.Label, last.Detail, null);
            await ApplyEditAsync(_ => copy, Cleared, step, recipeAfter);
            return;
        }

        if (recipeAfter is not null && _history is { } hg && CurrentPath is { } gpath)
        {
            await CommitRecipeAsync(recipeAfter, new ImageStep(ImageStepKind.Applied, Tr.T("Auswahl angewendet"), null, null), Cleared);
            return;
        }

        var before = CaptureSettings();
        ApplySettings(Cleared(before));
        _undo.Push(new Snapshot(ApplyToAll ? null : CurrentPath, null, before));
        ClearRedo();
        if (!ApplyToAll && CurrentPath is { } current) _fileSettings[current] = CaptureSettings();
        NotifyUndoRedo();
    }

    /// <summary>Rezept ohne die ausgeschalteten Stufen des Ausrichtens.</summary>
    private static GeometryRecipe WithoutHiddenStages(GeometryRecipe r, EditSteps hidden)
    {
        if ((hidden & EditSteps.Dewarp) != 0) r = r with { Dewarp = null };
        if ((hidden & EditSteps.Straighten) != 0) r = r with { Quad = null };
        if ((hidden & EditSteps.Level) != 0) r = r with { Angle = 0 };
        if ((hidden & EditSteps.CropStep) != 0) r = r with { Crop = null, CornerRadius = 0 };
        if ((hidden & EditSteps.Orient) != 0) r = r with { Turns = 0, Flip = false };
        return r;
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
        if ((hidden & EditSteps.MatchColor) != 0) s = s with { MatchLuminance = 0, MatchLimit = 100, MatchTarget = 0, MatchGlobal = 0, Neutralize = 0, Warmth = 0 };
        if ((hidden & EditSteps.WhiteBalance) != 0) s = s with { WbNeutral = null, WbBlack = null, WbWhite = null };
        if ((hidden & EditSteps.LumMask) != 0) s = s with { MaskAmount = 0 };
        if ((hidden & EditSteps.Sharpen) != 0) s = s with { SharpenAmount = 0 };
        if ((hidden & EditSteps.Grain) != 0) s = s with { GrainAmount = 0 };
        return s with { Hidden = EditSteps.None };
    }
}
