using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageConverter.Controls;
using ImageConverter.Core.Ai;
using ImageConverter.Core.Imaging;
using ImageConverter.Core.Platform;

namespace ImageConverter.ViewModels;

// Werkzeug „Gewölbtes Blatt“: Linien nachzeichnen, die in Wirklichkeit gerade sind (Textzeilen, Blattkanten, Falz) —
// daraus wird das Blatt geglättet (Core: Dewarp). Eine Stufe im Rezept vor dem Entzerren, immer aus dem Original.
public partial class MainViewModel
{
    /// <summary>Werkzeug „Gewölbtes Blatt“ offen: die Vorschau zeigt das Original (nur gedreht/gespiegelt) mit den Linien.</summary>
    [ObservableProperty] public partial bool DewarpTool { get; set; }

    /// <summary>Die Linien in Bildpixeln der Ansicht; null = Werkzeug aus.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCropToSelection))]
    public partial IReadOnlyList<WarpLineShape>? DewarpLines { get; set; }

    /// <summary>Zeichenmodus: jeder Klick setzt einen Punkt (Esc beendet); aus: Ziehen verschiebt das Bild.</summary>
    [ObservableProperty] public partial bool DewarpDrawing { get; set; }

    /// <summary>Unten in der Leiste: was herauskommt (Größe, Seitenverhältnis) oder was noch fehlt.</summary>
    [ObservableProperty] public partial string DewarpInfo { get; set; } = "";

    private bool HasDewarpLines => DewarpLines?.Any(l => l.Counts) == true;

    /// <summary>Es liegen Vorschläge da (für „Vorschläge verwerfen“).</summary>
    public bool HasDewarpSuggestions => DewarpLines?.Any(l => l.Suggested) == true;

