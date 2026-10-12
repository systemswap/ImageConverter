using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageConverter.Controls;
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

    /// <summary>Wechselt bei „Neue Linie“: die Vorschau beendet die aktive Linie.</summary>
    [ObservableProperty] public partial object? DewarpNewLineToken { get; set; }

    /// <summary>Unten in der Leiste: was herauskommt (Größe, Seitenverhältnis) oder was noch fehlt.</summary>
    [ObservableProperty] public partial string DewarpInfo { get; set; } = "";

    private bool HasDewarpLines => DewarpLines?.Any(l => l.Points.Count >= 2) == true;

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
        CropToSelectionCommand.NotifyCanExecuteChanged();
        _ = UpdateDewarpInfoAsync();
    }

    /// <summary>Alle Linien weg (übernommen wird erst mit „Gerade ziehen“).</summary>
    [RelayCommand]
    private void ResetDewarpLines()
    {
        if (DewarpTool) DewarpLines = [];
    }

    /// <summary>Die aktive Linie beenden — der nächste Klick beginnt eine neue (auch mit Enter oder Doppelklick).</summary>
    [RelayCommand]
    private void NewDewarpLine() => DewarpNewLineToken = new object();

    /// <summary>Rezept mit den Linien der Ansicht (Hilfe für Anzeige und Übernehmen).</summary>
    private GeometryRecipe? DewarpedRecipe(IReadOnlyList<WarpLineShape> lines)
    {
        if (_history is not { } h) return null;
        var (pw, ph) = PixelSize();
        return h.Recipe.DewarpedInView(
            lines.Select(l => (l.Kind, (IReadOnlyList<(double X, double Y)>)l.Points.Select(p => (p.X, p.Y)).ToArray())),
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
        var count = lines.Count(l => l.Points.Count >= 2);
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

    /// <summary>„Gerade ziehen“: die Linien ins Rezept — ein Schritt, Strg+Z macht ihn rückgängig.</summary>
    private async Task ApplyDewarpAsync(IReadOnlyList<WarpLineShape> lines)
    {
        if (_history is not { } h || DewarpedRecipe(lines) is not { } next) return;
        var (pw, ph) = PixelSize();
        var count = lines.Count(l => l.Points.Count >= 2);
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
