using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageConverter.Core.Imaging;
using ImageMagick;

namespace ImageConverter.ViewModels;

// Zuschneiden, Drehen, Spiegeln und der Verlauf dafür.
public partial class MainViewModel
{
    /// <summary>
    /// Ein Schritt im Verlauf: der Stand <b>vor</b> der Änderung. Bei Bildänderungen
    /// (KI, Entfernen) ist <see cref="Image"/> das Bild davor und
    /// <see cref="FilePath"/> die Datei; beim Ausrichten (Drehen, Zuschnitt …) ist <see cref="Recipe"/> das Rezept
    /// davor; bei reinen Einstellungsänderungen ist alles drei null.
    /// </summary>
    private sealed record Snapshot(string? FilePath, MagickImage? Image, SettingsState Settings,
        bool? ApplyToAll = null, IReadOnlyDictionary<string, SettingsState>? FileSettings = null,
        SettingsState? Template = null, MainViewModel.ImageStep? Step = null, GeometryRecipe? Recipe = null);

    /// <summary>Das Bild einer Datei: Original, aktuelles Pixelbild und das Ausrichten als Rezept.</summary>
    private sealed class EditHistory(MagickImage original)
    {
        public MagickImage Original { get; } = original;

        /// <summary>Pixelbild: Original plus KI und Entfernen — ungedreht und unbeschnitten.</summary>
        public MagickImage Current { get; set; } = original;

        /// <summary>Ausrichten (Entzerren, Gerade richten, Zuschnitt, Drehen/Spiegeln) — erst beim Anzeigen und Speichern angewandt.</summary>
        public GeometryRecipe Recipe { get; set; } = GeometryRecipe.None;

        public long FileBytes { get; init; }
        public string FormatName { get; init; } = "";
        public double Dpi { get; init; } = ImageLoader.DefaultDpi;

        /// <summary>Weicht vom geladenen Bild ab (zugeschnitten, gedreht, KI …).</summary>
        public bool IsEdited => !ReferenceEquals(Current, Original) || !Recipe.IsNone;
    }

    // Ein gemeinsamer Verlauf für alles: Einstellungen gelten für alle Bilder,
    // Bildänderungen merken sich ihre Datei. „Rückgängig" nimmt immer den
    // letzten Schritt zurück — notfalls auch an einem anderen Bild.
    private readonly Stack<Snapshot> _undo = new();
    private readonly Stack<Snapshot> _redo = new();

    private readonly Dictionary<string, EditHistory> _histories = new(StringComparer.Ordinal);
    private EditHistory? _history;

    // Während eines Bearbeitungsschritts (z. B. KI) nicht: das Ergebnis gehört zum Bild davor
    /// <summary>Drehen und Spiegeln: nur mit Bild und nicht, während skaliert wird.</summary>
    public bool CanTransform => HasImage && !IsUpscaling;

    public bool CanUndo => !IsEditing && !IsUpscaling && (_undo.Count > 0 || HasPendingSettings);
    public bool CanRedo => !IsEditing && !IsUpscaling && _redo.Count > 0 && !HasPendingSettings;

    [ObservableProperty] public partial bool IsEditing { get; set; }

    partial void OnIsEditingChanged(bool value) => NotifyUndoRedo();

    private void NotifyUndoRedo()
    {
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        UndoCommand.NotifyCanExecuteChanged();
        RequestEditRows();
        RedoCommand.NotifyCanExecuteChanged();
        NotifyCompare();
    }

    /// <summary>
    /// Bild für „Vorher“: im Verlauf dieser Datei zurück, solange die Form gleich bleibt —
    /// also vor KI-Schritten, nicht aber vor einem Zuschnitt oder Drehen (andere Form).
    /// Ein Vergrößern per KI (gleiches Seitenverhältnis) zählt mit. null: kein früherer Stand.
    /// </summary>
    private MagickImage? FindBeforeImage()
    {
        if (_history is not { } h || CurrentPath is not { } path) return null;

        var before = h.Current;
        foreach (var step in _undo)   // neuester zuerst
        {
            if (step.FilePath != path || step.Image is not { } image) continue;
            if (!SameShape(image, before)) break;
            before = image;
        }

        return ReferenceEquals(before, h.Current) ? null : before;
    }