    partial void OnDewarpToolChanged(bool value)
    {
        DewarpShowsResult = false;              // öffnet immer beim Justieren (Original mit Linien)
        if (value)
        {
            RotationTool = false;               // immer nur ein Werkzeug
            PerspectiveTool = false;
            CropSelection = null;
            if (!_quietTools) _ = EnterDewarpViewAsync();
        }
        else
        {
            DewarpDrawing = false;
            DewarpLines = null;
            if (!_quietTools) _ = ShowDocumentAsync();   // zurück zum fertigen Bild
        }
        OnPropertyChanged(nameof(CanCropToSelection));
        CropToSelectionCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Ansicht für das Werkzeug: das Pixelbild, nur gedreht/gespiegelt; die bisherigen Linien vorbelegt.</summary>
    private async Task EnterDewarpViewAsync()
    {
        if (_history is not { } h) return;
        await ShowDocumentAsync();
        if (!DewarpTool) return;
        var (pw, ph) = PixelSize();
        DewarpLines = h.Recipe.WarpLinesInView(pw, ph)
            .Select(l => new WarpLineShape(l.Kind, l.Points.Select(p => new Point(p.X, p.Y)).ToArray(), KindChosen: true))
            .ToArray();
    }

    partial void OnDewarpLinesChanged(IReadOnlyList<WarpLineShape>? value)
    {
        if (value is null && DewarpTool && !DewarpShowsResult) DewarpTool = false;   // Linien weg von außen: Werkzeug zu
        OnPropertyChanged(nameof(HasDewarpSuggestions));
        CropToSelectionCommand.NotifyCanExecuteChanged();
        _ = UpdateDewarpInfoAsync();
    }

    /// <summary>Alle Linien weg (übernommen wird erst mit „Gerade ziehen“).</summary>
    [RelayCommand]
    private void ResetDewarpLines()
    {
        if (DewarpTool) DewarpLines = [];
    }

    /// <summary>Linien werden gerade gesucht.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SuggestDewarpLinesCommand))]
    public partial bool IsSuggestingLines { get; set; }

    private bool CanSuggestLines => !IsSuggestingLines;

    /// <summary>
    /// Linien vorschlagen (Textzeilen, Blattrand, Falz — <see cref="LineFinder"/>), gestrichelt; frühere, unberührte
    /// Vorschläge werden ersetzt. Eigene Linien bleiben (auch übernommene, also geänderte Vorschläge), und was dicht an
    /// einer von ihnen liegt, wird nicht noch einmal vorgeschlagen. Vorschläge, die nicht zu den übrigen passen, kommen gar
    /// nicht erst dazu.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSuggestLines))]
    private async Task SuggestDewarpLines()
    {
        if (!DewarpTool) return;
        IsSuggestingLines = true;
        ActivityText = Tr.T("Linien werden gesucht …");
        ActivityProgress = -1;
        try
        {
            if (await Task.Run(() => ShownDocument) is not { } doc) return;
            var found = await Task.Run(() => FindLines(doc));
            if (!DewarpTool || DewarpLines is not { } current) return;
            var own = current.Where(l => !l.Suggested).ToList();
            var (pw, ph) = PixelSize();
            // „dicht an einer eigenen Linie“: 0,6 % der langen Bildseite, mindestens 6 px
            var tolerance = Math.Max(6, 0.006 * Math.Max(pw, ph));
            var suggested = found
                .Select(f => (f.Source, Line: new WarpLineShape(f.Kind, f.Points.Select(p => new Point(p.X, p.Y)).ToArray(),
                    KindChosen: true, Suggested: true)))
                .Where(s => !own.Any(o => o.Overlaps(s.Line, tolerance)))   // auch mit anderer Richtung: dieselbe Stelle, dieselbe Linie
                .ToList();
            var twins = found.Count - suggested.Count;

            // Prüfen: Vorschläge, die nicht zu den übrigen Linien passen (falsch erkannte Kante, verrutschte Zeile), weglassen
            ActivityText = Tr.T("Linien werden geprüft …");
            var lines = own.Concat(suggested.Select(s => s.Line)).ToList();
            var counted = lines.Where(l => l.Counts).ToList();
            var dropped = new HashSet<WarpLineShape>(ReferenceEqualityComparer.Instance);
            if (DewarpedRecipe(counted) is { Dewarp: { } spec })
            {
                var keep = await Task.Run(() => Dewarp.RejectOutliers(spec, counted.Select(l => l.Suggested).ToArray(), pw, ph));
                if (!DewarpTool) return;
                for (var k = 0; k < counted.Count && k < keep.Length; k++)
                    if (!keep[k] && counted[k].Suggested) dropped.Add(counted[k]);
            }
            if (DewarpLines is not { } now) return;
            // was in der Zwischenzeit von Hand dazukam, bleibt; nur die alten, unberührten Vorschläge gehen
            DewarpLines = now.Where(l => !l.Suggested)
                .Concat(suggested.Select(s => s.Line).Where(l => !dropped.Contains(l)))
                .ToArray();

            var kept = suggested.Where(s => !dropped.Contains(s.Line)).ToList();
            var count = (FoundLineSource s) => kept.Count(f => f.Source == s);
            StatusMessage = found.Count == 0
                ? Tr.T("Keine Linien gefunden – bitte von Hand setzen.")
                : Tr.T("Vorgeschlagen: {0} Textzeilen, {1} Blattkanten, {2} gezeichnete Linien, {3} Flächenkanten, {4} Textränder, {5} Falz – Klick auf eine Linie zeichnet an ihr weiter, das × entfernt sie.",
                      count(FoundLineSource.Text), count(FoundLineSource.Edge), count(FoundLineSource.Ruled), count(FoundLineSource.Box), count(FoundLineSource.Margin), count(FoundLineSource.Fold))
                  + (dropped.Count == 0 ? "" : " " + (dropped.Count == 1
                      ? Tr.T("1 Vorschlag passte nicht zu den übrigen und wurde weggelassen.")
                      : Tr.T("{0} Vorschläge passten nicht zu den übrigen und wurden weggelassen.", dropped.Count)))
                  + (twins == 0 ? "" : " " + (twins == 1
                      ? Tr.T("1 Vorschlag lag auf einer eigenen Linie und wurde weggelassen.")
                      : Tr.T("{0} Vorschläge lagen auf eigenen Linien und wurden weggelassen.", twins)));
        }
        catch (Exception ex)
        {
            ErrorMessage = Tr.T("Linien suchen fehlgeschlagen: {0}", ex.Message);
        }
        finally
        {
            IsSuggestingLines = false;
            ActivityText = null;
        }
    }

    /// <summary>Linien im angezeigten Bild suchen (verkleinert auf <see cref="LineFinder.WorkEdge"/>), in Pixeln der Ansicht.</summary>
    private static IReadOnlyList<FoundLine> FindLines(ImageMagick.MagickImage doc)
    {
        int w, h;
        lock (doc) (w, h) = ((int)doc.Width, (int)doc.Height);
        var scale = Math.Min(1.0, LineFinder.WorkEdge / (double)Math.Max(w, h));
        int sw = Math.Max(1, (int)Math.Round(w * scale)), sh = Math.Max(1, (int)Math.Round(h * scale));
        using var small = ImageOps.Resize(doc, sw, sh, ResampleFilter.Mitchell, gammaCorrect: false);
        var px = ImageOps.ToDisplayPixels(small);
        try
        {
            double fx = (double)w / sw, fy = (double)h / sh;
            var found = LineFinder.Find(px, sw, sh)
                .Select(l => l with { Points = l.Points.Select(p => (p.X * fx, p.Y * fy)).ToArray() })
                .ToArray();
            if (scale >= 0.999) return found;

            // im Original nachschärfen (Kanten, Falz, gezeichnete Linien) — auf der verkleinerten Fassung liegt ein Punkt
            // sonst bis zu einigen Pixeln daneben
            float[] gray;
            using (var g = ImageOps.CloneOf(doc))
            {
                g.Grayscale(ImageMagick.PixelIntensityMethod.Rec709Luma);
                using var pixels = g.GetPixelsUnsafe();
                var values = pixels.ToShortArray(0, 0, (uint)w, (uint)h, "R")
                             ?? throw new InvalidOperationException("Pixel konnten nicht gelesen werden.");
                gray = new float[values.Length];
                for (var i = 0; i < values.Length; i++) gray[i] = values[i] / 65535f;
            }
            return LineFinder.Refine(found, gray, w, h, scale);
        }
        finally
        {
            PixelPool.Return(px);
        }
    }

    /// <summary>Alle unberührten Vorschläge weg; eigene und geänderte Linien bleiben.</summary>
    [RelayCommand]
    private void DiscardDewarpSuggestions()
    {
        if (DewarpLines is { } lines) DewarpLines = lines.Where(l => !l.Suggested).ToArray();
    }

    /// <summary>Rezept mit den Linien der Ansicht (Hilfe für Anzeige und Übernehmen).</summary>
    private GeometryRecipe? DewarpedRecipe(IReadOnlyList<WarpLineShape> lines)
    {
        if (_history is not { } h) return null;
        var (pw, ph) = PixelSize();
        return h.Recipe.DewarpedInView(
            lines.Where(l => l.Counts).Select(l => (l.Kind, (IReadOnlyList<(double X, double Y)>)l.Points.Select(p => (p.X, p.Y)).ToArray())),
            pw, ph, Dewarp.Focal35Of(h.Current));
    }

    private CancellationTokenSource? _dewarpInfoCts;

    /// <summary>Ergebnis der Linien ausrechnen (im Hintergrund, die letzte Änderung gewinnt).</summary>
    private async Task UpdateDewarpInfoAsync()
    {
        _dewarpInfoCts?.Cancel();
        if (DewarpLines is not { } lines || DewarpedRecipe(lines) is not { } recipe)
        {
            DewarpInfo = "";
            return;
        }
        var count = lines.Count(l => l.Counts);
        if (count == 0)
        {
            DewarpInfo = Tr.T("Linien vorschlagen lassen oder Punkte auf eine Linie setzen, die eigentlich gerade ist.");
            return;
        }

        // Nicht mit „using“: der nächste Aufruf bricht _dewarpInfoCts ab — war es schon entsorgt, warf Cancel(), und die
        // Zeile blieb ab der zweiten Änderung auf dem ersten Stand stehen. Ohne Zeitgeber braucht es kein Dispose.
        var cts = _dewarpInfoCts = new CancellationTokenSource();
        var (pw, ph) = PixelSize();
        try
        {
            await Task.Delay(120, cts.Token);   // beim Ziehen nicht jede Bewegung rechnen
            var map = await Task.Run(() => recipe.DewarpMapFor(pw, ph), cts.Token);
            if (cts.IsCancellationRequested) return;
            DewarpInfo = DescribeDewarp(map, count, pw);
        }
        catch (OperationCanceledException)
        {
            // neuere Linien kommen gleich
        }
        finally
        {
            if (ReferenceEquals(_dewarpInfoCts, cts)) _dewarpInfoCts = null;
        }
    }

    private static string DescribeDewarp(DewarpMap? map, int lines, int width)
    {
        if (map is null) return Tr.T("Mit diesen Linien lässt sich nichts gerade ziehen.");
        var (w, h) = map.SizeFor(width);
        var parts = new List<string> { lines == 1 ? Tr.T("1 Linie") : Tr.T("{0} Linien", lines), Tr.T("Ergebnis: {0} × {1} px", w, h) };
        if (map.PageAspect is { } a)
            parts.Add(map.Din ? Tr.T("Seite 1 : {0:0.00} (DIN)", Math.Max(a, 1 / a)) : Tr.T("Seite 1 : {0:0.00}", Math.Max(a, 1 / a)));
        if (!map.Modelled) parts.Add(Tr.T("nur die Linien gerade gezogen – für das ganze Blatt mehr Linien setzen"));
        return string.Join(" · ", parts);
    }

    // ------------------------------------------------------------ KI begradigen (SyntheticDoc)

    /// <summary>Die KI-Begradigung ist im Rezept (Symbol gedrückt).</summary>
    public bool AiStraightenActive => CurrentRecipe.Dewarp?.Grid is not null;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StraightenWithAiCommand))]
    public partial bool IsStraighteningWithAi { get; set; }

    // nicht an HasImage koppeln: der Befehl erführe nichts davon und bliebe gesperrt; ohne Bild ist das Symbol ohnehin unsichtbar
    private bool CanStraightenWithAi => !IsUpscaling && !IsStraighteningWithAi;

    /// <summary>
    /// „Schatten entfernen“ (Rechtsklick auf das KI-Symbol): die Schattenkarte von SyntheticDoc beim Rendern herausrechnen.
    /// Die App merkt sich die Wahl; ist die KI-Begradigung schon im Bild, wird sie gleich umgeschaltet (ein Schritt, Strg+Z).
    /// </summary>
    [ObservableProperty] public partial bool AiRemoveShadows { get; set; } = AppSettings.Load().AiRemoveShadows;

    partial void OnAiRemoveShadowsChanged(bool value)
    {
        (AppSettings.Load() with { AiRemoveShadows = value }).Save();
        _ = SwitchAiShadowsAsync(value);
    }

    private async Task SwitchAiShadowsAsync(bool on)
    {
        if (_history is not { } h || h.Recipe.Dewarp is not { Grid: { Shading: { } shading } grid } spec || shading.On == on) return;
        var next = h.Recipe with { Dewarp = spec with { Grid = grid with { Shading = shading with { On = on } } } };
        await CommitRecipeAsync(next, new ImageStep(ImageStepKind.Crop, on ? Tr.T("Schatten entfernt") : Tr.T("Schatten wieder drin"),
            Tr.T("KI begradigt"), null), s => s with { Hidden = s.Hidden & ~EditSteps.Dewarp });
    }

    /// <summary>
    /// „KI begradigen“: SyntheticDoc rechnet aus dem Bild (nur gedreht/gespiegelt, verkleinert auf 1600 px) das Gitter, damit
    /// wird das Blatt gerade gezogen — ein Schritt, Strg+Z macht ihn rückgängig; Linien des Werkzeugs „Gewölbtes Blatt“
    /// werden ersetzt. Ist die KI-Begradigung schon drin, nimmt der Klick sie wieder heraus.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanStraightenWithAi))]
    private async Task StraightenWithAi()
    {
        if (_history is not { } h) return;
        if (AiStraightenActive)
        {
            CloseToolsQuietly();
            await CommitRecipeAsync(h.Recipe with { Dewarp = null }, new ImageStep(ImageStepKind.Crop, Tr.T("KI-Begradigung entfernt"), null, null));
            OnPropertyChanged(nameof(AiStraightenActive));
            return;
        }
        var model = AiModelCatalog.SyntheticDoc;
        if (Ai.Store.PathOf(model) is not { } checkpoint)
        {
            ErrorMessage = Tr.T("Das Modell „{0}“ fehlt – im Tab KI herunterladen.", model.Name);
            OnPropertyChanged(nameof(AiStraightenActive));
            return;
        }
        IsStraighteningWithAi = true;
        AppLog.Activity($"KI begradigen: {model.Name}");
        var fallback = System.IO.Path.Combine(Ai.Store.DownloadFolder, model.Id);
        ActivityText = Core.Ai.SyntheticDoc.SyntheticDocModel.NeedsBuild(checkpoint, fallback)
            ? Tr.T("KI-Modell wird vorbereitet …")
            : Tr.T("KI begradigt …");
        ActivityProgress = -1;
        try
        {
            CloseToolsQuietly();
            var focal = Dewarp.Focal35Of(h.Current);
            var grid = await Task.Run(() =>
            {
                if (CurrentPath is not { } path || DocumentFor(path, h, HiddenSteps, GeometryRecipe.DewarpView) is not { } doc) return null;
                var (gridOnnx, shadowOnnx) = Core.Ai.SyntheticDoc.SyntheticDocModel.OnnxFor(checkpoint, fallback);
                var session = Ai.Sessions.Get(model, gridOnnx);
                var shadowSession = Ai.Sessions.Get(model, shadowOnnx);
                // Schattenkarte immer mitrechnen (0,6 s): dann schaltet „Schatten entfernen“ ohne neuen Lauf um
                return Core.Ai.SyntheticDoc.SyntheticDocDewarp.Run(session, shadowSession, doc, focal, AiRemoveShadows, CancellationToken.None);
            });
            if (grid is null) return;
            var (pw, ph) = PixelSize();
            var next = h.Recipe.AiDewarpedInView(grid, pw, ph, focal);
            var (fw, fh) = await Task.Run(() => next.FlatSize(pw, ph));
            var detail = $"{fw} × {fh} px";
            if (grid.PageAspect is { } a)
                detail += " · " + (grid.Din ? Tr.T("Seite 1 : {0:0.00} (DIN)", Math.Max(a, 1 / a)) : Tr.T("Seite 1 : {0:0.00}", Math.Max(a, 1 / a)));
            if (grid.Shading is { On: true }) detail += " · " + Tr.T("Schatten entfernt");
            await CommitRecipeAsync(next, new ImageStep(ImageStepKind.Crop, Tr.T("KI begradigt"), detail, null),
                s => s with { Hidden = s.Hidden & ~EditSteps.Dewarp });
        }
        catch (Exception ex)
        {
            ErrorMessage = Tr.T("KI begradigen fehlgeschlagen: {0}", ex.Message);
        }
        finally
        {
            IsStraighteningWithAi = false;
            AppLog.Activity(null);
            ActivityText = null;
            OnPropertyChanged(nameof(AiStraightenActive));
        }
    }

    /// <summary>
    /// „Gerade ziehen“: die Linien ins Rezept — ein Schritt, Strg+Z macht ihn rückgängig. Das Werkzeug bleibt offen und zeigt
    /// das Ergebnis (<see cref="DewarpShowsResult"/>); „Justieren“ holt Original und Linien zurück, verlassen über das Symbol.
    /// </summary>
    private async Task ApplyDewarpAsync(IReadOnlyList<WarpLineShape> lines)
    {
        if (_history is not { } h || DewarpedRecipe(lines) is not { } next) return;
        var (pw, ph) = PixelSize();
        var count = lines.Count(l => l.Counts);
        string label, detail, info;
        if (next.HasDewarp)
        {
            var (w, ht) = await Task.Run(() => next.FlatSize(pw, ph));
            var map = await Task.Run(() => next.DewarpMapFor(pw, ph));
            (label, detail) = (Tr.T("Gerade gezogen"), $"{(count == 1 ? Tr.T("1 Linie") : Tr.T("{0} Linien", count))} · {w} × {ht} px");
            info = DescribeDewarp(map, count, pw);
        }
        else
        {
            if (!h.Recipe.HasDewarp) return;   // nichts zu tun: weiter justieren
            (label, detail) = (Tr.T("Gerade ziehen entfernt"), Tr.T("keine Linien"));
            info = Tr.T("Keine Linien – das Blatt ist nicht mehr gerade gezogen.");
        }

        // Ergebnis zeigen, Werkzeug offen lassen: erst umschalten, dann die Linien weg (sonst schlösse das das Werkzeug)
        DewarpDrawing = false;
        DewarpShowsResult = true;
        DewarpResultInfo = info;
        DewarpLines = null;
        _keepDewarpTool = true;   // der neue Stand setzt die Größen zurück und schlösse dabei das Werkzeug
        try
        {
            await CommitRecipeAsync(next, new ImageStep(ImageStepKind.Crop, label, detail, null),
                s => s with { Hidden = s.Hidden & ~EditSteps.Dewarp });
        }
        finally
        {
            _keepDewarpTool = false;
        }
    }

    // ------------------------------------------------------------ Ergebnis zeigen, justieren

    /// <summary>Werkzeug offen, aber nach „Gerade ziehen“: die Vorschau zeigt das Ergebnis, unten „Justieren“.</summary>
    [ObservableProperty] public partial bool DewarpShowsResult { get; set; }

    /// <summary>Unten im Ergebnis: Linien, Größe, Seitenverhältnis.</summary>
    [ObservableProperty] public partial string DewarpResultInfo { get; set; } = "";

    // während „Gerade ziehen“ übernimmt: das Werkzeug bleibt offen (ApplySourceSize schlösse es sonst)
    private bool _keepDewarpTool;

    /// <summary>„Justieren“: wieder das Original mit den verwendeten Linien — nacharbeiten und noch einmal gerade ziehen.</summary>
    [RelayCommand]
    private async Task AdjustDewarp()
    {
        if (!DewarpTool) return;
        DewarpShowsResult = false;
        await EnterDewarpViewAsync();
    }

    /// <summary>Im Bild steckt eine Begradigung mit Linien (Punkt am Symbol, Stift in „Bearbeitungen“).</summary>
    public bool HasAppliedDewarpLines => CurrentRecipe.Dewarp is { Grid: null, Lines.Count: > 0 };

    /// <summary>Stift in der Liste „Bearbeitungen“: das Werkzeug mit den verwendeten Linien öffnen.</summary>
    [RelayCommand]
    private void EditDewarpLines()
    {
        if (!DewarpTool) DewarpTool = true;   // öffnet direkt beim Justieren
        else if (DewarpShowsResult) _ = AdjustDewarp();
        ShowEditList = false;
    }
}
