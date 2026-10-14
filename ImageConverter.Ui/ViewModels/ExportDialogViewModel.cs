using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageConverter.Core.Imaging;
using ImageConverter.Imaging;

namespace ImageConverter.ViewModels;

/// <summary>Eine Zeile im Export-Fenster.</summary>
public partial class ExportItem(string path) : ObservableObject
{
    public string Path { get; } = path;
    public string FileName { get; } = System.IO.Path.GetFileName(path);

    [ObservableProperty] public partial bool Include { get; set; } = true;
    [ObservableProperty] public partial Bitmap? Thumbnail { get; set; }
    [ObservableProperty] public partial string Resolution { get; set; } = "…";
    [ObservableProperty] public partial string Details { get; set; } = "";
    [ObservableProperty] public partial string Status { get; set; } = "";
    [ObservableProperty] public partial bool Failed { get; set; }
    [ObservableProperty] public partial bool Done { get; set; }

    /// <summary>Upscaling muss beim Export noch gerechnet werden (geschätzte Dauer), sonst null.</summary>
    internal TimeSpan? UpscaleEstimate { get; set; }
}

/// <summary>
/// Export mehrerer Bilder: Liste mit Haken, Vorschaubild, Auflösung und Ziel.
/// Jedes Bild wird mit seinem eigenen Stand exportiert (Einzelmodus) bzw. mit dem
/// gemeinsamen, auf das Bild umgerechneten Stand.
/// </summary>
public partial class ExportDialogViewModel : ViewModelBase
{
    private const int ThumbSize = 72;

    private readonly MainViewModel _main;
    private readonly CancellationTokenSource _closing = new();
    private bool _cancelRequested;
    private CancellationTokenSource? _exportCts;   // bricht auch ein laufendes Upscaling ab

    /// <summary>Rückfrage (modal über diesem Fenster); Ergebnis: Nummer der Antwort, −1 = geschlossen.</summary>
    public event Func<ChoiceRequest, Task<int>>? ChoiceRequested;