    /// <summary>Gleiches Seitenverhältnis (auf einen Pixel genau).</summary>
    private static bool SameShape(MagickImage a, MagickImage b) =>
        Math.Abs((double)a.Width * b.Height - (double)b.Width * a.Height) <= Math.Max(a.Width, b.Width);

    private void ClearRedo()
    {
        foreach (var s in _redo)
        {
            s.Image?.Dispose();
            // KI ohne Haut & Details: gehört nur zu diesem Schritt, der jetzt wegfällt
            if (s.Step?.Plain is { } plain && !_undo.Any(u => ReferenceEquals(u.Step, s.Step))) plain.Dispose();
        }
        _redo.Clear();
    }

    /// <summary>Muss die Datei im Speicher bleiben, weil der Verlauf noch auf sie zeigt?</summary>
    private bool IsReferencedByHistory(string path, EditHistory h) =>
        h.IsEdited
        || HasFile(_undo, path)
        || HasFile(_redo, path);

    private static bool HasFile(Stack<Snapshot> stack, string path)
    {
        foreach (var s in stack)
            if (s.FilePath == path) return true;
        return false;
    }

    /// <summary>
    /// Der Ausschnitt wird zum neuen Bild. Bei runden Ecken ist alles außerhalb
    /// der Rundung danach transparent; das Bild bleibt rechteckig.
    /// </summary>
    /// <summary>Zuschneiden geht mit Auswahl — oder beim Entzerren mit einem brauchbaren Viereck.</summary>
    public bool CanCropToSelection => !IsUpscaling && HasCropSelection || (PerspectiveTool && IsPerspectiveValid)
        || (DewarpTool && (HasDewarpLines || CurrentRecipe.HasDewarp));

    [RelayCommand(CanExecute = nameof(CanCropToSelection))]
    private async Task CropToSelection()
    {
        if (_history is not { } h) return;
        var (pw, ph) = PixelSize();

        // Gewölbtes Blatt: die Linien ins Rezept
        if (DewarpTool && DewarpLines is { } lines)
        {
            await ApplyDewarpAsync(lines);
            return;
        }

        // Entzerren: Viereck in der Ansicht des Werkzeugs (Bild nach der Wölbung, nur gedreht/gespiegelt) ins Rezept
        if (PerspectiveTool && PerspectiveQuad is { } quad)
        {
            var pts = AsTuples(quad);
            if (!PerspectiveGeometry.IsValid(pts)) return;
            var next = h.Recipe.StraightenedInView(pts, pw, ph, StagesShown(HiddenSteps));
            var (tw, th) = next.SizeFor(pw, ph, GeometryStages.Straighten | (StagesShown(HiddenSteps) & GeometryStages.Dewarp));
            CloseToolsQuietly();
            await CommitRecipeAsync(next, new ImageStep(ImageStepKind.Crop, Tr.T("Entzerrt"), $"{tw} × {th} px", null),
                s => s with { Hidden = s.Hidden & ~EditSteps.Straighten });
            return;
        }

        if (CropSelection is not { } sel) return;
        var radius = CornerRadius;

        // Frei drehen: Winkel und Rahmen der Ansicht (ohne Drehung und Zuschnitt) ersetzen die bisherigen
        if (RotationTool)
        {
            var next = h.Recipe.LevelledInView(RotationAngle, sel.X, sel.Y, sel.Width, sel.Height, radius, pw, ph);
            var detail = $"{next.Angle.ToString("+0.0;-0.0", System.Globalization.CultureInfo.CurrentCulture)}° · {(int)Math.Round(sel.Width)} × {(int)Math.Round(sel.Height)} px";
            CloseToolsQuietly();
            await CommitRecipeAsync(next, new ImageStep(ImageStepKind.Rotate, Tr.T("Gerade gerichtet"), detail, null),
                s => s with { Hidden = s.Hidden & ~(EditSteps.Level | EditSteps.CropStep) });
            return;
        }

        // Zuschnitt im angezeigten Bild: enger schneiden — oder, wenn der Zuschnitt ausgeschaltet ist, ihn ersetzen
        var cropped = h.Recipe.CroppedInResult(sel.X, sel.Y, sel.Width, sel.Height, radius, pw, ph, StagesShown(HiddenSteps));
        await CommitRecipeAsync(cropped,
            new ImageStep(ImageStepKind.Crop, Tr.T("Zuschnitt"), $"{(int)Math.Round(sel.Width)} × {(int)Math.Round(sel.Height)} px", null),
            s => s with { Hidden = s.Hidden & ~(EditSteps.CropStep | ((s.Hidden & EditSteps.Level) != 0 ? EditSteps.Level : 0)) });
    }

