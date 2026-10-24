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

// "Curved page" tool: trace lines that are straight in reality (text lines, page edges, fold); the page is flattened
// from them (Core: Dewarp). A stage in the recipe before perspective correction, always computed from the original.
public partial class MainViewModel
{
    /// <summary>The "Curved page" tool is open: the preview shows the original (only rotated/flipped) with the lines.</summary>
    [ObservableProperty] public partial bool DewarpTool { get; set; }

    /// <summary>The lines in image pixels of the view; null if the tool is off.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCropToSelection))]
    public partial IReadOnlyList<WarpLineShape>? DewarpLines { get; set; }

    /// <summary>Drawing mode: each click sets a point (Esc ends it); when off, dragging pans the image.</summary>
    [ObservableProperty] public partial bool DewarpDrawing { get; set; }

    /// <summary>Shown at the bottom of the panel: the result (size, aspect ratio) or what is still missing.</summary>
    [ObservableProperty] public partial string DewarpInfo { get; set; } = "";

    private bool HasDewarpLines => DewarpLines?.Any(l => l.Counts) == true;

    /// <summary>Suggested lines are present (shows the "Clear suggestions" button).</summary>
    public bool HasDewarpSuggestions => DewarpLines?.Any(l => l.Suggested) == true;

    partial void OnDewarpToolChanged(bool value)
    {
        DewarpShowsResult = false;              // always opens in adjust mode (original with lines)
        if (value)
        {
            RotationTool = false;               // only one tool at a time
            PerspectiveTool = false;
            CropSelection = null;
            if (!_quietTools) _ = EnterDewarpViewAsync();
        }
        else
        {
            DewarpDrawing = false;
            DewarpLines = null;
            if (!_quietTools) _ = ShowDocumentAsync();   // back to the finished image
        }
        OnPropertyChanged(nameof(CanCropToSelection));
        CropToSelectionCommand.NotifyCanExecuteChanged();
    }