    public ExportDialogViewModel(MainViewModel main)
    {
        _main = main;
        Items = new ObservableCollection<ExportItem>(main.OpenFiles.Select(p => new ExportItem(p)));
        foreach (var item in Items)
            item.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ExportItem.Include)) NotifySelection();
            };
        Destination = main.OutputFolderDisplay;
    }

    public ObservableCollection<ExportItem> Items { get; }
    public string Destination { get; }
    public string ModeHint => _main.ApplyToAll
        ? Tr.T("Gleiche Einstellungen für alle — Größe passend zu jedem Bild umgerechnet.")
        : Tr.T("Jedes Bild mit seinen eigenen Einstellungen.");

    public int SelectedCount => Items.Count(i => i.Include);
    public string ExportButtonText => SelectedCount == 1 ? Tr.T("1 Bild exportieren") : Tr.T("{0} Bilder exportieren", SelectedCount);

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand))]
    public partial bool IsExporting { get; set; }

    [ObservableProperty] public partial bool IsDone { get; set; }
    [ObservableProperty] public partial double Progress { get; set; }

    /// <summary>Was gerade passiert: Bild n von m und der Schritt darin.</summary>
    [ObservableProperty] public partial string Step { get; set; } = "";
    [ObservableProperty] public partial string Summary { get; set; } = "";

    private void NotifySelection()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(ExportButtonText));
        ExportCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void SelectAll() { foreach (var i in Items) i.Include = true; }

    [RelayCommand]
    private void SelectNone() { foreach (var i in Items) i.Include = false; }

    /// <summary>Vorschaubilder und Auflösungen nacheinander im Hintergrund füllen.</summary>
    public async Task LoadAsync()
    {
        foreach (var item in Items)
        {
            if (_closing.IsCancellationRequested) return;
            try
            {
                var (image, owned, _, dpi) = await _main.GetImageAsync(item.Path);
                try
                {
                    var w = (int)image.Width;
                    var h = (int)image.Height;
                    var state = _main.SettingsFor(item.Path, w, h, dpi);
                    var options = MainViewModel.OptionsFor(state);
                    var (_, fileName) = _main.PlanOutput(item.Path, state, options);

                    item.Resolution = $"{options.Width} × {options.Height} px";
                    item.UpscaleEstimate = _main.UpscaleNeededFor(item.Path, w, h, state, options);
                    item.Details = fileName;

                    var (thumb, tw, th) = await Task.Run(() =>
                    {
                        using var t = ImageOps.CloneOf(image);
                        t.Thumbnail((uint)ThumbSize, (uint)ThumbSize);
                        return (ImageOps.ToDisplayBgra(t), (int)t.Width, (int)t.Height);
                    });
                    item.Thumbnail = BitmapOps.FromBgra(thumb, tw, th);
                }
                finally
                {
                    if (owned) image.Dispose();
                }
            }
            catch (Exception ex)
            {
                item.Resolution = "—";
                item.Status = Tr.T("nicht lesbar");
                item.Failed = true;
                item.Include = false;
                item.Details = ex.Message;
            }
        }
    }

    private bool CanExport => !IsExporting && SelectedCount > 0;

    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task Export()
    {
        IsExporting = true;
        IsDone = false;
        Progress = 0;
        _cancelRequested = false;

        var selected = Items.Where(i => i.Include).ToList();

        // Upscaling (KI) noch zu rechnen: einmal für alle fragen
        var upscale = true;
        var needs = selected.Where(i => i.UpscaleEstimate is not null).ToList();
        if (needs.Count > 0 && ChoiceRequested is { } ask)
        {
            var duration = Core.Ai.Upscale.Upscalers.FormatWithEnd(TimeSpan.FromTicks(needs.Sum(i => i.UpscaleEstimate!.Value.Ticks)), DateTime.Now);
            var answer = await ask(new ChoiceRequest(Tr.T("Upscaling noch nicht berechnet"),
                needs.Count == 1
                    ? Tr.T("Für 1 Bild ist ein KI-Upscaler gewählt, aber noch nicht berechnet. Das dauert etwa {0}.", duration)
                    : Tr.T("Für {0} Bilder ist ein KI-Upscaler gewählt, aber noch nicht berechnet. Das dauert zusammen etwa {1}.", needs.Count, duration),
                [Tr.T("Jetzt berechnen und speichern (≈ {0})", duration), Tr.T("Ohne KI speichern (nur Filter)"), Tr.T("Abbrechen")]));
            if (answer is < 0 or 2)
            {
                IsExporting = false;
                return;
            }
            upscale = answer == 0;
        }
        _exportCts = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
        var ok = 0;
        long total = 0;
        string? lastFolder = null;

        for (var n = 0; n < selected.Count; n++)
        {
            if (_cancelRequested || _closing.IsCancellationRequested) break;

            var item = selected[n];
            item.Status = Tr.T("wird exportiert …");
            item.Failed = false;
            // Ein Balken über alle Bilder: jedes Bild ein gleich großes Stück, darin seine Schritte
            var index = n;
            Step = Tr.T("Bild {0} von {1} · {2}", index + 1, selected.Count, Tr.T("Laden …"));
            var progress = new Progress<WorkProgress>(p =>
            {
                Progress = (index + p.Fraction) * 100 / selected.Count;
                Step = Tr.T("Bild {0} von {1} · {2}", index + 1, selected.Count, p.Step);
                item.Status = p.Step;
            });
            try
            {
                var (path, bytes) = await _main.ExportFileAsync(item.Path, progress, upscale, _exportCts.Token);
                item.Status = $"✓  {FormatBytes(bytes)}";
                item.Details = Path.GetFileName(path);   // tatsächlicher Name, ggf. mit „ (2)"
                item.Done = true;
                lastFolder = Path.GetDirectoryName(path);
                total += bytes;
                ok++;
            }
            catch (OperationCanceledException)
            {
                item.Status = Tr.T("abgebrochen");
                break;
            }
            catch (Exception ex)
            {
                item.Status = Tr.T("Fehler: {0}", ex.Message);
                item.Failed = true;
            }

            Progress = (n + 1) * 100.0 / selected.Count;
        }

        var failed = selected.Count(i => i.Failed);
        Summary = _cancelRequested
            ? Tr.T("Abgebrochen — {0} von {1} exportiert.", ok, selected.Count)
            : failed > 0
                ? Tr.T("{0} exportiert, {1} fehlgeschlagen.", ok, failed)
                : ok == 1 ? Tr.T("Fertig — 1 Bild exportiert ({0}).", FormatBytes(total)) : Tr.T("Fertig — {0} Bilder exportiert ({1}).", ok, FormatBytes(total));

        if (lastFolder is not null)
            _main.ReportExport(lastFolder, Summary);

        IsExporting = false;
        IsDone = true;
    }

    /// <summary>Beendet nach dem Bild, das gerade läuft — ein laufendes Upscaling sofort.</summary>
    [RelayCommand]
    private void CancelExport()
    {
        _cancelRequested = true;
        _exportCts?.Cancel();
    }

    /// <summary>Fenster wird geschlossen: Laden der Vorschaubilder beenden.</summary>
    public void OnClosing()
    {
        _cancelRequested = true;
        _closing.Cancel();
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024 * 1024 => $"{bytes / 1024.0 / 1024.0:F1} MB",
        >= 1024 => $"{bytes / 1024.0:F0} KB",
        _ => $"{bytes} B",
    };
}