    /// <summary>
    /// Ein Schritt des Ausrichtens: neues Rezept, das alte kommt auf den Rückgängig-Stapel (mit den Einstellungen davor).
    /// Das Pixelbild bleibt — angezeigt wird es mit dem neuen Rezept.
    /// </summary>
    private async Task<bool> CommitRecipeAsync(GeometryRecipe next, ImageStep step, Func<SettingsState, SettingsState>? settingsAfter = null)
    {
        if (_history is not { } h || IsEditing || _fileIndex < 0) return false;
        CommitPendingSettings();
        var path = _files[_fileIndex];
        var before = CaptureSettings();
        _undo.Push(new Snapshot(path, null, before, Step: step, Recipe: h.Recipe));
        ClearRedo();
        h.Recipe = next;
        if (settingsAfter is not null) ChangeSettings(path, settingsAfter);
        await ShowCurrentAsync();
        NotifyUndoRedo();
        return true;
    }

    // Werkzeug zu, ohne die Ansicht dafür noch einmal umzurechnen (gleich folgt das neue Rezept)
    private bool _quietTools;

    private void CloseToolsQuietly()
    {
        _quietTools = true;
        try
        {
            RotationTool = false;
            PerspectiveTool = false;
            DewarpTool = false;
        }
        finally
        {
            _quietTools = false;
        }
    }

    // ------------------------------------------------------------ Frei drehen

    /// <summary>Werkzeug „Frei drehen“: Regler unten in der Vorschau; der Auswahlrahmen gehört dazu.</summary>
    [ObservableProperty] public partial bool RotationTool { get; set; }

    /// <summary>
    /// Noch nicht übernommene freie Drehung in Grad (−180 … +180, positiv = im Uhrzeigersinn). Die Vorschau zeigt
    /// sie, übernommen wird sie zusammen mit dem Rahmen beim Zuschneiden. Der Rahmen gilt im ungedrehten
    /// Koordinatensystem um die Bildmitte (<see cref="RotationGeometry"/>).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRotationPending))]
    public partial double RotationAngle { get; set; }

    public bool IsRotationPending => Math.Abs(RotationAngle) > 1e-9;

    partial void OnRotationToolChanged(bool value)
    {
        if (value)
        {
            PerspectiveTool = false;              // immer nur ein Werkzeug
            DewarpTool = false;
            SelectionTool = true;                 // ohne Rahmen kein Zuschnitt nach dem Drehen
            if (!_quietTools) _ = EnterRotationViewAsync();
        }
        else
        {
            if (IsRotationPending) DiscardRotation();
            CropSelection = null;
            if (!_quietTools) _ = ShowDocumentAsync();   // zurück zum fertigen Bild
        }
    }

    /// <summary>
    /// Ansicht für „Frei drehen“: das Bild ohne freie Drehung und Zuschnitt, Winkel und Rahmen des Rezepts vorbelegt —
    /// so lässt sich ein Zuschnitt auch wieder erweitern.
    /// </summary>
    private async Task EnterRotationViewAsync()
    {
        if (_history is not { } h) return;
        await ShowDocumentAsync();
        if (!RotationTool) return;   // inzwischen wieder zu
        var (pw, ph) = PixelSize();
        var (angle, frame) = h.Recipe.LevelInView(pw, ph);
        if (Math.Abs(angle) > 1e-9) RotationAngle = Math.Round(angle, 1);
        if (frame is { } f) CropSelection = new Rect(Math.Round(f.X), Math.Round(f.Y), Math.Round(f.W), Math.Round(f.H));
    }

    // ------------------------------------------------------------ Entzerren

    /// <summary>Werkzeug „Entzerren“: Viereck mit frei verschiebbaren Ecken auf etwas Rechteckiges legen.</summary>
    [ObservableProperty] public partial bool PerspectiveTool { get; set; }

    /// <summary>Die vier Ecken (oben links, oben rechts, unten rechts, unten links) in Bildpixeln; null = Werkzeug aus.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPerspectiveValid), nameof(PerspectiveInfo), nameof(CanCropToSelection))]
    public partial IReadOnlyList<Point>? PerspectiveQuad { get; set; }