    /// <summary>View for the tool: the pixel image, only rotated/flipped, pre-filled with the lines already in the recipe.</summary>
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
        if (value is null && DewarpTool && !DewarpShowsResult) DewarpTool = false;   // lines removed from outside: close the tool
        OnPropertyChanged(nameof(HasDewarpSuggestions));
        CropToSelectionCommand.NotifyCanExecuteChanged();
        _ = UpdateDewarpInfoAsync();
    }

    /// <summary>Removes all lines from the view; the recipe changes only with "Straighten out".</summary>
    [RelayCommand]
    private void ResetDewarpLines()
    {
        if (DewarpTool) DewarpLines = [];
    }

    /// <summary>A line search is in progress.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SuggestDewarpLinesCommand))]
    public partial bool IsSuggestingLines { get; set; }

    private bool CanSuggestLines => !IsSuggestingLines;

    /// <summary>
    /// Suggests lines (text lines, page edge, fold; see <see cref="LineFinder"/>), drawn dashed; earlier untouched
    /// suggestions are replaced. Own lines stay (including adopted, that is modified, suggestions), and anything close to
    /// one of them is not suggested again. Suggestions that do not agree with the remaining lines are not added at all.
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
            // "close to an own line": 0.6 % of the long image side, at least 6 px
            var tolerance = Math.Max(6, 0.006 * Math.Max(pw, ph));
            var suggested = found
                .Select(f => (f.Source, Line: new WarpLineShape(f.Kind, f.Points.Select(p => new Point(p.X, p.Y)).ToArray(),
                    KindChosen: true, Suggested: true)))
                .Where(s => !own.Any(o => o.Overlaps(s.Line, tolerance)))   // regardless of direction: same place, same line
                .ToList();
            var twins = found.Count - suggested.Count;

            // Check: leave out suggestions that do not agree with the other lines (misdetected edge, shifted text line).
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
            // lines added by hand in the meantime stay; only the old, untouched suggestions are removed
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

    /// <summary>Searches lines in the displayed image (scaled down to <see cref="LineFinder.WorkEdge"/>); returns them in pixels of the view.</summary>
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

            // Refine on the full-resolution image (edges, fold, ruled lines); on the reduced version a point can
            // otherwise be off by up to a few pixels.
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

    /// <summary>Removes all untouched suggestions; own and modified lines stay.</summary>
    [RelayCommand]
    private void DiscardDewarpSuggestions()
    {
        if (DewarpLines is { } lines) DewarpLines = lines.Where(l => !l.Suggested).ToArray();
    }

    /// <summary>Recipe with the lines of the view (used for the info text and for applying).</summary>
    private GeometryRecipe? DewarpedRecipe(IReadOnlyList<WarpLineShape> lines)
    {
        if (_history is not { } h) return null;
        var (pw, ph) = PixelSize();
        return h.Recipe.DewarpedInView(
            lines.Where(l => l.Counts).Select(l => (l.Kind, (IReadOnlyList<(double X, double Y)>)l.Points.Select(p => (p.X, p.Y)).ToArray())),
            pw, ph, Dewarp.Focal35Of(h.Current));
    }

    private CancellationTokenSource? _dewarpInfoCts;

    /// <summary>Computes the result of the lines for the info text, in the background; the latest change wins.</summary>
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

        // Not with "using": the next call cancels _dewarpInfoCts; if it had already been disposed, Cancel() threw and the
        // info line stayed at the first result from the second change on. Without a timer no Dispose is needed.
        var cts = _dewarpInfoCts = new CancellationTokenSource();
        var (pw, ph) = PixelSize();
        try
        {
            await Task.Delay(120, cts.Token);   // do not compute every movement while dragging
            var map = await Task.Run(() => recipe.DewarpMapFor(pw, ph), cts.Token);
            if (cts.IsCancellationRequested) return;
            DewarpInfo = DescribeDewarp(map, count, pw);
        }
        catch (OperationCanceledException)
        {
            // newer lines are on their way
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

    // ------------------------------------------------------------ Straighten with AI (SyntheticDoc)

    /// <summary>The AI straightening is part of the recipe (icon shown as pressed).</summary>
    public bool AiStraightenActive => CurrentRecipe.Dewarp?.Grid is not null;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StraightenWithAiCommand))]
    public partial bool IsStraighteningWithAi { get; set; }

    // Not coupled to HasImage: the command would not be notified of changes and would stay disabled; without an image the icon is invisible anyway.
    private bool CanStraightenWithAi => !IsUpscaling && !IsStraighteningWithAi;

    /// <summary>
    /// "Remove shadows" (right-click on the AI icon): removes the SyntheticDoc shadow map during rendering.
    /// The choice is saved in the app settings; if the AI straightening is already applied, it is switched immediately
    /// (one step, undoable with Ctrl+Z).
    /// </summary>
    [ObservableProperty] public partial bool AiRemoveShadows { get; set; } = AppSettings.Load().AiRemoveShadows;

    partial void OnAiRemoveShadowsChanged(bool value)
    {
        AppSettings.Update(s => s with { AiRemoveShadows = value });
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
    /// "Straighten with AI": SyntheticDoc computes the grid from the image (only rotated/flipped, scaled to the model
    /// input size, see <see cref="Core.Ai.SyntheticDoc.SyntheticDocDewarp"/>) and the page is flattened with it. This is
    /// one step, undoable with Ctrl+Z; lines of the "Curved page" tool are replaced. If the AI straightening is already
    /// applied, the click removes it again.
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
                // Always compute the shadow map as well (0.6 s), so that "Remove shadows" switches without a new run.
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
    /// "Straighten out": writes the lines into the recipe as one step, undoable with Ctrl+Z. The tool stays open and shows
    /// the result (<see cref="DewarpShowsResult"/>); "Adjust" brings back the original with the lines; the tool is left
    /// via its icon.
    /// </summary>
    /// <param name="lines">The lines of the view.
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
            if (!h.Recipe.HasDewarp) return;   // nothing to do: keep adjusting
            (label, detail) = (Tr.T("Gerade ziehen entfernt"), Tr.T("keine Linien"));
            info = Tr.T("Keine Linien – das Blatt ist nicht mehr gerade gezogen.");
        }

        // Show the result and keep the tool open: switch first, then remove the lines (otherwise that would close the tool).
        DewarpDrawing = false;
        DewarpShowsResult = true;
        DewarpResultInfo = info;
        DewarpLines = null;
        _keepDewarpTool = true;   // the new state resets the sizes and would close the tool in doing so
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

    // ------------------------------------------------------------ Show result, adjust

    /// <summary>The tool is open after "Straighten out": the preview shows the result, with "Adjust" at the bottom.</summary>
    [ObservableProperty] public partial bool DewarpShowsResult { get; set; }

    /// <summary>Shown below the result: lines, size, aspect ratio.</summary>
    [ObservableProperty] public partial string DewarpResultInfo { get; set; } = "";

    // while "Straighten out" is being applied the tool stays open (ApplySourceSize would close it otherwise)
    private bool _keepDewarpTool;

    /// <summary>"Adjust": shows the original with the lines in use again, so that they can be refined and applied once more.</summary>
    [RelayCommand]
    private async Task AdjustDewarp()
    {
        if (!DewarpTool) return;
        DewarpShowsResult = false;
        await EnterDewarpViewAsync();
    }

    /// <summary>The image contains a straightening with lines (dot on the icon, pencil in the "Edits" list).</summary>
    public bool HasAppliedDewarpLines => CurrentRecipe.Dewarp is { Grid: null, Lines.Count: > 0 };

    /// <summary>Pencil in the "Edits" list: opens the tool with the lines in use.</summary>
    [RelayCommand]
    private void EditDewarpLines()
    {
        if (!DewarpTool) DewarpTool = true;   // opens directly in adjust mode
        else if (DewarpShowsResult) _ = AdjustDewarp();
        ShowEditList = false;
    }
}
