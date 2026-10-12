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

    /// <summary>Es gibt abgewählte (graue) Linien — für „Abgewählte entfernen“.</summary>
    public bool HasDisabledDewarpLines => DewarpLines?.Any(l => !l.Enabled) == true;

    partial void OnDewarpToolChanged(bool value)
    {
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
        if (value is null && DewarpTool) DewarpTool = false;   // Esc in der Vorschau
        OnPropertyChanged(nameof(HasDewarpSuggestions));
        OnPropertyChanged(nameof(HasDisabledDewarpLines));
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
    /// Linien vorschlagen (Textzeilen, Blattrand, Falz — <see cref="LineFinder"/>), gestrichelt und angewählt; frühere
    /// Vorschläge werden ersetzt, von Hand gesetzte Linien bleiben.
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
            var suggested = found.Select(f => new WarpLineShape(f.Kind, f.Points.Select(p => new Point(p.X, p.Y)).ToArray(),
                KindChosen: true, Suggested: true)).ToList();
            var lines = current.Where(l => !l.Suggested).Concat(suggested).ToList();

            // Prüfen: Vorschläge, die nicht zu den übrigen Linien passen (falsch erkannte Kante, verrutschte Zeile), abwählen —
            // sie bleiben grau sichtbar und lassen sich wieder anwählen
            ActivityText = Tr.T("Linien werden geprüft …");
            var counted = lines.Where(l => l.Counts).ToList();
            var off = 0;
            if (DewarpedRecipe(counted) is { Dewarp: { } spec })
            {
                var (pw, ph) = PixelSize();
                var keep = await Task.Run(() => Dewarp.RejectOutliers(spec, counted.Select(l => l.Suggested).ToArray(), pw, ph));
                if (!DewarpTool) return;
                for (var k = 0; k < counted.Count && k < keep.Length; k++)
                {
                    if (keep[k]) continue;
                    lines[lines.IndexOf(counted[k])] = counted[k] with { Enabled = false };
                    off++;
                }
            }
            DewarpLines = lines;

            var count = (FoundLineSource s) => found.Count(f => f.Source == s);
            StatusMessage = found.Count == 0
                ? Tr.T("Keine Linien gefunden – bitte von Hand setzen.")
                : Tr.T("Vorgeschlagen: {0} Textzeilen, {1} Blattkanten, {2} gezeichnete Linien, {3} Flächenkanten, {4} Textränder, {5} Falz – ein Klick auf eine gestrichelte Linie wählt sie ab oder an.",
                      count(FoundLineSource.Text), count(FoundLineSource.Edge), count(FoundLineSource.Ruled), count(FoundLineSource.Box), count(FoundLineSource.Margin), count(FoundLineSource.Fold))
                  + (off == 0 ? "" : " " + (off == 1
                      ? Tr.T("1 Vorschlag passt nicht zu den übrigen und ist abgewählt (grau).")
                      : Tr.T("{0} Vorschläge passen nicht zu den übrigen und sind abgewählt (grau).", off)));
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

    /// <summary>Alle abgewählten (grauen) Linien entfernen.</summary>
    [RelayCommand]
    private void RemoveDisabledDewarpLines()
    {
        if (DewarpLines is { } lines) DewarpLines = lines.Where(l => l.Enabled).ToArray();
    }

    /// <summary>Alle Vorschläge weg (angewählte wie abgewählte); von Hand gesetzte Linien bleiben.</summary>
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
            DewarpInfo = Tr.T("Punkte auf eine Linie setzen, die in Wirklichkeit gerade ist – Textzeile, Blattkante, Falz.");
            return;
        }

        using var cts = _dewarpInfoCts = new CancellationTokenSource();
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

    private bool CanStraightenWithAi => HasImage && !IsStraighteningWithAi;

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
        if (Ai.Store.PathOf(model) is not { } modelPath)
        {
            ErrorMessage = Tr.T("Das Modell „{0}“ fehlt: die Datei {1} nach {2} legen.", model.Name, model.MainFile,
                System.IO.Path.Combine(Ai.Store.DownloadFolder, model.Id));
            OnPropertyChanged(nameof(AiStraightenActive));
            return;
        }
        IsStraighteningWithAi = true;
        ActivityText = Tr.T("KI begradigt …");
        ActivityProgress = -1;
        try
        {
            CloseToolsQuietly();
            var focal = Dewarp.Focal35Of(h.Current);
            var grid = await Task.Run(() =>
            {
                if (CurrentPath is not { } path || DocumentFor(path, h, HiddenSteps, GeometryRecipe.DewarpView) is not { } doc) return null;
                int w, ht;
                lock (doc) (w, ht) = ((int)doc.Width, (int)doc.Height);
                var scale = Math.Min(1.0, LineFinder.WorkEdge / (double)Math.Max(w, ht));
                int sw = Math.Max(1, (int)Math.Round(w * scale)), sh = Math.Max(1, (int)Math.Round(ht * scale));
                using var small = ImageOps.Resize(doc, sw, sh, ResampleFilter.Mitchell, gammaCorrect: false);
                var px = ImageOps.ToDisplayPixels(small);
                try
                {
                    var session = Ai.Sessions.Get(model, modelPath);
                    return Core.Ai.AiDewarp.Run(session, px, sw, sh, focal, CancellationToken.None);
                }
                finally
                {
                    PixelPool.Return(px);
                }
            });
            if (grid is null) return;
            var (pw, ph) = PixelSize();
            var next = h.Recipe.AiDewarpedInView(grid, pw, ph, focal);
            var (fw, fh) = await Task.Run(() => next.FlatSize(pw, ph));
            var detail = $"{fw} × {fh} px";
            if (grid.PageAspect is { } a)
                detail += " · " + (grid.Din ? Tr.T("Seite 1 : {0:0.00} (DIN)", Math.Max(a, 1 / a)) : Tr.T("Seite 1 : {0:0.00}", Math.Max(a, 1 / a)));
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
            ActivityText = null;
            OnPropertyChanged(nameof(AiStraightenActive));
        }
    }

    /// <summary>„Gerade ziehen“: die Linien ins Rezept — ein Schritt, Strg+Z macht ihn rückgängig.</summary>
    private async Task ApplyDewarpAsync(IReadOnlyList<WarpLineShape> lines)
    {
        if (_history is not { } h || DewarpedRecipe(lines) is not { } next) return;
        var (pw, ph) = PixelSize();
        var count = lines.Count(l => l.Counts);
        string label, detail;
        if (next.HasDewarp)
        {
            var (w, ht) = await Task.Run(() => next.FlatSize(pw, ph));
            (label, detail) = (Tr.T("Gerade gezogen"), $"{(count == 1 ? Tr.T("1 Linie") : Tr.T("{0} Linien", count))} · {w} × {ht} px");
        }
        else
        {
            if (!h.Recipe.HasDewarp) { CloseToolsQuietly(); await ShowDocumentAsync(); return; }   // nichts zu tun
            (label, detail) = (Tr.T("Gerade ziehen entfernt"), Tr.T("keine Linien"));
        }
        CloseToolsQuietly();
        await CommitRecipeAsync(next, new ImageStep(ImageStepKind.Crop, label, detail, null),
            s => s with { Hidden = s.Hidden & ~EditSteps.Dewarp });
    }
}