    public bool IsPerspectiveValid => PerspectiveQuad is { Count: 4 } q && PerspectiveGeometry.IsValid(AsTuples(q));

    /// <summary>Unten in der Leiste: Größe des Ergebnisses oder was am Viereck nicht stimmt.</summary>
    public string PerspectiveInfo
    {
        get
        {
            if (PerspectiveQuad is not { Count: 4 } q) return "";
            if (!IsPerspectiveValid) return Tr.T("Die Ecken überkreuzen sich – bitte so legen, dass ein Viereck entsteht.");
            var (w, h) = PerspectiveGeometry.TargetSize(AsTuples(q));
            return Tr.T("Ergebnis: {0} × {1} px", w, h);
        }
    }

    private static IReadOnlyList<(double X, double Y)> AsTuples(IReadOnlyList<Point> q) => q.Select(p => (p.X, p.Y)).ToArray();

    partial void OnPerspectiveToolChanged(bool value)
    {
        if (value)
        {
            RotationTool = false;
            DewarpTool = false;
            CropSelection = null;
            if (!_quietTools) _ = EnterPerspectiveViewAsync();
        }
        else
        {
            PerspectiveQuad = null;
            if (!_quietTools) _ = ShowDocumentAsync();   // zurück zum fertigen Bild
        }
        OnPropertyChanged(nameof(CanCropToSelection));
        CropToSelectionCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Ansicht für „Entzerren“: das Pixelbild, nur gedreht/gespiegelt; das bisherige Viereck vorbelegt.</summary>
    private async Task EnterPerspectiveViewAsync()
    {
        if (_history is not { } h) return;
        await ShowDocumentAsync();
        if (!PerspectiveTool) return;
        var (pw, ph) = PixelSize();
        if (h.Recipe.QuadInView(pw, ph, StagesShown(HiddenSteps)) is { } q) PerspectiveQuad = q.Select(p => new Point(p.X, p.Y)).ToArray();
        else ResetPerspectiveQuad();
    }

    partial void OnPerspectiveQuadChanged(IReadOnlyList<Point>? value)
    {
        if (value is null && PerspectiveTool) PerspectiveTool = false;   // Esc in der Vorschau
        CropToSelectionCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Ecken an den Anfang: ein Zehntel vom Bildrand nach innen.</summary>
    [RelayCommand]
    private void ResetPerspectiveQuad()
    {
        // Größe der angezeigten Ansicht (beim Entzerren das Pixelbild, nur gedreht/gespiegelt)
        double w = DocumentSize.Width > 0 ? DocumentSize.Width : SourceWidth, h = DocumentSize.Height > 0 ? DocumentSize.Height : SourceHeight;
        double ix = w * 0.1, iy = h * 0.1;
        PerspectiveQuad = [new Point(ix, iy), new Point(w - ix, iy), new Point(w - ix, h - iy), new Point(ix, h - iy)];
    }

    partial void OnRotationAngleChanged(double value)
    {
        // Auf −180 … +180 und Zehntelgrad — Regler und Zahlenfeld zeigen dasselbe
        var tidy = double.IsFinite(value) ? Math.Round(RotationGeometry.Normalize(value), 1) : 0;
        if (tidy != value) { RotationAngle = tidy; return; }

        // Steht eine Drehung an (z. B. nach Rückgängig), gehört der Regler sichtbar dazu
        if (IsRotationPending && !RotationTool) RotationTool = true;
    }

    /// <summary>Drehung verwerfen: Winkel 0, Rahmen weg (Esc in der Vorschau tut dasselbe).</summary>
    [RelayCommand]
    private void DiscardRotation()
    {
        RotationAngle = 0;
        CropSelection = null;
    }

    // Drehen und Spiegeln: nur das Rezept (netto Vierteldrehungen), ein Rückgängig-Schritt je Klick
    [RelayCommand(CanExecute = nameof(CanTransform))]
    private Task RotateLeft() => Orient(r => r.Rotated(false), new ImageStep(ImageStepKind.Rotate, Tr.T("Links gedreht"), "90°", null));

    [RelayCommand(CanExecute = nameof(CanTransform))]
    private Task RotateRight() => Orient(r => r.Rotated(true), new ImageStep(ImageStepKind.Rotate, Tr.T("Rechts gedreht"), "90°", null));

    [RelayCommand(CanExecute = nameof(CanTransform))]
    private Task FlipHorizontal() => Orient(r => r.Flipped(), new ImageStep(ImageStepKind.Flip, Tr.T("Gespiegelt"), Tr.T("links ↔ rechts"), null));

    private async Task Orient(Func<GeometryRecipe, GeometryRecipe> change, ImageStep step)
    {
        if (_history is not { } h) return;
        if (RotationTool || PerspectiveTool || DewarpTool) CloseToolsQuietly();   // offenes Werkzeug verwerfen
        if (IsRotationPending) DiscardRotation();
        // Ist Drehen/Spiegeln ausgeschaltet, gilt erst einmal nur der neue Klick — sichtbar wird es auf jeden Fall
        var basis = (HiddenSteps & EditSteps.Orient) != 0 ? h.Recipe with { Turns = 0, Flip = false } : h.Recipe;
        await CommitRecipeAsync(change(basis), step, s => s with { Hidden = s.Hidden & ~EditSteps.Orient });
    }

    /// <summary>
    /// Ein Bearbeitungsschritt auf dem echten Bild (ImageMagick, im Hintergrund).
    /// Das Ergebnis wird das neue Bild; Bild und Einstellungen davor kommen
    /// als ein Schritt auf den Rückgängig-Stapel.
    /// </summary>
    /// <param name="settingsAfter">Einstellungen danach (z. B. Look auf null) — im selben Schritt; null: bleiben.</param>
    /// <param name="step">Was der Schritt war — für die Liste „Bearbeitungen“ (und zum Wiederholen auf anderem Bild).</param>
    /// <param name="recipeAfter">Ausrichten danach (im selben Schritt, z. B. „Neu anwenden“); null: bleibt.</param>
    private async Task<bool> ApplyEditAsync(Func<MagickImage, MagickImage> operation,
        Func<SettingsState, SettingsState>? settingsAfter, ImageStep step, GeometryRecipe? recipeAfter = null)
    {
        if (_history is not { } h || IsEditing || _fileIndex < 0) return false;

        CommitPendingSettings();   // offene Reglerbewegungen sind ein eigener Schritt davor
        IsEditing = true;
        try
        {
            // Lange Schritte (KI): inzwischen kann ein anderes Bild angezeigt werden
            var path = _files[_fileIndex];
            var source = h.Current;
            var before = CaptureSettings();
            var result = await Task.Run(() => operation(source));

            await StopPreviewAsync();   // Wiederholen-Bilder könnten gerade gelesen werden
            _undo.Push(new Snapshot(path, source, before, Step: step, Recipe: recipeAfter is null ? null : h.Recipe));
            ClearRedo();
            h.Current = result;
            if (recipeAfter is not null) h.Recipe = recipeAfter;
            if (settingsAfter is not null) ChangeSettings(path, settingsAfter);   // gehört zu diesem Schritt (Snapshot hat den Stand davor)
            if (h == _history) await ShowCurrentAsync();
            return true;
        }
        catch (OperationCanceledException)
        {
            // abgebrochen — Bild bleibt, wie es war
            return false;
        }
        catch (Exception ex)
        {
            ErrorMessage = Tr.T("Bearbeiten fehlgeschlagen: {0}", ex.Message);
            return false;
        }
        finally
        {
            IsEditing = false;
            NotifyUndoRedo();
        }
    }

    /// <summary>
    /// Einstellungen einer Datei ändern, ohne eigenen Rückgängig-Schritt — gehört zum Bildschritt davor.
    /// </summary>
    private void ChangeSettings(string path, Func<SettingsState, SettingsState> change)
    {
        if (path == CurrentPath)
        {
            ApplySettings(change(CaptureSettings()));
            if (!ApplyToAll) _fileSettings[path] = CaptureSettings();
        }
        else if (!ApplyToAll && _fileSettings.TryGetValue(path, out var saved))
        {
            _fileSettings[path] = change(saved);
        }
    }

    /// <summary>
    /// Für Erweiterungen (z. B. KI): ein Bearbeitungsschritt auf dem aktuellen Bild,
    /// mit Rückgängig/Wiederholen wie Zuschneiden und Drehen. Abbruch über
    /// <see cref="OperationCanceledException"/> lässt das Bild unverändert. Liefert true bei Erfolg.
    /// </summary>
    /// <param name="operation">Liefert das Ergebnis und, falls es Haut &amp; Details gibt, dieselbe KI ohne sie.</param>
    /// <param name="label">Name in der Liste „Bearbeitungen“ (z. B. „KI: Gesichter – CodeFormer“).</param>
    /// <param name="detail">Kurzinfo dazu (Stärke).</param>
    /// <param name="skinDetail">Kurzinfo für die Zeile „Haut &amp; Details“; null = keine.</param>
    /// <param name="lookAfter">Look, der danach gilt (z. B. neutral, wenn er ins Bild gerechnet wurde) — im selben
    /// Schritt, Rückgängig bringt Bild und Regler zusammen zurück. null: Regler bleiben.</param>
    internal Task<bool> ApplyImageEditAsync(Func<MagickImage, (MagickImage Result, MagickImage? Plain)> operation,
        string label, string? detail, string? skinDetail, Look? lookAfter = null)
    {
        var step = new ImageStep(ImageStepKind.Ai, label, detail, null, skinDetail);
        return ApplyEditAsync(img =>
            {
                var (result, plain) = operation(img);
                step.Plain = plain;
                return result;
            },
            lookAfter is null ? null : s => WithLook(s, lookAfter), step);
    }

    /// <summary>
    /// Für das Entfernen (Tab „Entfernen“): ein Bildschritt mit Rückgängig wie Zuschneiden und Drehen. Die Operation
    /// bekommt das aktuelle Bild, darf aber von einem früheren Stand aus rechnen (neuer Versuch mit anderem Modell) —
    /// Rückgängig führt dann zum vorigen Ergebnis zurück.
    /// </summary>
    /// <param name="replaces">Neuer Versuch vom Bild vor dem letzten Entfernen — ersetzt es in der Liste „Bearbeitungen“.</param>
    internal Task<bool> ApplyRetouchAsync(Func<MagickImage, MagickImage> operation, string detail, bool replaces) =>
        ApplyEditAsync(operation, null, new ImageStep(ImageStepKind.Retouch, Tr.T("Entfernt"), detail, null) { Replaces = replaces });

    /// <summary>Das aktuelle Bild in voller Auflösung (mit allen Bearbeitungen) — nur lesen.</summary>
    internal MagickImage? CurrentImage => _history?.Current;

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private async Task Undo()
    {
        CommitPendingSettings();
        if (!_undo.TryPop(out var previous)) return;

        _redo.Push(await RestoreAsync(previous));
        NotifyUndoRedo();
    }

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private async Task Redo()
    {
        if (!_redo.TryPop(out var next)) return;

        _undo.Push(await RestoreAsync(next));
        NotifyUndoRedo();
    }

    /// <summary>
    /// Stellt einen Schritt wieder her und liefert sein Gegenstück für den anderen
    /// Stapel (Rückgängig ↔ Wiederholen) — gleicher Art, mit dem Stand von jetzt.
    /// </summary>
    private async Task<Snapshot> RestoreAsync(Snapshot step)
    {
        // Gehört der Schritt zu einem anderen Bild, erst dorthin wechseln
        if (step.FilePath is { } path && path != CurrentPath)
        {
            var index = _files.IndexOf(path);
            if (index >= 0) await ShowFileAsync(index);
        }

        var settingsNow = CaptureSettings();

        // Bildschritt: Bild tauschen; Ausrichten: Rezept tauschen
        MagickImage? imageNow = null;
        GeometryRecipe? recipeNow = null;
        if ((step.Image is not null || step.Recipe is not null) && step.FilePath is not null && _histories.TryGetValue(step.FilePath, out var h))
        {
            await StopPreviewAsync();
            if (step.Image is not null)
            {
                imageNow = h.Current;
                h.Current = step.Image;
            }
            if (step.Recipe is not null)
            {
                recipeNow = h.Recipe;
                h.Recipe = step.Recipe;
            }
            if (h == _history)
            {
                CloseToolsQuietly();
                await ShowCurrentAsync();
            }
        }

        // Umschalten „für alle / einzeln": Modus und alle Einzelstände zurück
        bool? modeNow = null;
        SettingsState? templateNow = null;
        Dictionary<string, SettingsState>? fileSettingsNow = null;
        if (step.ApplyToAll is { } mode)
        {
            modeNow = ApplyToAll;
            templateNow = _individualTemplate;
            fileSettingsNow = new Dictionary<string, SettingsState>(_fileSettings);
            using (SuppressHistory())
                ApplyToAll = mode;
            _fileSettings = step.FileSettings is { } saved
                ? new Dictionary<string, SettingsState>(saved)
                : new Dictionary<string, SettingsState>(StringComparer.Ordinal);
            _individualTemplate = step.Template;
        }

        ApplySettings(step.Settings);
        if (!ApplyToAll && CurrentPath is { } current)
            _fileSettings[current] = CaptureSettings();

        return new Snapshot(step.FilePath, imageNow, settingsNow, modeNow, fileSettingsNow, templateNow, step.Step, recipeNow);
    }

    /// <summary>Zeigt das aktuelle Bild der Datei; Pixelmaße beginnen wieder beim Bild.</summary>
    private async Task ShowCurrentAsync()
    {
        if (_history is not { } h) return;
        CommitPendingSettings();   // z. B. Regler bewegt, während die KI rechnete

        // Pixelbild + Rezept im Hintergrund (Entzerren eines großen Bildes dauert)
        DropGeometryDocsExcept(CurrentPath);
        var shown = await Task.Run(() => ShownDocument) ?? h.Current;
        var result = await Task.Run(() => ResultDocument) ?? h.Current;
        EmptyGeometryTrash();
        await SetDocumentAsync(shown);   // bei ausgeblendeter KI das Bild ohne sie

        int w, hgt;
        lock (result) (w, hgt) = ((int)result.Width, (int)result.Height);
        UpdateFileDetails();

        using (SuppressHistory())
            ApplySourceSize(w, hgt);
        ResetHistoryBaseline();

        NotifyUndoRedo();
        RequestPreview();
    }

    /// <summary>Zeile unter dem Dateinamen: Maße, Größe, Format.</summary>
    private void UpdateFileDetails()
    {
        if (_history is not { } h) return;
        var (pw, ph) = PixelSize();
        var (dw, dh) = h.Recipe.SizeFor(pw, ph, StagesShown(HiddenSteps));
        var doc = (Width: dw, Height: dh);
        FileDetails = h.IsEdited
            ? Tr.T("{0} × {1} px  ·  {2}  ·  bearbeitet", doc.Width, doc.Height, h.FormatName)
            : $"{doc.Width} × {doc.Height} px  ·  {FormatBytes(h.FileBytes)}  ·  {h.FormatName}";
    }

    /// <summary>Alles freigeben — jedes Bild genau einmal, auch wenn es mehrfach referenziert ist.</summary>
    private void DisposeAllHistories()
    {
        ClearWithoutAi();   // Bilder ohne KI — vor dem Verlauf, der sie noch kennt
        ClearGeometryDocs();
        var images = new HashSet<MagickImage>(ReferenceEqualityComparer.Instance);
        foreach (var h in _histories.Values) { images.Add(h.Original); images.Add(h.Current); }
        foreach (var s in _undo) if (s.Image is not null) images.Add(s.Image);
        foreach (var s in _redo) if (s.Image is not null) images.Add(s.Image);
        foreach (var s in _undo.Concat(_redo)) if (s.Step?.Plain is { } plain) images.Add(plain);
        foreach (var img in images) img.Dispose();

        _histories.Clear();
        _undo.Clear();
        _redo.Clear();
        _history = null;
        _fileSettings.Clear();
        _individualTemplate = null;
        _pendingBaseline = null;
        NotifyUndoRedo();
    }

    // ----------------------------------------------------------- Zuschnitt

    /// <summary>Auswahl in Bildpixeln; null = keine Auswahl. Kommt vom CropView.</summary>
    [ObservableProperty] public partial Rect? CropSelection { get; set; }

    [ObservableProperty] public partial string SelectedAspectPreset { get; set; }
    [ObservableProperty] public partial int CropX { get; set; }
    [ObservableProperty] public partial int CropY { get; set; }
    [ObservableProperty] public partial int CropWidth { get; set; }
    [ObservableProperty] public partial int CropHeight { get; set; }

    // Verhindert Ping-Pong zwischen Auswahlrahmen und Zahlenfeldern
    private bool _isSyncingCrop;

    /// <summary>Eckenradius in Bildpixeln.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CornerRadiusLabel), nameof(ShowTransparencyWarning))]
    public partial double CornerRadius { get; set; }

    public bool HasCropSelection => CropSelection is not null;

    /// <summary>
    /// Maximum = halbe längste Seite. Am Anschlag wird die Auswahl zur Ellipse,
    /// bei einer quadratischen Auswahl zum Kreis.
    /// </summary>
    public double MaxCornerRadius =>
        CropSelection is { } s ? Math.Max(1, Math.Max(s.Width, s.Height) / 2) : 1;

    public string CornerRadiusLabel
    {
        get
        {
            if (CornerRadius < 0.5) return Tr.T("eckig");
            if (CornerRadius >= MaxCornerRadius - 0.5)
                return CropWidth == CropHeight ? Tr.T("Kreis") : Tr.T("Ellipse");
            return $"{CornerRadius:F0} px";
        }
    }

    /// <summary>Runde Ecken werden transparent — JPEG und BMP können das nicht.</summary>
    public bool ShowTransparencyWarning =>
        CornerRadius >= 0.5 && SelectedFormat is "JPEG" or "BMP";

    public double AspectRatio => SelectedAspectPreset switch
    {
        "Original" => SourceHeight > 0 ? (double)SourceWidth / SourceHeight : 0,
        "1:1" => 1,
        "4:3" => 4.0 / 3,
        "3:2" => 3.0 / 2,
        "16:9" => 16.0 / 9,
        _ => 0,
    };

    partial void OnSelectedAspectPresetChanged(string value) => OnPropertyChanged(nameof(AspectRatio));

    partial void OnCropSelectionChanged(Rect? value)
    {
        OnPropertyChanged(nameof(HasCropSelection));
        OnPropertyChanged(nameof(MaxCornerRadius));
        CropToSelectionCommand.NotifyCanExecuteChanged();

        if (value is null)
            CornerRadius = 0;
        else if (CornerRadius > MaxCornerRadius)
            CornerRadius = MaxCornerRadius;

        if (_isSyncingCrop) return;
        _isSyncingCrop = true;
        if (value is { } r)
        {
            CropX = (int)r.X;
            CropY = (int)r.Y;
            CropWidth = (int)r.Width;
            CropHeight = (int)r.Height;
        }
        _isSyncingCrop = false;
        OnPropertyChanged(nameof(CornerRadiusLabel));
    }

    partial void OnCropXChanged(int value) => PushCropFieldsToSelection();
    partial void OnCropYChanged(int value) => PushCropFieldsToSelection();
    partial void OnCropWidthChanged(int value) => PushCropFieldsToSelection();
    partial void OnCropHeightChanged(int value) => PushCropFieldsToSelection();

    private void PushCropFieldsToSelection()
    {
        if (_isSyncingCrop || CropSelection is null) return;

        // Größe der angezeigten Ansicht (bei „Frei drehen“ das Bild ohne Drehung und Zuschnitt)
        int vw = DocumentSize.Width > 0 ? DocumentSize.Width : SourceWidth, vh = DocumentSize.Height > 0 ? DocumentSize.Height : SourceHeight;
        var x = Math.Clamp(CropX, 0, vw - 1);
        var y = Math.Clamp(CropY, 0, vh - 1);
        var w = Math.Clamp(CropWidth, 1, vw - x);
        var h = Math.Clamp(CropHeight, 1, vh - y);
        var rect = new Rect(x, y, w, h);

        // Beim Drehen: nicht über die schrägen Kanten hinaus — um die Mitte verkleinern
        if (IsRotationPending)
        {
            var (fx, fy, fw, fh) = RotationGeometry.ShrinkInside(vw, vh, RotationAngle, x, y, w, h);
            var x0 = Math.Ceiling(fx - 1e-6);
            var y0 = Math.Ceiling(fy - 1e-6);
            rect = new Rect(x0, y0, Math.Max(1, Math.Floor(fx + fw + 1e-6) - x0), Math.Max(1, Math.Floor(fy + fh + 1e-6) - y0));
        }

        _isSyncingCrop = true;
        CropSelection = rect;
        _isSyncingCrop = false;
        if (rect != new Rect(x, y, w, h)) OnCropSelectionChanged(rect);   // Zahlenfelder auf den verkleinerten Rahmen
    }

    /// <summary>Auswahl aufheben — beim Drehen auch die Drehung, denn der Rahmen gehört zu ihr.</summary>
    [RelayCommand]
    private void ClearSelection()
    {
        if (IsRotationPending) RotationAngle = 0;
        CropSelection = null;
    }
}
